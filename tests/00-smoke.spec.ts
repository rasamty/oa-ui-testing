import { test, expect } from '@playwright/test';

test('the page loads and shows its title', async ({ page }) => {
  await page.goto('/app/Objective%20Alignment%20v11.html');
  await expect(page).toHaveTitle(/Portfolio Alignment/);
  await expect(page.locator('#leftList .item')).toHaveCount(1);
  await expect(page.locator('#err')).toBeHidden();
});
