import { test, expect } from '@playwright/test';

test('the app is up and serves the page', async ({ page }) => {
  const health = await page.request.get('/health');
  expect(health.ok()).toBe(true);
  expect(await health.json()).toEqual({ status: 'ok' });

  await page.goto('/');
  await expect(page).toHaveTitle(/Portfolio Alignment/);
  await expect(page.locator('#leftList .item')).toHaveCount(1);
  await expect(page.locator('#err')).toBeHidden();
});
