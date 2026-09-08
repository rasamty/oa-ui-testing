import { test, expect, openApp, state, readOA } from './helpers';

/** Add a portfolio through the "+ Add portfolio" dropdown option. */
async function addPortfolio(page: any, name: string) {
  await page.locator('#leftPortfolioSelect').selectOption('__ADD__');
  await page.locator('#newPortfolioName').fill(name);
  await page.locator('#createSave').click();
  await expect(page.locator('#createPortfolioOverlay')).toBeHidden();
}

test.describe('Portfolio management', () => {

  test('US-14  rename a portfolio from its pane title', { tag: '@US-14' }, async ({ page }) => {
    await openApp(page);

    // give Portfolio A an objective so we can prove the data moves with the rename
    await page.locator('#addObjective').click();
    await expect(page.locator('#rightList .item')).toHaveCount(2);

    await page.locator('#leftPaneTitleLabel').dblclick();
    const editor = page.locator('#leftPane h3 input');
    await editor.fill('Alpha');
    await editor.press('Enter');

    await expect(page.locator('#leftPaneTitleLabel')).toHaveText('Alpha');

    const s = await state(page);
    expect(s.UI.portfolios).toContain('Alpha');
    expect(s.UI.portfolios).not.toContain('Portfolio A');
    expect(s.UI.leftPortfolio).toBe('Alpha');
    expect(s.OA.objectivesByPortfolio['Alpha']).toHaveLength(2);   // data followed the rename
    expect(s.OA.objectivesByPortfolio['Portfolio A']).toBeUndefined();
  });

  test('US-15  reject a conflicting portfolio rename', { tag: '@US-15' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#leftPaneTitleLabel').dblclick();
    const editor = page.locator('#leftPane h3 input');
    await editor.fill('Portfolio B');            // already exists
    await editor.press('Enter');

    await expect(page.locator('#nameConflictOverlay')).toBeVisible();

    await page.locator('#nameConflictCancel').click();
    await expect(page.locator('#nameConflictOverlay')).toBeHidden();
    await expect(page.locator('#leftPaneTitleLabel')).toHaveText('Portfolio A');

    const s = await state(page);
    expect(s.UI.portfolios).toEqual(['Portfolio A', 'Portfolio B']);
  });

  test('US-16  View & Edit: rename one portfolio and delete another', { tag: '@US-16' }, async ({ page }) => {
    await openApp(page);
    await addPortfolio(page, 'Portfolio C');

    await page.locator('#managePortfolios').click();
    await expect(page.locator('#manageOverlay')).toBeVisible();

    const rows = page.locator('#mgrList .mgrItem');
    await expect(rows).toHaveCount(3);
    // rows are in UI.portfolios order: [Portfolio A, Portfolio B, Portfolio C]
    await rows.nth(0).locator('input').fill('Alpha');
    await rows.nth(2).getByRole('button', { name: 'Delete' }).click();

    await page.locator('#mgrSave').click();
    await expect(page.locator('#manageOverlay')).toBeHidden();

    const s = await state(page);
    expect(s.UI.portfolios).toEqual(['Alpha', 'Portfolio B']);
    expect(s.OA.metricsByPortfolio['Portfolio C']).toBeUndefined();
  });

  test('US-17  cannot leave fewer than two portfolios', { tag: '@US-17' }, async ({ page }) => {
    await openApp(page);

    let alertMsg = '';
    page.on('dialog', (d) => { alertMsg = d.message(); d.accept(); });

    await page.locator('#managePortfolios').click();
    const rows = page.locator('#mgrList .mgrItem');
    await rows.first().getByRole('button', { name: 'Delete' }).click();
    await page.locator('#mgrSave').click();

    expect(alertMsg).toContain('two portfolios');
    const oa = await readOA(page);
    const ui = await state(page);
    expect(ui.UI.portfolios).toHaveLength(2);
    expect(oa).toBeTruthy();
  });

});
