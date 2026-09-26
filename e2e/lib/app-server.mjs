// Throwaway InternTrackAI servers, run the way production runs them. Two profiles:
//   - 'suite' (run-all starts one for functional, visual, a11y, security, compat, perf and
//     applications-clickthrough): the placeholder OpenAI key, so every AI feature short-circuits exactly as
//     those checks expect, and no accounts seeded.
//   - 'stub' (practice and tour start their own): the in-process OpenAI stub, and a seeded demo account.
//
// What both guarantee, and why each matters:
//   - Its own SQLite file and uploads folder under e2e/artifacts/run-<ts>/, so app.db is never touched and
//     no throwaway account outlives the run.
//   - ASPNETCORE_ENVIRONMENT=Production, and the published appsettings.json deleted — production's exact
//     configuration layering: appsettings.Production.json plus environment variables. (Until 2026-09-26 this
//     ran as "E2E", which read the developer's gitignored local appsettings.json — publish copies it — and
//     never read appsettings.Production.json: a configuration production has never had. CLAUDE.md §12.)
//     User secrets load only in Development, so the real OpenAI key in user-secrets is never read at all.
//   - Its startup log must say "Hosting environment: Production"; startApp fails otherwise.
//   - A demo account and an admin account, both registered through the real Register page (CLAUDE.md §8:
//     accounts are only ever created there), with local-only credentials, then the admin runs
//     /Admin/ResetDemo so the demo has its 15 applications, suggestions and fixed practice state.
//   - A lowered practice allowance, so the batch check can watch a call be refused. Allowances are per
//     user, so only the account that check uses ever reaches it.
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { startStub } from './openai-stub.mjs';
import { ARTIFACTS, registerThroughUi } from './harness.mjs';
import { errorLines } from './server-log.mjs';

export { errorLines };

const ROOT = process.cwd();
// Published, not just built: outside Development ASP.NET does not serve static web assets out of obj/
// (the scoped-CSS bundle InternTrackAI.styles.css, Identity UI's files), so a built dll run outside Development serves
// pages missing a stylesheet — and a tour measured on a page without its CSS measures the wrong layout.
// Publish output is also what the Dockerfile ships, so this is the shape production actually runs.
// Outside the repo on purpose: a publish folder inside the project directory is itself matched by the
// project's content globs, so each publish copied the previous one into itself until a copy failed.
const PUBLISH = path.join(os.tmpdir(), 'interntrackai-e2e-publish');
const DLL = path.join(PUBLISH, 'InternTrackAI.dll');

export const PRACTICE_LIMIT = 12;   // per signed-in user, per window, for this server only
export const DEMO_EMAIL = 'qa.demo@example.test';
export const ADMIN_EMAIL = 'qa.admin@example.test';

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.once('error', reject);
    s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); });
  });
}

// Always published once per run — there is deliberately no skip switch. One existed while this was written
// and silently ran a whole dimension against the previous publish (a deliberately broken tour.js left
// there by a mutation check); an unchanged republish costs ~4 s, which is not worth that.
let built = false;
function buildOnce() {
  if (built) return;
  const r = spawnSync('dotnet', ['publish', 'InternTrackAI.csproj', '-c', 'Release', '-o', PUBLISH, '--nologo', '-v', 'q'], { cwd: ROOT, encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`dotnet publish failed:\n${(r.stdout || '').slice(-2000)}${(r.stderr || '').slice(-1000)}`);
  // Production has no appsettings.json: it is gitignored, so it is never in the Docker build context. Publish
  // copies the local one in; delete it so these servers read what production reads.
  fs.rmSync(path.join(PUBLISH, 'appsettings.json'), { force: true });
  if (fs.existsSync(path.join(PUBLISH, 'appsettings.json'))) throw new Error('could not remove the published appsettings.json');
  built = true;
}

async function waitForHealth(base, child, logFile, timeoutMs = 90_000) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    if (child.exitCode !== null) throw new Error(`app exited (${child.exitCode}) before it was healthy; see ${logFile}`);
    try { if ((await fetch(`${base}/health`)).ok) return Date.now() - t0; } catch { /* not listening yet */ }
    await new Promise((r) => setTimeout(r, 300));
  }
  throw new Error(`app not healthy after ${timeoutMs} ms; see ${logFile}`);
}

/**
 * Starts the app (and, for the 'stub' profile, the OpenAI stub and a seeded demo) and returns
 * { base, stub, dir, logFile, demoPassword, errors(), stop }. `browser` is used only to register the 'stub'
 * profile's two configured accounts through the UI.
 */
export async function startApp(browser, { name = 'e2e', profile = 'stub' } = {}) {
  if (profile !== 'stub' && profile !== 'suite') throw new Error(`unknown server profile "${profile}"`);
  const seeded = profile === 'stub';
  buildOnce();

  // Keep only the latest run per dimension: its server.log and app.db are what you read after a failure,
  // and older ones would otherwise pile up (each holds a database and an uploads folder).
  if (fs.existsSync(ARTIFACTS)) {
    for (const d of fs.readdirSync(ARTIFACTS)) {
      if (d.startsWith(`run-${name}-`)) fs.rmSync(path.join(ARTIFACTS, d), { recursive: true, force: true });
    }
  }
  const dir = path.join(ARTIFACTS, `run-${name}-${Date.now().toString(36)}`);
  fs.mkdirSync(path.join(dir, 'uploads'), { recursive: true });
  const logFile = path.join(dir, 'server.log');

  const stub = seeded ? await startStub() : null;
  const port = await freePort();
  const base = `http://127.0.0.1:${port}`;
  const demoPassword = `Demo-${Math.random().toString(36).slice(2, 10)}-9x!`;

  // Everything production configures that a local run can honestly mirror. What it cannot — Resend, Google
  // OAuth, PostgreSQL, the TLS proxy — is left unset and listed in CLAUDE.md §10 as a known deviation.
  const env = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Production',
    ConnectionStrings__DefaultConnection: `Data Source=${path.join(dir, 'app.db')}`,
    UPLOADS_PATH: path.join(dir, 'uploads'),
    // Configured as in production, so "the admin endpoint is 404 for a normal account" is about the account,
    // not about no admin existing. The demo keys make the landing page render its demo button, as live.
    Demo__Email: DEMO_EMAIL,
    Demo__Password: demoPassword,
    Demo__AutoReset: 'false',
    Admin__Email: ADMIN_EMAIL,
  };
  if (seeded) {
    Object.assign(env, {
      OpenAI__ApiKey: 'sk-e2e-stub-not-a-real-key',
      OpenAI__BaseUrl: stub.url,
      RateLimiting__AI__Practice__PermitLimit: String(PRACTICE_LIMIT),
      RateLimiting__Registration__PerIpPerHour: '1000',
    });
  } else {
    // The placeholder short-circuits every AI service before a request is built (CLAUDE.md §6) — what the
    // suite's "degrades gracefully without a key" checks are written against. Registration keeps production's
    // limit: the security dimension checks that it throttles.
    env.OpenAI__ApiKey = 'your-openai-api-key-here';
  }
  // Whatever the shell exported for local development must not leak into this server.
  for (const k of ['DATABASE_URL', 'Google__ClientId', 'Google__ClientSecret', 'Resend__ApiKey', 'OpenAI__BaseUrl',
    'Serilog__MinimumLevel__Default', 'Email__BaseUrl', 'BASE_URL'].filter((k) => !(seeded && k === 'OpenAI__BaseUrl'))) delete env[k];

  const out = fs.openSync(logFile, 'a');
  // Content root is the publish folder, as in the container: wwwroot and Data/Seeds are read from there.
  const child = spawn('dotnet', [DLL, '--urls', base], { cwd: PUBLISH, env, stdio: ['ignore', out, out] });
  const kill = () => { if (child.exitCode === null) child.kill('SIGTERM'); };
  process.once('exit', kill);

  // If anything between the spawn and the return fails, stop what was started before rethrowing — the caller
  // never gets a handle to stop it with.
  let startedMs;
  try {
    startedMs = await waitForHealth(base, child, logFile);
    const log = fs.readFileSync(logFile, 'utf8');
    const envLine = log.match(/Hosting environment: (\S+)/);
    if (!envLine || envLine[1] !== 'Production') throw new Error(`the server is not running as Production (log says ${envLine ? envLine[1] : 'nothing'}); see ${logFile}`);

    if (seeded) {
    // The two configured accounts, created the only way accounts are ever created.
    const ctx = await browser.newContext();
    await registerThroughUi(ctx, DEMO_EMAIL, 'Demo User', { base, password: demoPassword });
    await ctx.clearCookies();
    await registerThroughUi(ctx, ADMIN_EMAIL, 'QA Admin', { base });
    const page = await ctx.newPage();
    await page.goto(`${base}/Admin/ResetDemo`, { waitUntil: 'domcontentloaded' });
    await page.click('form[action*="ResetDemo"] button[type=submit]');
    await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }), page.click('#app-confirm-ok')]);   // the form has data-confirm
    const toast = await page.evaluate(() => document.querySelector('#app-toast, .app-toast')?.textContent || document.body.innerText.slice(0, 300));
    await ctx.close();
    if (!/Demo account reset/i.test(toast)) throw new Error(`demo reset did not report success: ${toast}`);
    }
  } catch (err) {
    kill();
    await stub?.close().catch(() => {});
    throw err;
  }

  return {
    base,
    stub,
    dir,
    logFile,
    startedMs,
    demoPassword,
    /** Error/Fatal lines the server has logged so far. */
    errors: () => errorLines(logFile),
    async stop() {
      kill();
      await new Promise((r) => (child.exitCode !== null ? r() : child.once('exit', r)));
      await stub?.close();
    },
  };
}
