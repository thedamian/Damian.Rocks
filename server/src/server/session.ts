import type { IncomingMessage } from 'node:http';
import type { TLSSocket } from 'node:tls';

export const NAME_COOKIE = 'dr_name';
export const ID_COOKIE = 'dr_id';
const ONE_YEAR_SECONDS = 60 * 60 * 24 * 365;
const MAX_NAME_LENGTH = 40;

export interface Session {
  id?: string;
  name?: string;
}

export function parseCookies(header: string | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  if (!header) return out;
  for (const part of header.split(';')) {
    const eq = part.indexOf('=');
    if (eq < 0) continue;
    const key = part.slice(0, eq).trim();
    const raw = part.slice(eq + 1).trim();
    try {
      out[key] = decodeURIComponent(raw);
    } catch {
      // ignore malformed cookie values
    }
  }
  return out;
}

export function getSession(req: IncomingMessage): Session {
  const cookies = parseCookies(req.headers.cookie);
  const name = sanitizeName(cookies[NAME_COOKIE]);
  const id = /^[0-9a-f-]{36}$/i.test(cookies[ID_COOKIE] ?? '') ? cookies[ID_COOKIE] : undefined;
  return { id, name: name ?? undefined };
}

export function sanitizeName(input: unknown): string | null {
  if (typeof input !== 'string') return null;
  const cleaned = input
    .normalize('NFC')
    // Drop control/unassigned chars and bidi overrides, but keep ZWJ so emoji sequences survive.
    .replace(/[\p{Cc}\p{Co}\p{Cn}\u2028\u2029\u202A-\u202E\u2066-\u2069]/gu, '')
    .replace(/\s+/g, ' ')
    .trim();
  const name = Array.from(cleaned).slice(0, MAX_NAME_LENGTH).join('').trim();
  return name.length > 0 ? name : null;
}

export function isSecureRequest(req: IncomingMessage): boolean {
  if ((req.socket as TLSSocket).encrypted) return true;
  const proto = req.headers['x-forwarded-proto'];
  const first = (Array.isArray(proto) ? proto[0] : proto)?.split(',')[0]?.trim();
  return first === 'https';
}

export function sessionCookie(name: string, value: string, secure: boolean): string {
  return [
    `${name}=${encodeURIComponent(value)}`,
    'Path=/',
    `Max-Age=${ONE_YEAR_SECONDS}`,
    'HttpOnly',
    'SameSite=Lax',
    ...(secure ? ['Secure'] : []),
  ].join('; ');
}
