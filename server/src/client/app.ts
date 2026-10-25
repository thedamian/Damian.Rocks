interface Reaction {
  id: string;
  label: string;
  emoji?: string;
  image?: string;
}

type HandState = 'none' | 'waiting' | 'active';
type HandReason = 'lowered' | 'ended' | 'dismissed' | 'expired' | 'full';

type ServerMessage =
  | { type: 'hello'; name: string; presenterOnline: boolean; iceServers?: RTCIceServer[] }
  | { type: 'presenter'; online: boolean }
  | { type: 'hand'; state: HandState; position?: number; reason?: HandReason }
  | { type: 'rtc-answer'; sdp: string }
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
const askButton = $<HTMLButtonElement>('ask-button');
const askSheet = $('ask-sheet');
const handPanel = $('hand-panel');
const handTitle = $('hand-title');
const handDetail = $('hand-detail');
const speakButton = $<HTMLButtonElement>('speak-button');
const handDownButton = $<HTMLButtonElement>('hand-down');

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
        if (Array.isArray(msg.iceServers)) iceServers = msg.iceServers;
        // No hand message follows the hello if our hand isn't up (any more).
        if (handState !== 'none') applyHand({ state: 'none' });
        break;
      case 'presenter':
        presenterOnline = msg.online;
        break;
      case 'hand':
        applyHand(msg);
        break;
      case 'rtc-answer':
        void onAnswer(msg.sdp);
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
function toast(text: string, ms = 2000) {
  toastEl.textContent = text;
  toastEl.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => (toastEl.hidden = true), ms);
}

// ---------- Ask a question (raised hand) ----------

const HAND: Reaction = { id: 'hand', label: 'Raised hand', emoji: '🖐🏽' };
let handState: HandState = 'none';
let iceServers: RTCIceServer[] = [{ urls: 'stun:stun.l.google.com:19302' }];

function socketOpen(): WebSocket | null {
  return socket && socket.readyState === WebSocket.OPEN ? socket : null;
}

askButton.addEventListener('click', () => {
  if (!socketOpen()) {
    shake(askButton);
    toast('Reconnecting… try again in a sec');
    return;
  }
  askSheet.hidden = false;
  $('ask-yes').focus();
});

function closeSheet() {
  askSheet.hidden = true;
}
$('ask-no').addEventListener('click', closeSheet);
askSheet.addEventListener('click', (e) => {
  if (e.target === askSheet) closeSheet();
});
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && !askSheet.hidden) closeSheet();
});

$('ask-yes').addEventListener('click', () => {
  closeSheet();
  const ws = socketOpen();
  if (!ws) {
    toast('Reconnecting… try again in a sec');
    return;
  }
  ws.send(JSON.stringify({ type: 'hand', raised: true }));
  navigator.vibrate?.(20);
  floatUp(HAND, askButton);
});

handDownButton.addEventListener('click', () => {
  const ws = socketOpen();
  if (!ws) {
    toast('Reconnecting… try again in a sec');
    return;
  }
  ws.send(JSON.stringify({ type: 'hand', raised: false }));
  // Optimistic: stop the mic right away rather than waiting for the server.
  stopCall();
});

function applyHand(msg: { state: HandState; position?: number; reason?: HandReason }) {
  const previous = handState;
  handState = msg.state;
  askButton.hidden = msg.state !== 'none';
  handPanel.hidden = msg.state === 'none';
  handPanel.dataset.state = msg.state;

  if (msg.state === 'none') {
    stopCall();
    const message: Partial<Record<HandReason, string>> = {
      ended: 'Thanks for your question! 🙌',
      dismissed: 'The presenter lowered your hand',
      expired: 'Your hand was lowered',
      full: 'Too many questions right now – try again later',
    };
    if (msg.reason && message[msg.reason]) toast(message[msg.reason]!, 3500);
    return;
  }

  if (msg.state === 'waiting') {
    stopCall();
    speakButton.hidden = true;
    handTitle.textContent = 'Your hand is up';
    handDetail.textContent = msg.position
      ? `You're #${msg.position} in line. Hang tight — the presenter will call on you.`
      : 'Hang tight — the presenter will call on you.';
    if (previous === 'active') toast('Back in line – your hand is still up', 3000);
    return;
  }

  handTitle.textContent = "You're on! 🎤";
  handDetail.textContent = "Hold the button while you talk. Your voice plays on the presenter's speakers.";
  speakButton.hidden = false;
  if (previous !== 'active') navigator.vibrate?.([30, 60, 30]);
  // Called on (or re-called after a reconnect / presenter switch): always start a fresh call.
  void startCall();
}

// ---------- Push-to-talk audio (WebRTC, phone → presenter's computer) ----------

type SpeakState = 'connecting' | 'ready' | 'talking' | 'blocked' | 'failed' | 'unsupported';
let pc: RTCPeerConnection | null = null;
let mic: MediaStream | null = null;
let callId = 0;
let answerTimer: number | undefined;
let talking = false;

const speakLabels: Record<SpeakState, string> = {
  connecting: 'Connecting mic…',
  ready: '🎙️ Hold to speak',
  talking: '🔴 Speaking… release to stop',
  blocked: 'Microphone blocked – tap to allow',
  failed: "Couldn't connect – tap to retry",
  unsupported: 'This browser can’t send audio (needs https)',
};

function setSpeak(state: SpeakState) {
  speakButton.dataset.state = state;
  speakButton.textContent = speakLabels[state];
}

async function startCall() {
  const id = ++callId;
  closePeer();
  setSpeak('connecting');

  if (!window.RTCPeerConnection || !navigator.mediaDevices?.getUserMedia) {
    setSpeak('unsupported');
    return;
  }

  try {
    if (!mic || mic.getAudioTracks().every((t) => t.readyState === 'ended')) {
      mic = await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
        video: false,
      });
    }
  } catch (err) {
    if (id !== callId) return;
    const name = err instanceof DOMException ? err.name : '';
    setSpeak(name === 'NotAllowedError' || name === 'SecurityError' ? 'blocked' : 'failed');
    return;
  }
  if (id !== callId || handState !== 'active') return;

  const track = mic.getAudioTracks()[0];
  track.enabled = false;
  const peer = new RTCPeerConnection({ iceServers });
  pc = peer;
  peer.addTransceiver(track, { direction: 'sendonly', streams: [mic] });

  const onState = () => {
    if (pc !== peer) return;
    const state = peer.connectionState ?? peer.iceConnectionState;
    if (state === 'connected' || peer.iceConnectionState === 'connected' || peer.iceConnectionState === 'completed') {
      clearTimeout(answerTimer);
      if (!talking) setSpeak('ready');
    } else if (state === 'failed') {
      setTalking(false);
      setSpeak('failed');
    }
  };
  peer.addEventListener('connectionstatechange', onState);
  peer.addEventListener('iceconnectionstatechange', onState);

  try {
    await peer.setLocalDescription(await peer.createOffer());
    await iceGatheringDone(peer, 3000);
  } catch {
    if (pc === peer) setSpeak('failed');
    return;
  }
  const ws = socketOpen();
  const sdp = peer.localDescription?.sdp;
  if (pc !== peer || !ws || !sdp) return; // a reconnect will start a fresh call
  ws.send(JSON.stringify({ type: 'rtc-offer', sdp }));
  clearTimeout(answerTimer);
  answerTimer = window.setTimeout(() => {
    if (pc === peer && speakButton.dataset.state === 'connecting') setSpeak('failed');
  }, 15000);
}

function iceGatheringDone(peer: RTCPeerConnection, timeoutMs: number): Promise<void> {
  if (peer.iceGatheringState === 'complete') return Promise.resolve();
  return new Promise((resolve) => {
    const done = () => {
      clearTimeout(timer);
      peer.removeEventListener('icegatheringstatechange', check);
      resolve();
    };
    const check = () => {
      if (peer.iceGatheringState === 'complete') done();
    };
    const timer = window.setTimeout(done, timeoutMs);
    peer.addEventListener('icegatheringstatechange', check);
  });
}

async function onAnswer(sdp: string) {
  const peer = pc;
  if (!peer || peer.signalingState !== 'have-local-offer' || typeof sdp !== 'string') return;
  try {
    await peer.setRemoteDescription({ type: 'answer', sdp });
  } catch {
    if (pc === peer) setSpeak('failed');
  }
}

function closePeer() {
  clearTimeout(answerTimer);
  setTalking(false);
  const peer = pc;
  pc = null;
  peer?.close();
}

/** Hangs up and releases the microphone (so the phone's "mic in use" indicator goes away). */
function stopCall() {
  callId++;
  closePeer();
  mic?.getTracks().forEach((t) => t.stop());
  mic = null;
}

function setTalking(on: boolean) {
  if (talking === on) return;
  talking = on;
  mic?.getAudioTracks().forEach((t) => (t.enabled = on));
  socketOpen()?.send(JSON.stringify({ type: 'talking', on }));
  if (on) {
    navigator.vibrate?.(15);
    setSpeak('talking');
  } else if (speakButton.dataset.state === 'talking') {
    setSpeak('ready');
  }
}

speakButton.addEventListener('pointerdown', (e) => {
  if (speakButton.dataset.state !== 'ready') return;
  e.preventDefault();
  speakButton.setPointerCapture(e.pointerId);
  setTalking(true);
});
for (const type of ['pointerup', 'pointercancel', 'lostpointercapture'] as const) {
  speakButton.addEventListener(type, () => setTalking(false));
}
speakButton.addEventListener('contextmenu', (e) => e.preventDefault());
speakButton.addEventListener('click', () => {
  const state = speakButton.dataset.state;
  // Retrying from a tap counts as a user gesture, which some browsers need for the mic prompt.
  if (state === 'blocked' || state === 'failed') void startCall();
});
speakButton.addEventListener('keydown', (e) => {
  if ((e.key === ' ' || e.key === 'Enter') && !e.repeat && speakButton.dataset.state === 'ready') {
    e.preventDefault();
    setTalking(true);
  }
});
speakButton.addEventListener('keyup', (e) => {
  if (e.key === ' ' || e.key === 'Enter') setTalking(false);
});
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') setTalking(false);
});
window.addEventListener('blur', () => setTalking(false));

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
