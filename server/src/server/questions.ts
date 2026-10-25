import { randomUUID } from 'node:crypto';

const envNum = (name: string, fallback: number): number => {
  const n = Number(process.env[name]);
  return process.env[name]?.trim() && Number.isFinite(n) && n >= 0 ? n : fallback;
};

export const questionLimits = {
  maxHands: envNum('MAX_HANDS', 100),
  maxHandsPerIp: envNum('MAX_HANDS_PER_IP', 10),
  /** How long a raised hand survives with no connected device (phone asleep, page reloading…). */
  offlineGraceSeconds: envNum('HAND_GRACE_SECONDS', 90),
};

export interface Hand {
  /** Public id shared with displays. Never the session cookie. */
  id: string;
  sessionKey: string;
  ipKey: string;
  name: string;
  raisedAt: number;
  online: boolean;
  offlineSince: number;
  talking: boolean;
}

export interface PublicHand {
  id: string;
  name: string;
  active: boolean;
  talking: boolean;
  online: boolean;
}

export type RaiseResult = { ok: true; hand: Hand } | { ok: false; reason: 'full' };

/**
 * The raised-hands queue (in raise order) plus the one person the presenter is currently
 * talking to. Keyed by session (cookie), so several tabs or a reconnect share one hand.
 */
export class Questions {
  private readonly hands = new Map<string, Hand>();
  private activeKey: string | null = null;

  get active(): Hand | undefined {
    return this.activeKey ? this.hands.get(this.activeKey) : undefined;
  }

  get size() {
    return this.hands.size;
  }

  get(sessionKey: string): Hand | undefined {
    return this.hands.get(sessionKey);
  }

  byId(id: unknown): Hand | undefined {
    if (typeof id !== 'string') return undefined;
    for (const hand of this.hands.values()) if (hand.id === id) return hand;
    return undefined;
  }

  isActive(hand: Hand | undefined): boolean {
    return !!hand && hand.sessionKey === this.activeKey;
  }

  raise(sessionKey: string, ipKey: string, name: string): RaiseResult {
    const existing = this.hands.get(sessionKey);
    if (existing) {
      existing.name = name;
      return { ok: true, hand: existing };
    }
    let fromIp = 0;
    for (const h of this.hands.values()) if (h.ipKey === ipKey) fromIp++;
    if (this.hands.size >= questionLimits.maxHands || fromIp >= questionLimits.maxHandsPerIp) {
      return { ok: false, reason: 'full' };
    }
    const hand: Hand = {
      id: randomUUID(),
      sessionKey,
      ipKey,
      name,
      raisedAt: Date.now(),
      online: true,
      offlineSince: 0,
      talking: false,
    };
    this.hands.set(sessionKey, hand);
    return { ok: true, hand };
  }

  /** Removes the hand. Returns it (if it existed) and whether it was the active speaker. */
  lower(sessionKey: string): { hand?: Hand; wasActive: boolean } {
    const hand = this.hands.get(sessionKey);
    if (!hand) return { wasActive: false };
    const wasActive = this.activeKey === sessionKey;
    this.hands.delete(sessionKey);
    if (wasActive) this.activeKey = null;
    return { hand, wasActive };
  }

  /** Makes the hand the active speaker. Returns the previously active hand, if different. */
  activate(hand: Hand): Hand | undefined {
    const previous = this.active;
    this.activeKey = hand.sessionKey;
    hand.talking = false;
    return previous && previous !== hand ? previous : undefined;
  }

  /** Sends the active speaker (if any) back to the queue. */
  deactivate(): Hand | undefined {
    const previous = this.active;
    this.activeKey = null;
    if (previous) previous.talking = false;
    return previous;
  }

  /** 1-based place in line among hands that are still waiting. */
  position(hand: Hand): number {
    let n = 0;
    for (const h of this.hands.values()) {
      if (h.sessionKey === this.activeKey) continue;
      n++;
      if (h === hand) return n;
    }
    return 0;
  }

  setOnline(hand: Hand, online: boolean) {
    hand.online = online;
    hand.offlineSince = online ? 0 : Date.now();
    if (!online) hand.talking = false;
  }

  /** Hands whose devices have been gone longer than the grace period. */
  expired(now = Date.now()): Hand[] {
    const cutoff = now - questionLimits.offlineGraceSeconds * 1000;
    return [...this.hands.values()].filter((h) => !h.online && h.offlineSince < cutoff);
  }

  list(): PublicHand[] {
    return [...this.hands.values()].map((h) => ({
      id: h.id,
      name: h.name,
      active: h.sessionKey === this.activeKey,
      talking: h.talking,
      online: h.online,
    }));
  }
}
