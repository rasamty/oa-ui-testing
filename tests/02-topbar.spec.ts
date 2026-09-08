import { test, expect, openApp, state } from './helpers';

test.describe('Top bar — organisation name & theme', () => {

  test('US-04  rename the organisation, and it persists', { tag: '@US-04' }, async ({ page }) => {
    await openApp(page);
    const name = page.locator('#Root_Org_Name');

    await name.click();                 // turns the span editable
    await name.fill('Acme Health');     // clear + type
    await name.press('Enter');          // commit

    await expect(name).toHaveText('Acme Health');
    await expect(page).toHaveTitle(/Acme Health/);

    const s = await state(page);
    expect(s['Root Org Name']).toBe('Acme Health');

    await page.reload();
    await expect(page.locator('#Root_Org_Name')).toHaveText('Acme Health');
  });

  test('US-05  an empty name falls back to "My Organisation"', { tag: '@US-05' }, async ({ page }) => {
    await openApp(page);
    const name = page.locator('#Root_Org_Name');

    await name.click();
    await name.fill('');
    await name.press('Enter');

    await expect(name).toHaveText('My Organisation');
  });

  test('US-06  Escape cancels the rename', { tag: '@US-06' }, async ({ page }) => {
    await openApp(page);
    const name = page.locator('#Root_Org_Name');

    await name.click();
    await name.fill('This should not stick');
    await name.press('Escape');

    await expect(name).toHaveText('My Organisation');
    const s = await state(page);
    expect(s['Root Org Name']).toBe('My Organisation');
  });

  test('US-07  theme toggle switches and persists', { tag: '@US-07' }, async ({ page }) => {
    await openApp(page);
    const html = page.locator('html');

    await expect(html).toHaveAttribute('data-theme', 'dark');   // default

    await page.locator('#themeToggle').click();
    await expect(html).toHaveAttribute('data-theme', 'light');

    const theme = await page.evaluate(() => sessionStorage.getItem('oa_theme'));
    expect(theme).toBe('light');

    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  });

});
