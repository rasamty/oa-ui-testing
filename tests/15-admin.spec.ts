import { test, expect } from './helpers';

// The users.admin API + the token denylist (acceptance check #9: a disabled
// user's LIVE requests are rejected, not just their next login).
test.use({ storageState: { cookies: [], origins: [] } });

const NET = { timeout: 30_000 };
const P = (t: any) => `${t.project.name.slice(0, 3)}-${t.testId.slice(0, 8)}`;
const boardOrg = (t: any) => `ad-${P(t)}`;

async function admin(page: any, t: any) {
  const u = `ad-admin-${P(t)}`;
  const pass = 'Admin-User-Pass-1234';
  await page.request.post('/api/test/user', {
    data: { username: u, password: pass, permissions: 'state.read state.write users.admin', role: 'Admin' },
  });
  return { u, pass };
}

async function apiLogin(ctx: any, username: string, password: string) {
  const r = await ctx.post('/api/auth/login', { data: { username, password } });
  expect(r.ok(), `login for ${username}`).toBeTruthy();
  return (await r.json()).accessToken as string;
}

test.describe('Admin API (@admin)', () => {

  test('AD-1  a disabled user\'s LIVE token is rejected on the next request (acceptance #9)',
    async ({ playwright, baseURL }, t) => {
      const ctx = await playwright.request.newContext({ baseURL });
      const a = await admin({ request: ctx } as any, t);

      // a victim account
      const victim = `ad-victim-${P(t)}`;
      const vPass = 'Victim-User-Pass-99';
      await ctx.post('/api/test/user', { data: { username: victim, password: vPass } });

      const adminToken = await apiLogin(ctx, a.u, a.pass);
      const victimToken = await apiLogin(ctx, victim, vPass);
      const org = boardOrg(t);

      // victim can read right now
      expect((await ctx.get(`/api/state?org=${org}`, {
        headers: { Authorization: `Bearer ${victimToken}` },
      })).status()).toBe(200);

      // find the victim's id, disable them
      const list = await (await ctx.get('/api/auth/admin/users', {
        headers: { Authorization: `Bearer ${adminToken}` },
      })).json();
      const vid = list.find((x: any) => x.username === victim).id;
      const dis = await ctx.post(`/api/auth/admin/users/${vid}/disable`, {
        headers: { Authorization: `Bearer ${adminToken}` },
      });
      expect(dis.ok()).toBeTruthy();

      // the SAME victim token is now rejected — not at next login, right now
      expect((await ctx.get(`/api/state?org=${org}`, {
        headers: { Authorization: `Bearer ${victimToken}` },
      })).status()).toBe(401);
      // and they cannot sign in again
      expect((await ctx.post('/api/auth/login', { data: { username: victim, password: vPass } })).status())
        .toBe(403);

      // re-enable, and login works again
      await ctx.post(`/api/auth/admin/users/${vid}/enable`, { headers: { Authorization: `Bearer ${adminToken}` } });
      expect((await ctx.post('/api/auth/login', { data: { username: victim, password: vPass } })).status()).toBe(200);

      await ctx.dispose();
    });

  test('AD-2  the admin panel lists users, adds one, and disables one', async ({ page }, t) => {
    const a = await admin(page, t);
    await page.goto(`/?org=${boardOrg(t)}`, { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#authGate')).toBeVisible(NET);
    await page.locator('#authUser').fill(a.u);
    await page.locator('#authPass').fill(a.pass);
    await page.locator('#authSubmit').click();
    await expect(page.locator('#authGate')).toBeHidden(NET);

    await expect(page.locator('#adminBtn')).toBeVisible();
    await page.locator('#adminBtn').click();
    await expect(page.locator('#adminList table')).toBeVisible(NET);

    // add a user
    const fresh = `ad-fresh-${P(t)}-${Date.now().toString(36).slice(-4)}`;
    await page.locator('#adminNewUser').fill(fresh);
    await page.locator('#adminNewPass').fill('Fresh-Temp-Pass-12345');
    await page.locator('#adminCreate').click();
    await expect(page.locator('#adminMsg')).toContainText(/added/i, NET);
    const row = page.locator(`#adminList tr`, { hasText: fresh });
    await expect(row).toBeVisible(NET);

    // disable them from the panel
    await row.getByRole('button', { name: 'Disable' }).click();
    await expect(row.locator('td').nth(2)).toContainText('no', NET);

    // that new user cannot sign in
    expect((await page.request.post('/api/auth/login', { data: { username: fresh, password: 'Fresh-Temp-Pass-12345' } })).status())
      .toBe(403);
  });

  test('AD-3  a non-admin gets 403 from the admin API and no Users button', async ({ page }, t) => {
    const plainUser = `ad-plain-${P(t)}`;
    const pass = 'Plain-User-Pass-777';
    await page.request.post('/api/test/user', { data: { username: plainUser, password: pass } });

    await page.goto(`/?org=${boardOrg(t)}`, { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#authGate')).toBeVisible(NET);
    await page.locator('#authUser').fill(plainUser);
    await page.locator('#authPass').fill(pass);
    await page.locator('#authSubmit').click();
    await expect(page.locator('#authGate')).toBeHidden(NET);

    await expect(page.locator('#adminBtn')).toBeHidden();

    const token = await apiLogin(page.request, plainUser, pass);
    expect((await page.request.get('/api/auth/admin/users', { headers: { Authorization: `Bearer ${token}` } })).status())
      .toBe(403);
  });

});
