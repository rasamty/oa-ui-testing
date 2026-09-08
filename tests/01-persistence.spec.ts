import { test, expect, openApp, state, flush } from './helpers';

test.describe('Persistence & startup', () => {

  test('US-01  opens with the default layout', { tag: '@US-01' }, async ({ page }) => {
    await openApp(page);

    await expect(page.locator('#leftPane')).toBeVisible();
    await expect(page.locator('#rightPane')).toBeVisible();
    await expect(page.locator('#modePerf')).toHaveClass(/active/);
    await expect(page.locator('#leftList .item')).toHaveCount(1);
    await expect(page.locator('#rightList .item')).toHaveCount(1);
    await expect(page.locator('#err')).toBeHidden();

    const s = await state(page);
    expect(s.UI.mode).toBe('performance');
    expect(s.UI.leftPortfolio).toBe(s.UI.rightPortfolio);   // same portfolio in performance mode
  });

  test('US-02  changes survive a refresh', { tag: '@US-02' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#addObjective').click();
    await expect(page.locator('#rightList .item')).toHaveCount(2);

    await flush(page);            // make sure OPFS agrees with sessionStorage before reloading
    await page.reload();

    await expect(page.locator('#rightList .item')).toHaveCount(2);
  });

  test('US-03  reset returns to defaults and clears saved data', { tag: '@US-03' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#addMetric').click();
    await page.locator('#addObjective').click();
    await expect(page.locator('#leftList .item')).toHaveCount(2);
    await expect(page.locator('#rightList .item')).toHaveCount(2);

    page.once('dialog', (d) => d.accept());     // reset uses a native confirm()
    await page.locator('#resetApp').click();
    await page.waitForLoadState('load');        // wait for the reset's reload to finish

    await expect(page.locator('#leftList .item')).toHaveCount(1);
    await expect(page.locator('#rightList .item')).toHaveCount(1);

    // and it stays reset after another reload
    await page.reload();
    await expect(page.locator('#leftList .item')).toHaveCount(1);
  });

});
