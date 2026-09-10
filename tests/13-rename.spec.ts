import { test, expect } from './helpers';

// Change-your-own-username. Throwaway account per test + browser (the name is the
// thing under test and the same test runs on all 3 browsers).
test.use({ storageState: { cookies: [], origins: [] } });

const NET = { timeout: 30_000 };
const boardOrg = (t: any) => `rn-${t.project.name.slice(0, 3)}-${t.testId.slice(0, 12)}`;

async function signInAsNew(page: any, t: any, pass: string) {
  const u = `rn-${t.project.name.slice(0, 3)}-${t.title.replace(/[^a-z0-9]+/gi, '').slice(0, 8)}-${t.testId.slice(0, 6)}`;
  await page.request.post('/api/test/user', { data: { username: u, password: pass } });
  await page.goto(`/?org=${boardOrg(t)}`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('#authGate')).toBeVisible(NET);
  await page.locator('#authUser').fill(u);
  await page.locator('#authPass').fill(pass);
  await page.locator('#authSubmit').click();
  await expect(page.locator('#authGate')).toBeHidden(NET);
  return u;
}

test.describe('Change username (@rename)', () => {

  test('RN-1  renaming updates the top bar and the new name signs in', async ({ page }, t) => {
    const pass = 'Rename-Me-Pass-One-1';
    const u = await signInAsNew(page, t, pass);
    // unique per run — the test DB is not wiped between runs
    const renamed = `${u}-${Date.now().toString(36).slice(-5)}`;

    await expect(page.locator('#whoami')).toHaveText(`Signed in as ${u}`);
    await page.locator('#whoami').click();
    await page.locator('#nameNew').fill(renamed);
    await page.locator('#namePass').fill(pass);
    await page.locator('#nameSubmit').click();
    await expect(page.locator('#nameMsg')).toContainText(/changed/i, NET);
    await expect(page.locator('#whoami')).toHaveText(`Signed in as ${renamed}`, NET);

    // old name gone, new name works
    expect((await page.request.post('/api/auth/login', { data: { username: u, password: pass } })).status()).toBe(401);
    expect((await page.request.post('/api/auth/login', { data: { username: renamed, password: pass } })).status()).toBe(200);

    // and the board still works under the new session
    await expect(page.locator('#leftPane')).toBeVisible();
  });

  test('RN-2  a wrong password is rejected', async ({ page }, t) => {
    const pass = 'Rename-Me-Pass-Two-2';
    await signInAsNew(page, t, pass);

    await page.locator('#whoami').click();
    await page.locator('#nameNew').fill('whatever-new-name');
    await page.locator('#namePass').fill('not-the-password');
    await page.locator('#nameSubmit').click();
    await expect(page.locator('#nameMsg')).toContainText(/password is wrong/i, NET);
  });

});
