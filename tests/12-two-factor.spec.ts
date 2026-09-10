import { test, expect } from './helpers';
import { totp } from './totp';

// 2FA enrolment + login. Each test uses a throwaway account so turning 2FA on/off
// disturbs nothing else. Drops the shared session.
test.use({ storageState: { cookies: [], origins: [] } });

const NET = { timeout: 30_000 };
const boardOrg = (t: any) => `2fa-${t.project.name.slice(0, 3)}-${t.testId.slice(0, 12)}`;

// A username unique to this test AND browser project — 2FA changes state on the
// account, and the same test runs concurrently across chromium/firefox/webkit.
async function freshUser(page: any, t: any, pass: string) {
  const slug = t.title.replace(/[^a-z0-9]+/gi, '').slice(0, 10).toLowerCase();
  const u = `2fa-${t.project.name.slice(0, 3)}-${slug}-${t.testId.slice(0, 8)}`;
  await page.request.post('/api/test/user', { data: { username: u, password: pass } });
  return u;
}

async function signIn(page: any, t: { testId: string }, u: string, pass: string) {
  await page.goto(`/?org=${boardOrg(t)}`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('#authGate')).toBeVisible(NET);
  await page.locator('#authUser').fill(u);
  await page.locator('#authPass').fill(pass);
  await page.locator('#authSubmit').click();
  await expect(page.locator('#authGate')).toBeHidden(NET);
}

async function enrol(page: any): Promise<{ secret: string; recoveryCodes: string[] }> {
  await page.locator('#twoFaBtn').click();
  await expect(page.locator('#twoFaSecret')).toBeVisible(NET);
  // the QR renders from the deferred cdnjs lib
  await expect(page.locator('#twoFaQr svg')).toBeVisible(NET);
  const secret = ((await page.locator('#twoFaSecret').innerText()) || '').replace(/\s+/g, '');
  await page.locator('#twoFaCode').fill(totp(secret));
  await page.locator('#twoFaSubmit').click();
  await expect(page.locator('#twoFaRecovery li').first()).toBeVisible(NET);
  const recoveryCodes = await page.locator('#twoFaRecovery li').allInnerTexts();
  await page.locator('#twoFaSubmit').click(); // "I've saved these"
  await expect(page.locator('#twoFaPanel')).toBeHidden();
  return { secret, recoveryCodes };
}

test.describe('Two-factor (@twofa)', () => {

  test('2FA-1  enrol, then the next sign-in needs a code', async ({ page }, t) => {
    const pass = 'Two-Factor-Pass-One-1';
    const u = await freshUser(page, t, pass);
    await signIn(page, t, u, pass);

    const { secret } = await enrol(page);
    await expect(page.locator('#twoFaBtn')).toHaveText('Two-factor: on');

    // sign out, sign back in
    await page.locator('#signOutBtn').click();
    await expect(page.locator('#authGate')).toBeVisible(NET);
    await page.locator('#authUser').fill(u);
    await page.locator('#authPass').fill(pass);
    await page.locator('#authSubmit').click();

    // now it asks for the code
    await expect(page.locator('#authOtpRow')).toBeVisible(NET);
    await page.locator('#authOtp').fill(totp(secret));
    await page.locator('#authSubmit').click();
    await expect(page.locator('#authGate')).toBeHidden(NET);
    await expect(page.locator('#leftPane')).toBeVisible();
  });

  test('2FA-2  a recovery code also gets you in', async ({ page, playwright, baseURL }, t) => {
    const pass = 'Two-Factor-Pass-Two-2';
    const u = await freshUser(page, t, pass);
    await signIn(page, t, u, pass);
    const { recoveryCodes } = await enrol(page);

    // drive the recovery-code path over the API from a clean context
    const ctx = await playwright.request.newContext({ baseURL });
    const login = await ctx.post('/api/auth/login', { data: { username: u, password: pass } });
    const ticket = (await login.json()).ticket as string;
    const done = await ctx.post('/api/auth/login/2fa', { data: { ticket, code: recoveryCodes[0] } });
    expect(done.ok()).toBeTruthy();
    expect((await done.json()).accessToken).toBeTruthy();
    await ctx.dispose();
  });

  test('2FA-3  turning it back off stops the code prompt', async ({ page }, t) => {
    const pass = 'Two-Factor-Pass-Three-3';
    const u = await freshUser(page, t, pass);
    await signIn(page, t, u, pass);
    await enrol(page);

    await page.locator('#twoFaBtn').click();
    await expect(page.locator('#twoFaPassword')).toBeVisible(NET);
    await page.locator('#twoFaPassword').fill(pass);
    await page.locator('#twoFaSubmit').click();
    await expect(page.locator('#twoFaPanel')).toBeHidden(NET);
    await expect(page.locator('#twoFaBtn')).toHaveText('Two-factor: off');

    // a bearer login now succeeds in one step
    const r = await page.request.post('/api/auth/login', { data: { username: u, password: pass } });
    expect((await r.json()).accessToken).toBeTruthy();
  });

});
