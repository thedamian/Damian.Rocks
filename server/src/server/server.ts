import http, { type IncomingMessage, type ServerResponse } from 'node:http';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { attachRealtime } from './realtime.js';
import { Protection } from './protection.js';
import { REACTIONS } from './reactions.js';
import { getSession, ID_COOKIE, isSecureRequest, NAME_COOKIE, sanitizeName, sessionCookie } from './session.js';

const PORT = Number(process.env.PORT ?? 3000);
const HOST = process.env.HOST ?? '0.0.0.0';
const PUBLIC_DIR = path.resolve(import.meta.dirname, '..', 'public');

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.webmanifest': 'application/manifest+json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.gif': 'image/gif',
  '.webp': 'image/webp',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.ico': 'image/x-icon',
};

const SECURITY_HEADERS: Record<string, string> = {
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'X-Frame-Options': 'DENY',
  'Content-Security-Policy':
    "default-src 'self'; connect-src 'self' ws: wss:; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'",
};

const server = http.createServer((req, res) => {
  handle(req, res).catch((err) => {
    console.error(err);
    if (!res.headersSent) sendJson(res, 500, { error: 'Internal error' });
    else res.end();
  });
});
const protection = new Protection();
const stats = attachRealtime(server, protection);

async function handle(req: IncomingMessage, res: ServerResponse) {
  for (const [k, v] of Object.entries(SECURITY_HEADERS)) res.setHeader(k, v);
  const { pathname } = new URL(req.url ?? '/', 'http://localhost');

  if (pathname === '/healthz') return sendJson(res, 200, { ok: true, ...stats(), ...protection.stats() });

  if (pathname === '/api/session' && req.method === 'GET') {
    const session = getSession(req);
    if (!session.id) res.setHeader('Set-Cookie', sessionCookie(ID_COOKIE, randomUUID(), isSecureRequest(req)));
    const retryAfter = protection.banRemaining(protection.clientKey(req));
    return sendJson(res, 200, { name: session.name ?? null, reactions: REACTIONS, ...(retryAfter ? { retryAfter } : {}) });
  }

  if (pathname === '/api/name' && req.method === 'POST') {
    const ipKey = protection.clientKey(req);
    const banned = protection.banRemaining(ipKey);
    if (banned || !protection.allowNameChange(ipKey)) {
      res.setHeader('Retry-After', String(banned || 60));
      return sendJson(res, 429, { error: 'Too many requests. Please try again in a few minutes.' });
    }
    const body = await readJson(req);
    const name = sanitizeName((body as { name?: unknown } | null)?.name);
    if (!name) return sendJson(res, 400, { error: 'Please enter your name.' });
    const secure = isSecureRequest(req);
    const id = getSession(req).id ?? randomUUID();
    res.setHeader('Set-Cookie', [sessionCookie(NAME_COOKIE, name, secure), sessionCookie(ID_COOKIE, id, secure)]);
    console.log(`[join] ${name}`);
    return sendJson(res, 200, { name });
  }

  if (pathname.startsWith('/api/')) return sendJson(res, 404, { error: 'Not found' });
  if (req.method !== 'GET' && req.method !== 'HEAD') return sendJson(res, 405, { error: 'Method not allowed' });
  return serveStatic(req, res, pathname);
}

async function serveStatic(req: IncomingMessage, res: ServerResponse, pathname: string) {
  let rel: string;
  try {
    rel = decodeURIComponent(pathname);
  } catch {
    return sendText(res, 400, 'Bad request');
  }
  if (rel.endsWith('/')) rel += 'index.html';
  const file = path.resolve(PUBLIC_DIR, `.${rel}`);
  if (!file.startsWith(PUBLIC_DIR + path.sep)) return sendText(res, 404, 'Not found');

  let data: Buffer;
  try {
    data = await readFile(file);
  } catch {
    return sendText(res, 404, 'Not found');
  }
  const ext = path.extname(file).toLowerCase();
  res.writeHead(200, {
    'Content-Type': MIME[ext] ?? 'application/octet-stream',
    'Content-Length': data.length,
    'Cache-Control': ext === '.png' || ext === '.svg' ? 'public, max-age=86400' : 'no-cache',
  });
  res.end(req.method === 'HEAD' ? undefined : data);
}

function readJson(req: IncomingMessage, limit = 4096): Promise<unknown> {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => {
      size += chunk.length;
      if (size > limit) {
        reject(new Error('Body too large'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString('utf8') || 'null'));
      } catch {
        resolve(null);
      }
    });
    req.on('error', reject);
  });
}

function sendJson(res: ServerResponse, status: number, body: unknown) {
  const data = JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(data);
}

function sendText(res: ServerResponse, status: number, body: string) {
  res.writeHead(status, { 'Content-Type': 'text/plain; charset=utf-8' });
  res.end(body);
}

server.listen(PORT, HOST, () => {
  console.log(`Damian.Rocks server listening on http://${HOST}:${PORT}`);
  console.log(`  audience: /   desktop overlay: /display`);
});

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.on(signal, () => {
    console.log(`${signal} received, shutting down`);
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 3000).unref();
  });
}
