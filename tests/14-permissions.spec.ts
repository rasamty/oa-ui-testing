import { test, expect } from './helpers';

// Claim-based authorization: "state.write" gates PUT/DELETE /api/state.
test.use({ storageState: { cookies: [], origins: [] } });

const NET = { timeout: 30_000 };
const boardOrg = (t: any) => `pm-${t.project.name.slice(0, 3)}-${t.testId.slice(0, 12)}`;

async function makeUser(page: any, t: any, opts: { pass: string; role?: string; permissions?: string }) {
  const u = `pm-${t.project.name.slice(0, 3)}-${t.title.replace(/[^a-z0-9]+/gi, '').slice(0, 8)}-${t.testId.slice(0, 6)}`;
  await page.request.post('/api/test/user', {
    data: { username: u, password: opts.pass, role: opts.role, permissions: opts.permissions },
  });
  return u;
}

async function signIn(page: any, t: any, u: string, pass: string) {
  await page.goto(`/?org=${boardOrg(t)}`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('#authGate')).toBeVisible(NET);
  await page.locator('#authUser').fill(u);
  await page.locator('#authPass').fill(pass);
  await page.locator('#authSubmit').click();
  await expect(page.locator('#authGate')).toBeHidden(NET);
}

test.describe('Permissions (@perms)', () => {

  test('PM-1  a read-only member sees the board but cannot save', async ({ page }, t) => {
    const pass = 'Read-Only-Pass-One-11';
    const u = await makeUser(page, t, { pass, permissions: 'state.read' });
    await signIn(page, t, u, pass);

    // board is visible
    await expect(page.locator('#leftPane')).toBeVisible();
    // the read-only banner shows
    await expect(page.locator('#roBanner')).toBeVisible(NET);

    // a direct PUT with this user's token is 403
    const login = await page.request.post('/api/auth/login', { data: { username: u, password: pass } });
    const token = (await login.json()).accessToken;
    const put = await page.request.put(`/api/state?org=${boardOrg(t)}`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { UI: {}, OA: {}, colorScale: null, 'Root Org Name': 'x' },
    });
    expect(put.status()).toBe(403);
    // but GET is fine
    const get = await page.request.get(`/api/state?org=${boardOrg(t)}`, { headers: { Authorization: `Bearer ${token}` } });
    expect(get.status()).toBe(200);
  });

  test('PM-2  a writer can save and sees no banner', async ({ page }, t) => {
    const pass = 'Writer-Pass-Two-2222';
    const u = await makeUser(page, t, { pass, permissions: 'state.read state.write' });
    await signIn(page, t, u, pass);

    await expect(page.locator('#roBanner')).toBeHidden();
    const saved = await page.evaluate(() => (window as any).serverSync.flushNow());
    expect(saved).toBe(true);
  });

  test('PM-3  an Admin bypasses the permission check without state.write listed', async ({ page }, t) => {
    const pass = 'Admin-Pass-Three-3333';
    const u = await makeUser(page, t, { pass, role: 'Admin', permissions: 'state.read' });

    const login = await page.request.post('/api/auth/login', { data: { username: u, password: pass } });
    const token = (await login.json()).accessToken;
    const put = await page.request.put(`/api/state?org=${boardOrg(t)}`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { UI: {}, OA: {}, colorScale: null, 'Root Org Name': 'x' },
    });
    expect(put.status()).toBe(200);
  });

});
