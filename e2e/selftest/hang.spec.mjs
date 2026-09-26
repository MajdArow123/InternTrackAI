// Self-test dimension: never finishes, and holds an open handle — the shape of the 19-hour run.
export async function run() { setInterval(() => {}, 1000); await new Promise(() => {}); }
