interface Reaction {
  id: string;
  label: string;
  emoji?: string;
  image?: string;
}

type ServerMessage =
  | { type: 'hello'; name: string; presenterOnline: boolean }
  | { type: 'presenter'; online: boolean }
  | { type: 'slow-down' }
  | { type: 'busy' };

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const screens = {
  loading: $('loading-screen'),
  name: $('name-screen'),
  react: $('react-screen'),
};
const nameForm = $<HTMLFormElement>('name-form');
const nameInput = $<HTMLInputElement>('name-input');
const nameError = $('name-error');
const who = $('who');
const grid = $('grid');
const statusEl = $('status');
const statusText = $('status-text');
const toastEl = $('toast');
const sky = $('sky');

const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

let reactions: Reaction[] = [];
let socket: WebSocket | null = null;
let presenterOnline = false;
let reconnectTimer: number | undefined;
let reconnectAttempt = 0;
let wantConnection = false;
let blockedUntil = 0;

// Mirrors the server's per-connection limit (5 burst, 2/s) but a little stricter, so a
// legitimate user tapping fast is slowed down locally and never trips server-side strikes.
const SEND_BURST = 4;
const SEND_PER_SECOND = 1.6;
let sendTokens = SEND_BURST;
let sendRefilledAt = performance.now();

function takeSendToken(): boolean {
  const now = performance.now();
  sendTokens = Math.min(SEND_BURST, sendTokens + ((now - sendRefilledAt) / 1000) * SEND_PER_SECOND);
  sendRefilledAt = now;
  if (sendTokens < 1) return false;
  sendTokens -= 1;
  return true;
}

function show(screen: keyof typeof screens) {
  for (const [key, el] of Object.entries(screens)) el.hidden = key !== screen;
}

async function init() {
  try {
    const res = await fetch('/api/session', { credentials: 'same-origin' });
    const data = (await res.json()) as { name: string | null; reactions: Reaction[] };
    reactions = data.reactions;
    renderGrid();
    if (data.name) enterReactions(data.name);
    else showNameScreen();
  } catch {
    showNameScreen();
    nameError.textContent = 'Could not reach the server. Check your connection and refresh.';
    nameError.hidden = false;
  }
}

function showNameScreen(prefill = '') {
  disconnect();
  nameInput.value = prefill;
  show('name');
  setTimeout(() => nameInput.focus(), 50);
}

function enterReactions(name: string) {
  who.textContent = name;
  show('react');
  connect();
}

nameForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const name = nameInput.value.trim();
  if (!name) return;
  const button = nameForm.querySelector('button') as HTMLButtonElement;
  button.disabled = true;
  nameError.hidden = true;
  try {
    const res = await fetch('/api/name', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify({ name }),
    });
    const data = (await res.json()) as { name?: string; error?: string };
    if (!res.ok || !data.name) throw new Error(data.error ?? 'Something went wrong.');
    nameInput.blur();
    enterReactions(data.name);
  } catch (err) {
    nameError.textContent = err instanceof Error ? err.message : 'Something went wrong.';
    nameError.hidden = false;
  } finally {
    button.disabled = false;
  }
});

$('change-name').addEventListener('click', () => showNameScreen(who.textContent ?? ''));

function renderGrid() {
  grid.replaceChildren(
    ...reactions.map((r) => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'reaction';
      btn.setAttribute('aria-label', r.label);
      btn.append(reactionContent(r));
      btn.addEventListener('click', () => sendReaction(r, btn));
      return btn;
    }),
  );
}

function reactionContent(r: Reaction): Node {
  if (r.image) {
    const img = document.createElement('img');
    img.src = r.image;
    img.alt = r.label;
    img.draggable = false;
    return img;
  }
  return document.createTextNode(r.emoji ?? '?');
}

function sendReaction(r: Reaction, btn: HTMLElement) {
  if (Date.now() < blockedUntil) {
    shake(btn);
    toast('Too many reactions – paused for a bit');
    return;
  }
  if (!socket || socket.readyState !== WebSocket.OPEN) {
    shake(btn);
    toast('Reconnecting… try again in a sec');
    return;
  }
  if (!takeSendToken()) {
    shake(btn);
    toast('Whoa, slow down! 🐢');
    return;
  }
  socket.send(JSON.stringify({ type: 'react', id: r.id }));
  navigator.vibrate?.(12);
  floatUp(r, btn);
}

function shake(btn: HTMLElement) {
  btn.classList.remove('shake');
  void btn.offsetWidth;
  btn.classList.add('shake');
}

// ---------- WebSocket ----------

function connect() {
  wantConnection = true;
  if (socket && socket.readyState <= WebSocket.OPEN) return;
  setStatus('connecting');
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const ws = new WebSocket(`${proto}//${location.host}/ws`);
  socket = ws;

  ws.addEventListener('open', () => {
    reconnectAttempt = 0;
  });
  ws.addEventListener('message', (ev) => {
    let msg: ServerMessage;
    try {
      msg = JSON.parse(ev.data as string);
    } catch {
      return;
    }
    switch (msg.type) {
      case 'hello':
        presenterOnline = msg.presenterOnline;
        break;
      case 'presenter':
        presenterOnline = msg.online;
        break;
      case 'slow-down':
        toast('Whoa, slow down! 🐢');
        break;
      case 'busy':
        toast('Lots of reactions right now – try again in a sec');
        break;
    }
    setStatus('connected');
  });
  ws.addEventListener('close', (ev) => {
    if (socket !== ws) return;
    socket = null;
    if (!wantConnection) return;
    const banned = /^banned:(\d+)$/.exec(ev.reason);
    if (ev.code === 1008 && banned) {
      blockUntil(Number(banned[1]));
      return;
    }
    // 1006 before ever opening: rejected upgrade (no name cookie → 401, or rate limited → 429).
    if (ev.code === 1006 && reconnectAttempt >= 3) {
      void recheckSession();
    }
    setStatus('offline');
    scheduleReconnect();
  });
}

function blockUntil(seconds: number) {
  blockedUntil = Date.now() + seconds * 1000;
  clearTimeout(reconnectTimer);
  setStatus('blocked');
  reconnectAttempt = 0;
  reconnectTimer = window.setTimeout(() => {
    blockedUntil = 0;
    connect();
  }, seconds * 1000 + 500);
}

async function recheckSession() {
  try {
    const res = await fetch('/api/session', { credentials: 'same-origin' });
    const data = (await res.json()) as { name: string | null; retryAfter?: number };
    if (data.retryAfter) blockUntil(data.retryAfter);
    else if (!data.name) showNameScreen();
  } catch {
    /* still offline */
  }
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  const delay = Math.min(15000, 500 * 2 ** reconnectAttempt) + Math.random() * 400;
  reconnectAttempt++;
  reconnectTimer = window.setTimeout(connect, delay);
}

function disconnect() {
  wantConnection = false;
  clearTimeout(reconnectTimer);
  const ws = socket;
  socket = null;
  ws?.close();
}

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible' && wantConnection && !socket) {
    reconnectAttempt = 0;
    connect();
  }
});

function setStatus(state: 'connecting' | 'connected' | 'offline' | 'blocked') {
  if (state === 'connected') {
    statusEl.dataset.state = presenterOnline ? 'live' : 'connecting';
    statusText.textContent = presenterOnline ? 'Live on the big screen' : 'Connected – waiting for the presenter';
  } else if (state === 'blocked') {
    statusEl.dataset.state = 'offline';
    const minutes = Math.max(1, Math.ceil((blockedUntil - Date.now()) / 60000));
    statusText.textContent = `Too many requests – try again in ${minutes} min`;
  } else {
    statusEl.dataset.state = state;
    statusText.textContent = state === 'connecting' ? 'Connecting…' : 'Reconnecting…';
  }
}

let toastTimer: number | undefined;
function toast(text: string) {
  toastEl.textContent = text;
  toastEl.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => (toastEl.hidden = true), 2000);
}

// ---------- Floating animation ----------

const rand = (min: number, max: number) => min + Math.random() * (max - min);
const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));

/**
 * Floats a reaction from the bottom of the screen to the top like a leaf on a slow
 * breeze: a gentle rise with side-to-side sway, a little up/down "bubbling" bob,
 * and a slight tilt in the direction it's drifting.
 */
function floatUp(r: Reaction, from: HTMLElement) {
  const W = window.innerWidth;
  const H = window.innerHeight;
  const size = Math.round(clamp(W * 0.2, 64, 110));

  const el = document.createElement('div');
  el.className = 'floater';
  el.style.width = el.style.height = `${size}px`;
  el.style.fontSize = `${Math.round(size * 0.85)}px`;
  el.append(reactionContent(r));
  sky.append(el);

  const motion = reduceMotion ? 0 : 1;
  const swayAmp = rand(18, 32) * motion;
  const swayFreq = rand(0.22, 0.32);
  const bobAmp = rand(5, 9) * motion;
  const bobFreq = rand(0.8, 1.2);
  const phase = rand(0, Math.PI * 2);
  const phase2 = rand(0, Math.PI * 2);
  const duration = rand(5200, 6400);

  const rect = from.getBoundingClientRect();
  const margin = size / 2 + swayAmp * 1.3;
  const baseX = clamp(rect.left + rect.width / 2 + rand(-24, 24), margin, W - margin);
  const startY = H + size * 0.6;
  const endY = -size * 0.8;

  const start = performance.now();
  const step = (now: number) => {
    const elapsed = now - start;
    const t = elapsed / duration;
    if (t >= 1) {
      el.remove();
      return;
    }
    const s = elapsed / 1000;
    // A soft ease-in so it starts lazily and picks up with the breeze.
    const p = 0.7 * t + 0.3 * (0.5 - 0.5 * Math.cos(Math.PI * t));
    const sway = Math.sin(2 * Math.PI * swayFreq * s + phase) + 0.3 * Math.sin(2 * Math.PI * swayFreq * 2.3 * s + phase2);
    const x = baseX + swayAmp * sway;
    const y = startY + (endY - startY) * p + bobAmp * Math.sin(2 * Math.PI * bobFreq * s + phase2);
    const tilt = 12 * motion * Math.cos(2 * Math.PI * swayFreq * s + phase);
    const pop = s < 0.45 ? 0.55 + 0.45 * easeOutBack(s / 0.45) : 1;
    const opacity = Math.min(1, t / 0.06, (1 - t) / 0.15);

    el.style.transform = `translate3d(${x - size / 2}px, ${y - size / 2}px, 0) rotate(${tilt}deg) scale(${pop})`;
    el.style.opacity = String(clamp(opacity, 0, 1));
    requestAnimationFrame(step);
  };
  requestAnimationFrame(step);
}

function easeOutBack(x: number) {
  const c1 = 1.70158;
  const c3 = c1 + 1;
  return 1 + c3 * Math.pow(x - 1, 3) + c1 * Math.pow(x - 1, 2);
}

void init();
