import { createHash, createHmac, timingSafeEqual } from 'node:crypto';
import type { IncomingMessage } from 'node:http';

/**
 * WebRTC + presenter configuration.
 *
 *  ICE_SERVERS   JSON array of RTCIceServer, default Google's public STUN server.
 *  TURN_URLS     Comma-separated TURN urls, e.g. "turn:turn.damian.rocks:3478,turns:turn.damian.rocks:5349".
 *  TURN_SECRET   coturn `static-auth-secret`: short-lived credentials are minted per connection.
 *  DISPLAY_KEY   Secret the desktop app must send to call on people and receive their audio.
 *                If unset, only displays connecting from this machine (localhost) may do so.
 */

const DEFAULT_ICE: RTCIceServerConfig[] = [{ urls: 'stun:stun.l.google.com:19302' }];
const TURN_TTL_SECONDS = 12 * 60 * 60;

export interface RTCIceServerConfig {
  urls: string | string[];
  username?: string;
  credential?: string;
}

const staticIce = parseIceServers(process.env.ICE_SERVERS);
const turnUrls = (process.env.TURN_URLS ?? '')
  .split(',')
  .map((u) => u.trim())
  .filter(Boolean);
const turnSecret = process.env.TURN_SECRET ?? '';
const displayKeyHash = process.env.DISPLAY_KEY ? sha256(process.env.DISPLAY_KEY) : null;

export const presenterKeyConfigured = displayKeyHash !== null;

export function iceServersFor(user: string): RTCIceServerConfig[] {
  if (!turnUrls.length || !turnSecret) return staticIce;
  // coturn "TURN REST API" credentials: username = "<expiry>:<user>", credential = HMAC-SHA1.
  const username = `${Math.floor(Date.now() / 1000) + TURN_TTL_SECONDS}:${user.replace(/[^\w-]/g, '').slice(0, 32) || 'user'}`;
  const credential = createHmac('sha1', turnSecret).update(username).digest('base64');
  return [...staticIce, { urls: turnUrls, username, credential }];
}

/** Whether a /display connection may control the question queue and receive audio. */
export function isPresenter(req: IncomingMessage, ipKey: string): boolean {
  const auth = req.headers.authorization ?? '';
  const supplied = auth.startsWith('Bearer ') ? auth.slice(7).trim() : '';
  if (displayKeyHash) return supplied.length > 0 && timingSafeEqual(sha256(supplied), displayKeyHash);
  return ipKey === '127.0.0.1' || ipKey === '::1' || ipKey.startsWith('0:0:0:0::');
}

function sha256(value: string): Buffer {
  return createHash('sha256').update(value).digest();
}

function parseIceServers(raw: string | undefined): RTCIceServerConfig[] {
  if (!raw?.trim()) return DEFAULT_ICE;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (Array.isArray(parsed) && parsed.every((s) => s && typeof s === 'object' && 'urls' in s)) {
      return parsed as RTCIceServerConfig[];
    }
  } catch {
    /* fall through */
  }
  console.warn('[config] ICE_SERVERS is not a JSON array of {urls, username?, credential?}; using default STUN');
  return DEFAULT_ICE;
}
