import type { IncomingMessage } from 'node:http';
import { isIP } from 'node:net';
import type { WebSocket } from 'ws';

/**
 * Abuse protection, keyed by client IP (IPv6 grouped per /64, since one host usually owns a
 * whole /64). Everything is configurable through environment variables; see README.
 *
 * Layers:
 *  - per connection: reaction rate + raw message rate (enforced in realtime.ts)
 *  - per IP:         reaction rate shared across all of that IP's connections and tabs,
 *                    concurrent sockets, connection attempts, name sign-ups
 *  - global:         max reactions forwarded to the overlay, max total sockets
 *  - bans:           protocol violations add "strikes"; too many strikes bans the IP for a while
 *
 * The official web client throttles itself below the per-connection limit, so only scripted
 * or modified clients accumulate strikes. Limits that NAT'd audiences can legitimately hit
 * (shared venue Wi-Fi) only reject requests; they never cause bans.
 */

const envNum = (name: string, fallback: number): number => {
  const raw = process.env[name];
  if (raw === undefined || raw.trim() === '') return fallback;
  const n = Number(raw);
  return Number.isFinite(n) && n >= 0 ? n : fallback;
};

export const limits = {
  connReactionBurst: envNum('RL_CONN_BURST', 5),
  connReactionPerSec: envNum('RL_CONN_PER_SEC', 2),
  connMessageBurst: envNum('RL_MSG_BURST', 20),
  connMessagePerSec: envNum('RL_MSG_PER_SEC', 8),
  ipReactionBurst: envNum('RL_IP_BURST', 15),
  ipReactionPerSec: envNum('RL_IP_PER_SEC', 4),
  globalReactionBurst: envNum('RL_GLOBAL_BURST', 30),
  globalReactionPerSec: envNum('RL_GLOBAL_PER_SEC', 12),
  ipConnectBurst: envNum('RL_CONNECT_BURST', 100),
  ipConnectPerMin: envNum('RL_CONNECT_PER_MIN', 60),
  ipNameBurst: envNum('RL_NAME_BURST', 60),
  ipNamePerMin: envNum('RL_NAME_PER_MIN', 30),
  maxConnPerIp: envNum('MAX_CONN_PER_IP', 250),
  maxConnTotal: envNum('MAX_CONN_TOTAL', 5000),
  banStrikes: envNum('BAN_STRIKES', 20),
  banStrikeDecayPerMin: envNum('BAN_STRIKE_DECAY_PER_MIN', 10),
  banSeconds: envNum('BAN_SECONDS', 300),
};
export type Limits = typeof limits;

export class TokenBucket {
  private tokens: number;
  private last: number;

  constructor(
    private readonly capacity: number,
    private readonly refillPerSec: number,
  ) {
    this.tokens = capacity;
    this.last = Date.now();
  }

  take(cost = 1): boolean {
    this.refill();
    if (this.tokens < cost) return false;
    this.tokens -= cost;
    return true;
  }

  /** A full bucket is indistinguishable from a new one, so it can be forgotten. */
  isFull(): boolean {
    this.refill();
    return this.tokens >= this.capacity;
  }

  private refill() {
    const now = Date.now();
    this.tokens = Math.min(this.capacity, this.tokens + ((now - this.last) / 1000) * this.refillPerSec);
    this.last = now;
  }
}

class KeyedBuckets {
  private readonly buckets = new Map<string, TokenBucket>();

  constructor(
    private readonly capacity: number,
    private readonly refillPerSec: number,
  ) {}

  take(key: string, cost = 1): boolean {
    let bucket = this.buckets.get(key);
    if (!bucket) {
      bucket = new TokenBucket(this.capacity, this.refillPerSec);
      this.buckets.set(key, bucket);
    }
    return bucket.take(cost);
  }

  sweep() {
    for (const [key, bucket] of this.buckets) if (bucket.isFull()) this.buckets.delete(key);
  }

  get size() {
    return this.buckets.size;
  }
}

export type Admission = { ok: true } | { ok: false; status: 429 | 503; retryAfter: number };
export type ReactionVerdict = 'ok' | 'ip-limit' | 'global-limit';

export class Protection {
  private readonly ipReactions: KeyedBuckets;
  private readonly ipConnects: KeyedBuckets;
  private readonly ipNames: KeyedBuckets;
  private readonly strikes: KeyedBuckets;
  private readonly global: TokenBucket;
  private readonly bans = new Map<string, number>();
  private readonly sockets = new Map<string, Set<WebSocket>>();
  private totalSockets = 0;
  private readonly trusted: Set<string>;

  constructor(
    private readonly cfg: Limits = limits,
    trustProxy = process.env.TRUST_PROXY ?? 'loopback',
  ) {
    this.trusted = parseTrustedProxies(trustProxy);
    this.ipReactions = new KeyedBuckets(cfg.ipReactionBurst, cfg.ipReactionPerSec);
    this.ipConnects = new KeyedBuckets(cfg.ipConnectBurst, cfg.ipConnectPerMin / 60);
    this.ipNames = new KeyedBuckets(cfg.ipNameBurst, cfg.ipNamePerMin / 60);
    this.strikes = new KeyedBuckets(cfg.banStrikes, cfg.banStrikeDecayPerMin / 60);
    this.global = new TokenBucket(cfg.globalReactionBurst, cfg.globalReactionPerSec);
    setInterval(() => this.sweep(), 60_000).unref();
  }

  /**
   * The real client IP. X-Forwarded-For is only honoured when the direct peer is a trusted
   * proxy (default: loopback, i.e. Caddy/nginx on the same box), and then the right-most
   * untrusted hop is used, because everything left of it can be forged by the client.
   */
  clientKey(req: IncomingMessage): string {
    const remote = normalizeIp(req.socket.remoteAddress ?? '');
    let ip = remote;
    if (this.trusted.has(remote)) {
      const header = req.headers['x-forwarded-for'];
      const hops = (Array.isArray(header) ? header.join(',') : (header ?? ''))
        .split(',')
        .map((h) => normalizeIp(h.trim()))
        .filter(Boolean);
      for (let i = hops.length - 1; i >= 0; i--) {
        if (this.trusted.has(hops[i])) continue;
        if (isIP(hops[i])) ip = hops[i];
        break;
      }
    }
    return ipKey(ip);
  }

  /** Seconds left on a ban, or 0. */
  banRemaining(key: string): number {
    const until = this.bans.get(key);
    if (!until) return 0;
    const left = Math.ceil((until - Date.now()) / 1000);
    if (left > 0) return left;
    this.bans.delete(key);
    return 0;
  }

  admitConnection(key: string): Admission {
    const banned = this.banRemaining(key);
    if (banned) return { ok: false, status: 429, retryAfter: banned };
    if (this.totalSockets >= this.cfg.maxConnTotal) return { ok: false, status: 503, retryAfter: 30 };
    if ((this.sockets.get(key)?.size ?? 0) >= this.cfg.maxConnPerIp) return { ok: false, status: 429, retryAfter: 30 };
    if (!this.ipConnects.take(key)) return { ok: false, status: 429, retryAfter: 10 };
    return { ok: true };
  }

  register(key: string, ws: WebSocket) {
    let set = this.sockets.get(key);
    if (!set) this.sockets.set(key, (set = new Set()));
    set.add(ws);
    this.totalSockets++;
    ws.once('close', () => {
      this.totalSockets--;
      set.delete(ws);
      if (set.size === 0 && this.sockets.get(key) === set) this.sockets.delete(key);
    });
  }

  allowNameChange(key: string): boolean {
    return this.ipNames.take(key);
  }

  allowReaction(key: string): ReactionVerdict {
    if (!this.ipReactions.take(key)) return 'ip-limit';
    if (!this.global.take()) return 'global-limit';
    return 'ok';
  }

  /** Records a protocol violation. Returns true if the IP is (now) banned. */
  strike(key: string, weight: number, reason: string): boolean {
    if (this.banRemaining(key)) return true;
    if (this.cfg.banSeconds <= 0 || this.strikes.take(key, weight)) return false;
    this.ban(key, reason);
    return true;
  }

  ban(key: string, reason: string) {
    const seconds = this.cfg.banSeconds;
    this.bans.set(key, Date.now() + seconds * 1000);
    console.warn(`[ban] ${key} for ${seconds}s: ${reason}`);
    for (const ws of this.sockets.get(key) ?? []) {
      ws.close(1008, `banned:${seconds}`);
      setTimeout(() => ws.terminate(), 1000).unref();
    }
  }

  stats() {
    return {
      sockets: this.totalSockets,
      ips: this.sockets.size,
      bannedIps: [...this.bans.keys()].filter((k) => this.banRemaining(k) > 0).length,
    };
  }

  private sweep() {
    this.ipReactions.sweep();
    this.ipConnects.sweep();
    this.ipNames.sweep();
    this.strikes.sweep();
    for (const key of this.bans.keys()) this.banRemaining(key);
  }
}

function parseTrustedProxies(value: string): Set<string> {
  const out = new Set<string>();
  for (const part of value.split(',').map((p) => p.trim().toLowerCase())) {
    if (!part || part === 'none' || part === 'false') continue;
    if (part === 'loopback') {
      out.add('127.0.0.1');
      out.add('::1');
    } else {
      out.add(normalizeIp(part));
    }
  }
  return out;
}

export function normalizeIp(ip: string): string {
  let s = ip.trim().toLowerCase();
  if (s.startsWith('[') && s.includes(']')) s = s.slice(1, s.indexOf(']'));
  const zone = s.indexOf('%');
  if (zone >= 0) s = s.slice(0, zone);
  if (s.startsWith('::ffff:') && isIP(s.slice(7)) === 4) s = s.slice(7);
  return s;
}

/** IPv4 as-is; IPv6 collapsed to its /64 so a single host can't rotate through addresses. */
export function ipKey(ip: string): string {
  if (isIP(ip) !== 6) return ip || 'unknown';
  const [head, tail] = ip.split('::');
  const left = head ? head.split(':') : [];
  const right = tail ? tail.split(':') : [];
  const groups = ip.includes('::') ? [...left, ...Array(Math.max(0, 8 - left.length - right.length)).fill('0'), ...right] : left;
  return `${groups.slice(0, 4).map((g) => g.replace(/^0+(?=.)/, '')).join(':')}::/64`;
}
