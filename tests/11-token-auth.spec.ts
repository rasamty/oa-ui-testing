import { test, expect } from './helpers';
import { TEST_USER, TEST_PASS } from './auth-constants';

// Phase 3: /api/* is protected by a bearer access token; the refresh token rides
// an HttpOnly cookie and rotates on every use. This file drops the shared session
// and drives the token endpoints directly.
test.use({ storageState: { cookies: [], origins: [] } });

const NET = { timeout: 30_000 };
const boardOrg = (t: { testId: string }) => 'tok-' + t.testId.slice(0, 16);

test.describe('Token auth (@token)', () => {

  test('TOK-1  /api/state refuses a request with no token', async ({ request }, t) => {
    const r = await request.get(`/api/state?org=${boardOrg(t)}`);
    expect(r.status()).toBe(401);
  });

  test('TOK-2  login returns an access token that opens /api/state', async ({ request }, t) => {
    const login = await request.post('/api/auth/login', { data: { username: TEST_USER, password: TEST_PASS } });
    expect(login.ok()).toBeTruthy();
    const { accessToken, tokenType } = await login.json();
    expect(tokenType).toBe('Bearer');

    const ok = await request.get(`/api/state?org=${boardOrg(t)}`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    expect(ok.status()).toBe(200);
  });

  test('TOK-3  a refresh rotates the token and replaying the old one is rejected', async ({ playwright, baseURL }) => {
    const ctx = await playwright.request.newContext({ baseURL });
    const login = await ctx.post('/api/auth/login', { data: { username: TEST_USER, password: TEST_PASS } });
    const firstRt = (await login.json()).refreshToken as string;

    const rotated = await ctx.post('/api/auth/refresh', { data: { refreshToken: firstRt } });
    expect(rotated.ok()).toBeTruthy();
    const secondRt = (await rotated.json()).refreshToken as string;
    expect(secondRt).not.toBe(firstRt);

    // Replay the first (now-rotated) token from a cookie-less context -> rejected.
    const clean = await playwright.request.newContext({ baseURL });
    const replay = await clean.post('/api/auth/refresh', { data: { refreshToken: firstRt } });
    expect(replay.status()).toBe(401);
    await ctx.dispose();
    await clean.dispose();
  });

  test('TOK-4  logout kills the refresh session', async ({ playwright, baseURL }) => {
    const ctx = await playwright.request.newContext({ baseURL });
    const login = await ctx.post('/api/auth/login', { data: { username: TEST_USER, password: TEST_PASS } });
    const rt = (await login.json()).refreshToken as string;

    await ctx.post('/api/auth/logout');
    const after = await ctx.post('/api/auth/refresh', { data: { refreshToken: rt } });
    expect(after.status()).toBe(401);
    await ctx.dispose();
  });

  test('TOK-5  the page recovers from an expired access token via a silent refresh', async ({ page }, t) => {
    await page.request.post(`/api/test/reset?org=${boardOrg(t)}`);
    await page.goto(`/?org=${boardOrg(t)}`, { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#authGate')).toBeVisible(NET);
    await page.locator('#authUser').fill(TEST_USER);
    await page.locator('#authPass').fill(TEST_PASS);
    await page.locator('#authSubmit').click();
    await expect(page.locator('#authGate')).toBeHidden(NET);

    // Throw the in-memory access token away; authFetch must refresh and retry.
    await page.evaluate(() => (window as any).authClient._expireForTest());
    expect(await page.evaluate(() => (window as any).authClient.isSignedIn())).toBe(false);

    // A save (PUT via authFetch) still succeeds and the sign-in panel stays away.
    const saved = await page.evaluate(() => (window as any).serverSync.flushNow());
    expect(saved).toBe(true);
    await expect(page.locator('#authGate')).toBeHidden();
    expect(await page.evaluate(() => (window as any).authClient.isSignedIn())).toBe(true);
  });

});
