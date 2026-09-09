import { test, expect } from './helpers';
import { TEST_USER, TEST_PASS } from './auth-constants';

// This file tests the signed-OUT experience, so it drops the shared session.
test.use({ storageState: { cookies: [], origins: [] } });

test.describe('Authentication (@auth)', () => {

  test('AUTH-1  the sign-in panel blocks the board until you log in', { tag: '@auth' }, async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('#authGate')).toBeVisible();
    await expect(page.locator('#signOutBtn')).toBeHidden();
  });

  test('AUTH-2  a wrong password keeps you on the panel', { tag: '@auth' }, async ({ page }) => {
    await page.goto('/');
    await page.locator('#authUser').fill(TEST_USER);
    await page.locator('#authPass').fill('definitely-not-the-password');
    await page.locator('#authSubmit').click();

    await expect(page.locator('#authErr')).toBeVisible();
    await expect(page.locator('#authErr')).toContainText(/wrong/i);
    await expect(page.locator('#authGate')).toBeVisible();
  });

  test('AUTH-3  a correct sign-in reveals the board', { tag: '@auth' }, async ({ page }) => {
    await page.goto('/');
    await page.locator('#authUser').fill(TEST_USER);
    await page.locator('#authPass').fill(TEST_PASS);
    await page.locator('#authSubmit').click();

    await expect(page.locator('#authGate')).toBeHidden();
    await expect(page.locator('#signOutBtn')).toBeVisible();
    await expect(page.locator('#leftPane')).toBeVisible();
  });

  test('AUTH-4  the session survives a reload', { tag: '@auth' }, async ({ page }) => {
    await page.goto('/');
    await page.locator('#authUser').fill(TEST_USER);
    await page.locator('#authPass').fill(TEST_PASS);
    await page.locator('#authSubmit').click();
    await expect(page.locator('#authGate')).toBeHidden();

    await page.reload();
    await expect(page.locator('#authGate')).toBeHidden();      // still signed in
    await expect(page.locator('#signOutBtn')).toBeVisible();
  });

  test('AUTH-5  signing out brings the panel back', { tag: '@auth' }, async ({ page }) => {
    await page.goto('/');
    await page.locator('#authUser').fill(TEST_USER);
    await page.locator('#authPass').fill(TEST_PASS);
    await page.locator('#authSubmit').click();
    await expect(page.locator('#authGate')).toBeHidden();

    await page.locator('#signOutBtn').click();
    await expect(page.locator('#authGate')).toBeVisible();     // reloaded, no cookie
  });

});
