import { test, expect } from './helpers';
import { TEST_USER, TEST_PASS } from './auth-constants';

// This file tests the signed-OUT experience, so it drops the shared session.
test.use({ storageState: { cookies: [], origins: [] } });

// A login/logout round-trips through the dev server; under the full parallel
// suite that can briefly take longer than the default 5s assertion timeout.
const NET = { timeout: 30_000 };

// Each test gets its own board org (?org=) so board writes land in a private
// SQLite file, not the shared alignment.db that also holds the users table.
const boardOrg = (t: { testId: string }) => 'auth-' + t.testId.slice(0, 16);

async function openSignedOut(page: any, t: { testId: string }) {
  await page.goto(`/?org=${boardOrg(t)}`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('#authGate')).toBeVisible(NET);
}

async function submitLogin(page: any, user: string, pass: string) {
  await page.locator('#authUser').fill(user);
  await page.locator('#authPass').fill(pass);
  await page.locator('#authSubmit').click();
}

test.describe('Authentication (@auth)', () => {

  test('AUTH-1  the sign-in panel blocks the board until you log in', { tag: '@auth' }, async ({ page }, t) => {
    await openSignedOut(page, t);
    await expect(page.locator('#signOutBtn')).toBeHidden();
  });

  test('AUTH-2  a wrong password keeps you on the panel', { tag: '@auth' }, async ({ page }, t) => {
    await openSignedOut(page, t);
    await submitLogin(page, TEST_USER, 'definitely-not-the-password');

    await expect(page.locator('#authErr')).toContainText(/wrong/i, NET);
    await expect(page.locator('#authGate')).toBeVisible();
  });

  test('AUTH-3  a correct sign-in reveals the board', { tag: '@auth' }, async ({ page }, t) => {
    await openSignedOut(page, t);
    await submitLogin(page, TEST_USER, TEST_PASS);

    await expect(page.locator('#authGate')).toBeHidden(NET);
    await expect(page.locator('#signOutBtn')).toBeVisible();
    await expect(page.locator('#leftPane')).toBeVisible();
  });

  test('AUTH-4  the session survives a reload', { tag: '@auth' }, async ({ page }, t) => {
    await openSignedOut(page, t);
    await submitLogin(page, TEST_USER, TEST_PASS);
    await expect(page.locator('#authGate')).toBeHidden(NET);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect(page.locator('#authGate')).toBeHidden(NET);   // still signed in
    await expect(page.locator('#signOutBtn')).toBeVisible();
  });

  test('AUTH-5  signing out brings the panel back', { tag: '@auth' }, async ({ page }, t) => {
    await openSignedOut(page, t);
    await submitLogin(page, TEST_USER, TEST_PASS);
    await expect(page.locator('#authGate')).toBeHidden(NET);

    await page.locator('#signOutBtn').click();
    await expect(page.locator('#authGate')).toBeVisible(NET);  // reloaded, no cookie
  });

  // AUTH-6/7 use a throwaway account each, so changing its password disturbs nothing.
  async function signInAsNewUser(page: any, t: { testId: string }, user: string, pass: string) {
    await page.request.post('/api/test/user', { data: { username: user, password: pass } });
    await page.goto(`/?org=${boardOrg(t)}`, { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#authGate')).toBeVisible(NET);
    await submitLogin(page, user, pass);
    await expect(page.locator('#authGate')).toBeHidden(NET);
  }

  test('AUTH-6  you can change your own password', { tag: '@auth' }, async ({ page }, t) => {
    const u = 'pw6-' + t.testId.slice(0, 12);
    const oldPass = 'Old-Pass-Six-1';
    const newPass = 'New-Pass-Six-2';
    await signInAsNewUser(page, t, u, oldPass);

    await page.locator('#changePwBtn').click();
    await page.locator('#pwCurrent').fill(oldPass);
    await page.locator('#pwNew').fill(newPass);
    await page.locator('#pwConfirm').fill(newPass);
    await page.locator('#pwSubmit').click();
    await expect(page.locator('#pwMsg')).toContainText(/changed/i, NET);

    expect((await page.request.post('/api/auth/login', { data: { username: u, password: oldPass } })).status()).toBe(401);
    expect((await page.request.post('/api/auth/login', { data: { username: u, password: newPass } })).status()).toBe(200);
  });

  test('AUTH-7  the wrong current password is rejected and nothing changes', { tag: '@auth' }, async ({ page }, t) => {
    const u = 'pw7-' + t.testId.slice(0, 12);
    const pass = 'Right-Pass-Seven-1';
    await signInAsNewUser(page, t, u, pass);

    await page.locator('#changePwBtn').click();
    await page.locator('#pwCurrent').fill('not-the-current-password');
    await page.locator('#pwNew').fill('Some-New-Password-9');
    await page.locator('#pwConfirm').fill('Some-New-Password-9');
    await page.locator('#pwSubmit').click();
    await expect(page.locator('#pwMsg')).toContainText(/current password is wrong/i, NET);

    expect((await page.request.post('/api/auth/login', { data: { username: u, password: pass } })).status()).toBe(200);
  });

});
