import { test, expect, openApp, state } from './helpers';

test.describe('Modes & portfolio selection', () => {

  test('US-08  switch to Objectives mode', { tag: '@US-08' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#modeObj').click();

    await expect(page.locator('#leftPaneTitlePrefix')).toHaveText('Objectives');
    await expect(page.locator('#totalBadgeLeft')).toBeVisible();

    const s = await state(page);
    expect(s.UI.mode).toBe('objectives');
    expect(s.UI.leftPortfolio).not.toBe(s.UI.rightPortfolio);
  });

  test('US-09  switch back to Performance mode', { tag: '@US-09' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#modeObj').click();
    await page.locator('#modePerf').click();

    await expect(page.locator('#leftPaneTitlePrefix')).toHaveText('Metrics');
    await expect(page.locator('#totalBadgeLeft')).toBeHidden();

    const s = await state(page);
    expect(s.UI.mode).toBe('performance');
    expect(s.UI.leftPortfolio).toBe(s.UI.rightPortfolio);
  });

  test('US-10  changing portfolio in Performance mode moves both sides', { tag: '@US-10' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#leftPortfolioSelect').selectOption('Portfolio B');

    await expect(page.locator('#leftPortfolioSelect')).toHaveValue('Portfolio B');
    await expect(page.locator('#rightPortfolioSelect')).toHaveValue('Portfolio B');

    const s = await state(page);
    expect(s.UI.leftPortfolio).toBe('Portfolio B');
    expect(s.UI.rightPortfolio).toBe('Portfolio B');
  });

  test('US-11  the two portfolios stay distinct in Objectives mode', { tag: '@US-11' }, async ({ page }) => {
    await openApp(page);
    await page.locator('#modeObj').click();

    const s = await state(page);
    expect(s.UI.leftPortfolio).not.toBe(s.UI.rightPortfolio);

    // neither dropdown offers the portfolio the other side is on
    const rightOpts = await page.locator('#rightPortfolioSelect option').allTextContents();
    const leftOpts = await page.locator('#leftPortfolioSelect option').allTextContents();
    expect(rightOpts).not.toContain(s.UI.leftPortfolio);
    expect(leftOpts).not.toContain(s.UI.rightPortfolio);
  });

  test('US-12  add a portfolio via the dropdown', { tag: '@US-12' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#leftPortfolioSelect').selectOption('__ADD__');
    await expect(page.locator('#createPortfolioOverlay')).toBeVisible();

    await page.locator('#newPortfolioName').fill('R&D');
    await page.locator('#createSave').click();

    await expect(page.locator('#createPortfolioOverlay')).toBeHidden();
    const s = await state(page);
    expect(s.UI.portfolios).toContain('R&D');
    expect(s.UI.leftPortfolio).toBe('R&D');   // performance mode selects it for both sides
  });

  test('US-13  reject a duplicate portfolio name', { tag: '@US-13' }, async ({ page }) => {
    await openApp(page);

    let alertMsg = '';
    page.on('dialog', (d) => { alertMsg = d.message(); d.accept(); });

    await page.locator('#leftPortfolioSelect').selectOption('__ADD__');
    await page.locator('#newPortfolioName').fill('portfolio a');   // case-insensitive duplicate
    await page.locator('#createSave').click();

    expect(alertMsg).toContain('unique');
    const s = await state(page);
    expect(s.UI.portfolios.filter((p: string) => p.toLowerCase() === 'portfolio a')).toHaveLength(1);
  });

});
