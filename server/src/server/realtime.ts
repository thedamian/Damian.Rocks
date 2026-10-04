import { STATUS_CODES, type IncomingMessage, type Server } from 'node:http';
import type { Duplex } from 'node:stream';
import { WebSocketServer, WebSocket, type RawData } from 'ws';
import { limits, TokenBucket, type Protection } from './protection.js';
import { findReaction, REACTIONS } from './reactions.js';
import { getSession } from './session.js';

const HEARTBEAT_MS = 25_000;

// Strike weights: only things the official web client never does.
const STRIKE_OVER_CONN_LIMIT = 1;
const STRIKE_INVALID_MESSAGE = 2;
const STRIKE_MESSAGE_FLOOD = 5;

interface AudienceMember {
  ws: WebSocket;
  name: string;
  ipKey: string;
  reactions: TokenBucket;
  messages: TokenBucket;
}

export interface RealtimeStats {
  audience: number;
  displays: number;
}

export function attachRealtime(server: Server, protection: Protection): () => RealtimeStats {
  const wss = new WebSocketServer({ noServer: true, maxPayload: 1024 });
  const audience = new Set<AudienceMember>();
  const displays = new Set<WebSocket>();
  const alive = new WeakMap<WebSocket, boolean>();

  const send = (ws: WebSocket, msg: unknown) => {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
  };
  const toDisplays = (msg: unknown) => displays.forEach((ws) => send(ws, msg));
  const toAudience = (msg: unknown) => audience.forEach((m) => send(m.ws, msg));
  const audienceStats = () => toDisplays({ type: 'stats', audience: audience.size });
  const presenterStatus = () => toAudience({ type: 'presenter', online: displays.size > 0 });

  server.on('upgrade', (req: IncomingMessage, socket: Duplex, head: Buffer) => {
    socket.on('error', () => socket.destroy());
    const { pathname } = new URL(req.url ?? '/', 'http://localhost');
    if (pathname !== '/ws' && pathname !== '/display') return reject(socket, 404);

    const ipKey = protection.clientKey(req);
    const admission = protection.admitConnection(ipKey);
    if (!admission.ok) return reject(socket, admission.status, admission.retryAfter);

    if (pathname === '/ws') {
      if (!isSameOrigin(req)) return reject(socket, 403);
      const { name } = getSession(req);
      if (!name) return reject(socket, 401);
      wss.handleUpgrade(req, socket, head, (ws) => {
        protection.register(ipKey, ws);
        onAudience(ws, name, ipKey);
      });
    } else {
      wss.handleUpgrade(req, socket, head, (ws) => {
        protection.register(ipKey, ws);
        onDisplay(ws, ipKey);
      });
    }
  });

  function onAudience(ws: WebSocket, name: string, ipKey: string) {
    const member: AudienceMember = {
      ws,
      name,
      ipKey,
      reactions: new TokenBucket(limits.connReactionBurst, limits.connReactionPerSec),
      messages: new TokenBucket(limits.connMessageBurst, limits.connMessagePerSec),
    };
    audience.add(member);
    alive.set(ws, true);
    ws.on('pong', () => alive.set(ws, true));
    send(ws, { type: 'hello', name, presenterOnline: displays.size > 0 });
    audienceStats();

    ws.on('message', (data: RawData, isBinary: boolean) => onAudienceMessage(member, data, isBinary));
    ws.on('close', () => {
      audience.delete(member);
      audienceStats();
    });
    ws.on('error', () => ws.terminate());
  }

  function onAudienceMessage(member: AudienceMember, data: RawData, isBinary: boolean) {
    const { ws, ipKey } = member;
    if (ws.readyState !== WebSocket.OPEN) return;

    // Raw message flood (anything at all, valid or not): drop the connection.
    if (!member.messages.take()) {
      if (!protection.strike(ipKey, STRIKE_MESSAGE_FLOOD, 'message flood')) ws.close(1008, 'Too many messages');
      return;
    }

    const reaction = isBinary ? undefined : parseReaction(data);
    if (!reaction) {
      protection.strike(ipKey, STRIKE_INVALID_MESSAGE, 'invalid message');
      return;
    }

    if (!member.reactions.take()) {
      if (!protection.strike(ipKey, STRIKE_OVER_CONN_LIMIT, 'over per-connection reaction limit')) {
        send(ws, { type: 'slow-down' });
      }
      return;
    }

    const verdict = protection.allowReaction(ipKey);
    if (verdict !== 'ok') {
      // Probably shared Wi-Fi/NAT neighbours or a busy room: tell them, but no strike.
      send(ws, { type: 'busy' });
      return;
    }

    toDisplays({ type: 'reaction', id: reaction.id, emoji: reaction.emoji, image: reaction.image });
    console.log(`[react] ${member.name} (${ipKey}): ${reaction.emoji ?? reaction.id}`);
  }

  function onDisplay(ws: WebSocket, ipKey: string) {
    displays.add(ws);
    alive.set(ws, true);
    ws.on('pong', () => alive.set(ws, true));
    console.log(`[display] connected from ${ipKey} (${displays.size} total)`);
    send(ws, { type: 'hello', audience: audience.size, reactions: REACTIONS });
    if (displays.size === 1) presenterStatus();

    // Displays are receive-only; incoming messages are ignored.
    ws.on('close', () => {
      displays.delete(ws);
      console.log(`[display] disconnected (${displays.size} total)`);
      if (displays.size === 0) presenterStatus();
    });
    ws.on('error', () => ws.terminate());
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
  server.on('close', () => clearInterval(heartbeat));

  return () => ({ audience: audience.size, displays: displays.size });
}

function parseReaction(data: RawData) {
  let msg: { type?: unknown; id?: unknown };
  try {
    msg = JSON.parse(data.toString());
  } catch {
    return undefined;
  }
  return msg?.type === 'react' ? findReaction(msg.id) : undefined;
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
