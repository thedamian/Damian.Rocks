import { randomUUID } from 'node:crypto';
import { STATUS_CODES, type IncomingMessage, type Server } from 'node:http';
import type { Duplex } from 'node:stream';
import { WebSocketServer, WebSocket, type RawData } from 'ws';
import { limits, TokenBucket, type Protection } from './protection.js';
import { Questions, type Hand } from './questions.js';
import { findReaction, REACTIONS } from './reactions.js';
import { getSession } from './session.js';
import { iceServersFor, isPresenter, presenterKeyConfigured } from './webrtc.js';

const HEARTBEAT_MS = 25_000;
/** Ordinary messages are tiny; only WebRTC session descriptions are allowed to be bigger. */
const SMALL_MESSAGE_BYTES = 1024;
const MAX_SDP_LENGTH = 12_000;

// Strike weights: only things the official web client never does.
const STRIKE_OVER_CONN_LIMIT = 1;
const STRIKE_INVALID_MESSAGE = 2;
const STRIKE_MESSAGE_FLOOD = 5;

interface AudienceMember {
  ws: WebSocket;
  name: string;
  ipKey: string;
  sessionKey: string;
  reactions: TokenBucket;
  messages: TokenBucket;
  handToggles: TokenBucket;
  offers: TokenBucket;
}

interface Display {
  ws: WebSocket;
  ipKey: string;
  presenter: boolean;
  messages: TokenBucket;
}

type AudienceMessage =
  | { type: 'react'; id: unknown }
  | { type: 'hand'; raised: unknown }
  | { type: 'rtc-offer'; sdp: unknown }
  | { type: 'talking'; on: unknown };

type DisplayMessage =
  | { type: 'pick'; id: unknown }
  | { type: 'lower'; id: unknown }
  | { type: 'rtc-answer'; id: unknown; sdp: unknown };

export interface RealtimeStats {
  audience: number;
  displays: number;
  hands: number;
}

export function attachRealtime(server: Server, protection: Protection): () => RealtimeStats {
  const wss = new WebSocketServer({ noServer: true, maxPayload: 16 * 1024 });
  const audience = new Set<AudienceMember>();
  const sessions = new Map<string, Set<AudienceMember>>();
  const displays = new Set<Display>();
  const alive = new WeakMap<WebSocket, boolean>();
  const questions = new Questions();
  /** The presenter display that called on the active speaker; it receives their audio. */
  let controller: Display | null = null;
  /** The speaker's connection that sent the latest offer; the answer goes back to it. */
  let speakerConn: AudienceMember | null = null;

  if (!presenterKeyConfigured) {
    console.warn('[config] DISPLAY_KEY is not set: only a desktop app on this machine can take questions');
  }

  const send = (ws: WebSocket, msg: unknown) => {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
  };
  const toDisplays = (msg: unknown) => displays.forEach((d) => send(d.ws, msg));
  const toAudience = (msg: unknown) => audience.forEach((m) => send(m.ws, msg));
  const toSession = (sessionKey: string, msg: unknown) => sessions.get(sessionKey)?.forEach((m) => send(m.ws, msg));
  const audienceStats = () => toDisplays({ type: 'stats', audience: audience.size });
  const presenterStatus = () => toAudience({ type: 'presenter', online: displays.size > 0 });
  const broadcastHands = () => toDisplays({ type: 'hands', hands: questions.list() });

  /** Tells a hand's owner where they stand: waiting (with place in line) or on the mic. */
  const handState = (hand: Hand) =>
    questions.isActive(hand)
      ? { type: 'hand', state: 'active' }
      : { type: 'hand', state: 'waiting', position: questions.position(hand) };

  /** Re-sends every waiting hand its place in line (it changes whenever someone ahead leaves). */
  const refreshQueue = () => {
    broadcastHands();
    for (const hand of questions.list()) {
      const h = questions.byId(hand.id);
      if (h && !hand.active) toSession(h.sessionKey, handState(h));
    }
  };

  const closeCall = (hand: Hand | undefined) => {
    speakerConn = null;
    if (hand && controller) send(controller.ws, { type: 'rtc-close', id: hand.id });
  };

  const lowerHand = (sessionKey: string, reason: 'lowered' | 'ended' | 'dismissed' | 'expired') => {
    const { hand, wasActive } = questions.lower(sessionKey);
    if (!hand) return;
    if (wasActive) {
      closeCall(hand);
      controller = null;
    }
    toSession(sessionKey, { type: 'hand', state: 'none', reason });
    console.log(`[hand] ${hand.name} (${hand.ipKey}) ${reason}`);
    refreshQueue();
  };

  server.on('upgrade', (req: IncomingMessage, socket: Duplex, head: Buffer) => {
    socket.on('error', () => socket.destroy());
    const { pathname } = new URL(req.url ?? '/', 'http://localhost');
    if (pathname !== '/ws' && pathname !== '/display') return reject(socket, 404);

    const ipKey = protection.clientKey(req);
    const admission = protection.admitConnection(ipKey);
    if (!admission.ok) return reject(socket, admission.status, admission.retryAfter);

    if (pathname === '/ws') {
      if (!isSameOrigin(req)) return reject(socket, 403);
      const session = getSession(req);
      if (!session.name) return reject(socket, 401);
      const name = session.name;
      const sessionKey = session.id ?? `conn:${randomUUID()}`;
      wss.handleUpgrade(req, socket, head, (ws) => {
        protection.register(ipKey, ws);
        onAudience(ws, name, ipKey, sessionKey);
      });
    } else {
      const presenter = isPresenter(req, ipKey);
      wss.handleUpgrade(req, socket, head, (ws) => {
        protection.register(ipKey, ws);
        onDisplay(ws, ipKey, presenter);
      });
    }
  });

  // ---------- audience ----------

  function onAudience(ws: WebSocket, name: string, ipKey: string, sessionKey: string) {
    const member: AudienceMember = {
      ws,
      name,
      ipKey,
      sessionKey,
      reactions: new TokenBucket(limits.connReactionBurst, limits.connReactionPerSec),
      messages: new TokenBucket(limits.connMessageBurst, limits.connMessagePerSec),
      handToggles: new TokenBucket(4, 0.2),
      offers: new TokenBucket(4, 0.1),
    };
    audience.add(member);
    let group = sessions.get(sessionKey);
    if (!group) sessions.set(sessionKey, (group = new Set()));
    group.add(member);
    alive.set(ws, true);
    ws.on('pong', () => alive.set(ws, true));

    send(ws, { type: 'hello', name, presenterOnline: displays.size > 0, iceServers: iceServersFor(sessionKey) });
    const hand = questions.get(sessionKey);
    if (hand) {
      if (!hand.online) {
        questions.setOnline(hand, true);
        broadcastHands();
      }
      send(ws, handState(hand));
    }
    audienceStats();

    ws.on('message', (data: RawData, isBinary: boolean) => onAudienceMessage(member, data, isBinary));
    ws.on('close', () => onAudienceClose(member));
    ws.on('error', () => ws.terminate());
  }

  function onAudienceClose(member: AudienceMember) {
    audience.delete(member);
    const group = sessions.get(member.sessionKey);
    group?.delete(member);
    const hand = questions.get(member.sessionKey);
    if (speakerConn === member) closeCall(hand);
    if (!group?.size) {
      sessions.delete(member.sessionKey);
      if (hand) {
        questions.setOnline(hand, false);
        broadcastHands();
      }
    }
    audienceStats();
  }

  function onAudienceMessage(member: AudienceMember, data: RawData, isBinary: boolean) {
    const { ws, ipKey } = member;
    if (ws.readyState !== WebSocket.OPEN) return;

    // Raw message flood (anything at all, valid or not): drop the connection.
    if (!member.messages.take()) {
      if (!protection.strike(ipKey, STRIKE_MESSAGE_FLOOD, 'message flood')) ws.close(1008, 'Too many messages');
      return;
    }

    const msg = isBinary ? undefined : parseJson<AudienceMessage>(data);
    const size = rawSize(data);
    const hand = questions.get(member.sessionKey);
    const isSpeaker = questions.isActive(hand);
    // A hand that was active a moment ago may still have an offer in flight, so allow any raised hand.
    const validSize = size <= SMALL_MESSAGE_BYTES || (msg?.type === 'rtc-offer' && !!hand);
    if (!msg || !validSize) {
      protection.strike(ipKey, STRIKE_INVALID_MESSAGE, 'invalid message');
      return;
    }

    switch (msg.type) {
      case 'react':
        return onReact(member, msg.id);
      case 'hand':
        return onHand(member, msg.raised);
      case 'rtc-offer':
        return onOffer(member, hand, msg.sdp);
      case 'talking':
        if (isSpeaker && hand && typeof msg.on === 'boolean' && hand.talking !== msg.on) {
          hand.talking = msg.on;
          broadcastHands();
        }
        return;
      default:
        protection.strike(ipKey, STRIKE_INVALID_MESSAGE, 'unknown message');
    }
  }

  function onReact(member: AudienceMember, id: unknown) {
    const reaction = findReaction(id);
    if (!reaction) {
      protection.strike(member.ipKey, STRIKE_INVALID_MESSAGE, 'invalid reaction');
      return;
    }
    if (!member.reactions.take()) {
      if (!protection.strike(member.ipKey, STRIKE_OVER_CONN_LIMIT, 'over per-connection reaction limit')) {
        send(member.ws, { type: 'slow-down' });
      }
      return;
    }
    if (protection.allowReaction(member.ipKey) !== 'ok') {
      // Probably shared Wi-Fi/NAT neighbours or a busy room: tell them, but no strike.
      send(member.ws, { type: 'busy' });
      return;
    }
    toDisplays({ type: 'reaction', id: reaction.id, emoji: reaction.emoji, image: reaction.image });
    console.log(`[react] ${member.name} (${member.ipKey}): ${reaction.emoji ?? reaction.id}`);
  }

  function onHand(member: AudienceMember, raised: unknown) {
    if (typeof raised !== 'boolean') {
      protection.strike(member.ipKey, STRIKE_INVALID_MESSAGE, 'invalid hand message');
      return;
    }
    if (!member.handToggles.take()) {
      send(member.ws, { type: 'slow-down' });
      return;
    }
    if (!raised) return lowerHand(member.sessionKey, 'lowered');

    const result = questions.raise(member.sessionKey, member.ipKey, member.name);
    if (!result.ok) {
      send(member.ws, { type: 'hand', state: 'none', reason: 'full' });
      return;
    }
    console.log(`[hand] ${member.name} (${member.ipKey}) raised`);
    toSession(member.sessionKey, handState(result.hand));
    broadcastHands();
  }

  function onOffer(member: AudienceMember, hand: Hand | undefined, sdp: unknown) {
    if (!hand || !isSdp(sdp)) {
      protection.strike(member.ipKey, STRIKE_INVALID_MESSAGE, 'unexpected rtc-offer');
      return;
    }
    if (!questions.isActive(hand) || !controller) return; // the presenter just ended or switched the call
    if (!member.offers.take()) {
      protection.strike(member.ipKey, STRIKE_OVER_CONN_LIMIT, 'rtc-offer flood');
      return;
    }
    speakerConn = member;
    send(controller.ws, { type: 'rtc-offer', id: hand.id, name: hand.name, sdp });
  }

  // ---------- displays (desktop overlay) ----------

  function onDisplay(ws: WebSocket, ipKey: string, presenter: boolean) {
    const display: Display = {
      ws,
      ipKey,
      presenter,
      messages: new TokenBucket(limits.connMessageBurst, limits.connMessagePerSec),
    };
    displays.add(display);
    alive.set(ws, true);
    ws.on('pong', () => alive.set(ws, true));
    console.log(`[display] connected from ${ipKey}${presenter ? ' (presenter)' : ' (view only)'}, ${displays.size} total`);
    send(ws, {
      type: 'hello',
      audience: audience.size,
      reactions: REACTIONS,
      presenter,
      iceServers: presenter ? iceServersFor('presenter') : [],
    });
    send(ws, { type: 'hands', hands: questions.list() });
    if (displays.size === 1) presenterStatus();

    ws.on('message', (data: RawData, isBinary: boolean) => onDisplayMessage(display, data, isBinary));
    ws.on('close', () => {
      displays.delete(display);
      console.log(`[display] disconnected (${displays.size} total)`);
      if (controller === display) {
        // The presenter's app went away mid-call: put the speaker back in line.
        controller = null;
        speakerConn = null;
        const previous = questions.deactivate();
        if (previous) toSession(previous.sessionKey, handState(previous));
        refreshQueue();
      }
      if (displays.size === 0) presenterStatus();
    });
    ws.on('error', () => ws.terminate());
  }

  function onDisplayMessage(display: Display, data: RawData, isBinary: boolean) {
    if (!display.messages.take()) {
      display.ws.close(1008, 'Too many messages');
      return;
    }
    // View-only displays (no presenter key) can't control anything.
    if (!display.presenter || isBinary) return;
    const msg = parseJson<DisplayMessage>(data);
    if (!msg) return;

    switch (msg.type) {
      case 'pick': {
        const hand = questions.byId(msg.id);
        if (!hand) return send(display.ws, { type: 'hands', hands: questions.list() });
        if (questions.isActive(hand) && controller === display) return;
        const previous = questions.active;
        closeCall(previous);
        questions.deactivate();
        questions.activate(hand);
        controller = display;
        if (previous && previous !== hand) toSession(previous.sessionKey, handState(previous));
        console.log(`[hand] ${hand.name} called on`);
        toSession(hand.sessionKey, handState(hand));
        refreshQueue();
        return;
      }
      case 'lower': {
        const hand = questions.byId(msg.id);
        if (hand) lowerHand(hand.sessionKey, questions.isActive(hand) ? 'ended' : 'dismissed');
        return;
      }
      case 'rtc-answer': {
        const hand = questions.byId(msg.id);
        if (!hand || !questions.isActive(hand) || controller !== display || !speakerConn || !isSdp(msg.sdp)) return;
        send(speakerConn.ws, { type: 'rtc-answer', sdp: msg.sdp });
        return;
      }
    }
  }

  const heartbeat = setInterval(() => {
    for (const ws of wss.clients) {
      if (alive.get(ws) === false) {
        ws.terminate();
        continue;
      }
      alive.set(ws, false);
      ws.ping();
    }
  }, HEARTBEAT_MS);
  const expiry = setInterval(() => {
    const expired = questions.expired();
    for (const hand of expired) lowerHand(hand.sessionKey, 'expired');
  }, 5_000);
  server.on('close', () => {
    clearInterval(heartbeat);
    clearInterval(expiry);
  });

  return () => ({ audience: audience.size, displays: displays.size, hands: questions.size });
}

function rawSize(data: RawData): number {
  if (Array.isArray(data)) return data.reduce((n, b) => n + b.length, 0);
  return data instanceof ArrayBuffer ? data.byteLength : data.length;
}

function parseJson<T extends { type: string }>(data: RawData): T | undefined {
  try {
    const msg: unknown = JSON.parse(data.toString());
    return msg && typeof msg === 'object' && typeof (msg as { type?: unknown }).type === 'string' ? (msg as T) : undefined;
  } catch {
    return undefined;
  }
}

function isSdp(sdp: unknown): sdp is string {
  return typeof sdp === 'string' && sdp.length <= MAX_SDP_LENGTH && sdp.startsWith('v=0');
}

function isSameOrigin(req: IncomingMessage): boolean {
  const origin = req.headers.origin;
  if (!origin) return true;
  try {
    return new URL(origin).host === req.headers.host;
  } catch {
    return false;
  }
}

function reject(socket: Duplex, code: number, retryAfter?: number) {
  const extra = retryAfter ? `Retry-After: ${retryAfter}\r\n` : '';
  socket.end(`HTTP/1.1 ${code} ${STATUS_CODES[code]}\r\nConnection: close\r\n${extra}Content-Length: 0\r\n\r\n`);
  setTimeout(() => socket.destroy(), 1000).unref();
}
