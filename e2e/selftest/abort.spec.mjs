// Self-test dimension: throws before recording anything — the shape that used to read as "0 new failures".
export async function run() { throw new Error('deliberate abort before any check ran'); }
