// Runs `fn` against the server the audit tools should measure: BASE_URL if you set one, otherwise a throwaway
// Production-environment server (lib/app-server.mjs) — the same environment the e2e suite now measures, for
// the same reason (CLAUDE.md §12: a check run in the wrong environment proves nothing about the right one).
export async function withAuditServer(profile, fn) {
  if (process.env.BASE_URL) return fn();
  const { chromium, setBase } = await import('./harness.mjs');
  const { startApp } = await import('./app-server.mjs');
  const browser = await chromium.launch({ channel: 'chrome' });
  const app = await startApp(browser, { name: `audit-${profile}`, profile });
  try {
    setBase(app.base);
    console.log(`measuring a Production-environment server on ${app.base} (log: ${app.logFile})`);
    return await fn();
  } finally {
    await app.stop();
    await browser.close();
  }
}
