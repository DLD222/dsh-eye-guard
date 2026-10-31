import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

/** The browser settings page needs the Web carrier; the helper itself only needs Node. */
export const inject = ['webServer'];

const PACKAGE_DIR = dirname(fileURLToPath(import.meta.url));
const HELPER_SCRIPT = join(PACKAGE_DIR, 'helper', 'EyeGuard.ps1');
const POWERSHELL = join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const API_PREFIX = '/eye-guard/api';

const DEFAULT_CONFIG = Object.freeze({
  intervalMinutes: 20,
  breakSeconds: 20,
  warningSeconds: 30,
  idleResetMinutes: 5,
  highContrast: false,
  reduceMotion: false,
});

const CONFIG_LIMITS = Object.freeze({
  intervalMinutes: { min: 1, max: 120, kind: 'number' },
  breakSeconds: { min: 5, max: 300, kind: 'number' },
  warningSeconds: { min: 0, max: 120, kind: 'number' },
  idleResetMinutes: { min: 1, max: 60, kind: 'number' },
  highContrast: { kind: 'boolean' },
  reduceMotion: { kind: 'boolean' },
});

function pad(value) {
  return String(value).padStart(2, '0');
}

function dayKey(date = new Date()) {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function yesterdayKey() {
  const date = new Date();
  date.setDate(date.getDate() - 1);
  return dayKey(date);
}

function normalizeStats(stats) {
  const today = dayKey();
  if (stats.date !== today) {
    stats.date = today;
    stats.completed = 0;
  }
  if (stats.lastCompletedDate && stats.lastCompletedDate !== today && stats.lastCompletedDate !== yesterdayKey()) {
    stats.streak = 0;
  }
  if (!stats.lastCompletedDate) stats.streak = 0;
  return stats;
}

function sanitizeStats(input) {
  const stats = {
    date: dayKey(),
    completed: 0,
    streak: 0,
    lastCompletedDate: null,
  };
  if (input && typeof input === 'object' && !Array.isArray(input)) {
    if (typeof input.date === 'string' && /^\d{4}-\d{2}-\d{2}$/u.test(input.date)) stats.date = input.date;
    if (Number.isInteger(input.completed) && input.completed >= 0) stats.completed = input.completed;
    if (Number.isInteger(input.streak) && input.streak >= 0) stats.streak = input.streak;
    if (typeof input.lastCompletedDate === 'string' && /^\d{4}-\d{2}-\d{2}$/u.test(input.lastCompletedDate)) {
      stats.lastCompletedDate = input.lastCompletedDate;
    }
  }
  return normalizeStats(stats);
}

function statsKey(stats) {
  return `${stats.date}|${stats.completed}|${stats.streak}|${stats.lastCompletedDate ?? ''}`;
}

function clampInteger(value, min, max) {
  return Math.min(max, Math.max(min, Math.round(value)));
}

/** Coerce a helper-originated config, which is untrusted but non-fatal. */
function coerceConfig(input, base = DEFAULT_CONFIG) {
  const config = { ...base };
  if (!input || typeof input !== 'object' || Array.isArray(input)) return config;
  for (const [key, rule] of Object.entries(CONFIG_LIMITS)) {
    const value = input[key];
    if (value === undefined) continue;
    if (rule.kind === 'boolean') {
      if (typeof value === 'boolean') config[key] = value;
      continue;
    }
    if (typeof value === 'number' && Number.isFinite(value)) {
      config[key] = clampInteger(value, rule.min, rule.max);
    }
  }
  return config;
}

/** Validate a settings-page patch strictly; return a user-facing message on failure. */
function readConfigPatch(input) {
  if (!input || typeof input !== 'object' || Array.isArray(input)) {
    return { error: 'Send a JSON object with the settings to change.' };
  }
  const patch = {};
  for (const [key, rule] of Object.entries(CONFIG_LIMITS)) {
    if (!Object.hasOwn(input, key)) continue;
    const value = input[key];
    if (rule.kind === 'boolean') {
      if (typeof value !== 'boolean') return { error: `${key} must be true or false.` };
      patch[key] = value;
      continue;
    }
    if (!Number.isInteger(value) || value < rule.min || value > rule.max) {
      return { error: `${key} must be a whole number between ${rule.min} and ${rule.max}.` };
    }
    patch[key] = value;
  }
  if (Object.keys(patch).length === 0) return { error: 'No recognized settings were supplied.' };
  return { patch };
}

async function readJson(file) {
  try {
    return JSON.parse(await readFile(file, 'utf8'));
  } catch (error) {
    if (error?.code !== 'ENOENT') console.error('[eye-guard] could not read', file, error);
    return undefined;
  }
}

async function writeJson(file, value) {
  const temporary = `${file}.${process.pid}.tmp`;
  await writeFile(temporary, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
  try {
    await rename(temporary, file);
  } catch {
    await rm(file, { force: true });
    await rename(temporary, file);
  }
}

function originAllowed(request) {
  const origin = request.headers.origin;
  if (!origin) return true;
  try {
    return new URL(origin).host === request.headers.host;
  } catch {
    return false;
  }
}

function json(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'content-length': Buffer.byteLength(body),
  });
  response.end(body);
}

class EyeGuardRuntime {
  constructor(ctx, dataDir, config, stats) {
    this.ctx = ctx;
    this.dataDir = dataDir;
    this.configFile = join(dataDir, 'settings.json');
    this.statsFile = join(dataDir, 'stats.json');
    this.config = config;
    this.stats = stats;
    this.child = undefined;
    this.connected = false;
    this.status = null;
    this.lastBreakId = '';
    this.stopped = false;
    this.restartDelay = 2000;
    this.restartTimer = undefined;
    this.midnightTimer = undefined;
    this.disposeRoute = undefined;
    this.stdoutBuffer = '';
  }

  async start() {
    this.disposeRoute = this.ctx.webServer.register({
      kind: 'prefix',
      path: API_PREFIX,
      handler: (request, response) => this.handleRequest(request, response),
    });
    this.midnightTimer = setInterval(() => this.tickDay(), 60_000);
    this.midnightTimer.unref?.();
    this.spawnHelper();
  }

  stop() {
    if (this.stopped) return;
    this.stopped = true;
    this.disposeRoute?.();
    this.disposeRoute = undefined;
    clearInterval(this.midnightTimer);
    clearTimeout(this.restartTimer);
    this.midnightTimer = undefined;
    this.restartTimer = undefined;
    const child = this.child;
    this.child = undefined;
    this.connected = false;
    if (child) {
      this.writeHelper({ type: 'shutdown' });
      try {
        child.stdin?.end();
      } catch {
        // The pipe is already gone.
      }
      const killTimer = setTimeout(() => {
        if (child.exitCode === null && child.signalCode === null) child.kill();
      }, 1500);
      killTimer.unref?.();
    }
  }

  spawnHelper() {
    if (this.stopped || this.child) return;
    if (!existsSync(HELPER_SCRIPT)) {
      console.error('[eye-guard] helper script is missing:', HELPER_SCRIPT);
      return;
    }
    if (!existsSync(POWERSHELL)) {
      console.error('[eye-guard] Windows PowerShell is missing:', POWERSHELL);
      return;
    }
    let child;
    try {
      child = spawn(POWERSHELL, [
        '-NoProfile',
        '-NonInteractive',
        '-WindowStyle',
        'Hidden',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        HELPER_SCRIPT,
      ], {
        stdio: ['pipe', 'pipe', 'pipe'],
        windowsHide: true,
        cwd: PACKAGE_DIR,
      });
    } catch (error) {
      console.error('[eye-guard] could not start helper:', error);
      this.scheduleRestart();
      return;
    }
    this.child = child;
    this.stdoutBuffer = '';
    child.stdout?.setEncoding('utf8');
    child.stdout?.on('data', (chunk) => this.consumeOutput(chunk));
    child.stderr?.setEncoding('utf8');
    child.stderr?.on('data', (chunk) => {
      const text = String(chunk).trim();
      if (text) console.error('[eye-guard helper]', text);
    });
    child.once('spawn', () => {
      this.connected = true;
      this.restartDelay = 2000;
      this.sendInit();
    });
    child.once('error', (error) => {
      console.error('[eye-guard] helper error:', error);
    });
    child.once('exit', (code, signal) => {
      if (this.child !== child) return;
      this.child = undefined;
      this.connected = false;
      this.status = null;
      if (!this.stopped) {
        console.error('[eye-guard] helper exited', { code, signal });
        this.scheduleRestart();
      }
    });
  }

  scheduleRestart() {
    if (this.stopped || this.restartTimer) return;
    const delay = this.restartDelay;
    this.restartDelay = Math.min(this.restartDelay * 2, 30_000);
    this.restartTimer = setTimeout(() => {
      this.restartTimer = undefined;
      this.spawnHelper();
    }, delay);
    this.restartTimer.unref?.();
  }

  consumeOutput(chunk) {
    this.stdoutBuffer += chunk;
    for (;;) {
      const newline = this.stdoutBuffer.indexOf('\n');
      if (newline < 0) break;
      const line = this.stdoutBuffer.slice(0, newline).trim();
      this.stdoutBuffer = this.stdoutBuffer.slice(newline + 1);
      if (line) this.consumeMessage(line);
    }
  }

  consumeMessage(line) {
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      console.error('[eye-guard] unparseable helper message:', line.slice(0, 500));
      return;
    }
    if (!message || typeof message !== 'object') return;
    switch (message.type) {
      case 'ready':
        this.connected = true;
        this.sendInit();
        break;
      case 'state':
        this.status = message;
        break;
      case 'break-complete':
        this.completeBreak(message);
        break;
      case 'break-skipped':
        this.status = message;
        break;
      case 'settings-changed':
        this.config = coerceConfig(message.config, this.config);
        void this.persistConfig();
        this.writeHelper({ type: 'config', config: this.config, resetClock: false });
        break;
      case 'log':
        if (typeof message.message === 'string') console.log('[eye-guard helper]', message.message);
        break;
      default:
        break;
    }
  }

  completeBreak(message) {
    const breakId = typeof message.breakId === 'string' ? message.breakId : '';
    if (breakId && breakId === this.lastBreakId) return;
    if (breakId) this.lastBreakId = breakId;
    normalizeStats(this.stats);
    const today = dayKey();
    if (this.stats.lastCompletedDate !== today) {
      this.stats.streak = this.stats.lastCompletedDate === yesterdayKey()
        ? Math.max(1, this.stats.streak + 1)
        : 1;
      this.stats.lastCompletedDate = today;
    }
    this.stats.date = today;
    this.stats.completed += 1;
    void this.persistStats();
    this.writeHelper({ type: 'stats', stats: this.stats });
  }

  tickDay() {
    const before = statsKey(this.stats);
    normalizeStats(this.stats);
    if (statsKey(this.stats) === before) return;
    void this.persistStats();
    this.writeHelper({ type: 'stats', stats: this.stats });
  }

  sendInit() {
    this.writeHelper({ type: 'init', config: this.config, stats: this.stats });
  }

  writeHelper(message) {
    if (!this.child?.stdin?.writable) return;
    try {
      this.child.stdin.write(`${JSON.stringify(message)}\n`);
    } catch (error) {
      console.error('[eye-guard] could not write to helper:', error);
    }
  }

  async persistConfig() {
    try {
      await writeJson(this.configFile, this.config);
    } catch (error) {
      console.error('[eye-guard] could not save settings:', error);
    }
  }

  async persistStats() {
    try {
      await writeJson(this.statsFile, this.stats);
    } catch (error) {
      console.error('[eye-guard] could not save stats:', error);
    }
  }

  snapshot() {
    normalizeStats(this.stats);
    return {
      ok: true,
      config: this.config,
      stats: this.stats,
      helperConnected: this.connected,
      status: this.status,
      nextBreakAt: typeof this.status?.nextBreakAt === 'number' ? this.status.nextBreakAt : null,
      phase: typeof this.status?.phase === 'string' ? this.status.phase : 'offline',
      now: Date.now(),
    };
  }

  async readBody(request) {
    let body = '';
    for await (const chunk of request) {
      body += chunk;
      if (Buffer.byteLength(body) > 64 * 1024) throw new Error('Request body is too large.');
    }
    if (!body.trim()) return {};
    return JSON.parse(body);
  }

  async handleRequest(request, response) {
    if (!originAllowed(request)) {
      json(response, 403, { ok: false, error: 'This request did not come from the Harness page.' });
      return;
    }
    const url = new URL(request.url || '/', 'http://eye-guard.local');
    const path = url.pathname;
    try {
      if (path === `${API_PREFIX}/state` && request.method === 'GET') {
        json(response, 200, this.snapshot());
        return;
      }
      if (path === `${API_PREFIX}/config` && request.method === 'POST') {
        const parsed = readConfigPatch(await this.readBody(request));
        if (parsed.error) {
          json(response, 400, { ok: false, error: parsed.error });
          return;
        }
        this.config = { ...this.config, ...parsed.patch };
        await this.persistConfig();
        this.writeHelper({ type: 'config', config: this.config, resetClock: true });
        json(response, 200, this.snapshot());
        return;
      }
      if (path === `${API_PREFIX}/break` && request.method === 'POST') {
        if (!this.connected) {
          json(response, 503, { ok: false, error: 'The Eye Guard helper is not running.' });
          return;
        }
        this.writeHelper({ type: 'break-now' });
        json(response, 200, this.snapshot());
        return;
      }
      if (path === `${API_PREFIX}/stats/reset` && request.method === 'POST') {
        this.stats = { date: dayKey(), completed: 0, streak: 0, lastCompletedDate: null };
        await this.persistStats();
        this.writeHelper({ type: 'stats', stats: this.stats });
        json(response, 200, this.snapshot());
        return;
      }
      json(response, 404, { ok: false, error: 'Unknown Eye Guard endpoint.' });
    } catch (error) {
      console.error('[eye-guard] request failed:', error);
      json(response, 400, { ok: false, error: error instanceof Error ? error.message : 'Invalid request.' });
    }
  }
}

export async function apply(ctx) {
  const home = process.env.DSH_HOME?.trim() ? resolve(process.env.DSH_HOME) : join(homedir(), '.dsh');
  const dataDir = join(home, 'eye-guard');
  await mkdir(dataDir, { recursive: true });

  const config = coerceConfig(await readJson(join(dataDir, 'settings.json')), DEFAULT_CONFIG);
  const stats = sanitizeStats(await readJson(join(dataDir, 'stats.json')));
  const runtime = new EyeGuardRuntime(ctx, dataDir, config, stats);
  await runtime.start();
  ctx.effect(() => () => runtime.stop(), 'eye-guard: helper and routes');
}
