import { test, expect, openApp, state, setRange, linkFirstPerfPair, flush } from './helpers';

test.describe('Metric & objective rows', () => {

  test('US-18  add a metric with a unique name, and it persists', { tag: '@US-18' }, async ({ page }) => {
    await openApp(page);
    await page.locator('#addMetric').click();

    await expect(page.locator('#leftList .item.metric')).toHaveCount(2);
    const s = await state(page);
    const pf = s.UI.leftPortfolio;
    const labels = s.OA.metricsByPortfolio[pf].map((m: any) => m.label);
    // the app names the first extra "Metric" (base name is free), then "Metric 2", ...
    expect(labels).toEqual(['Metric 1', 'Metric']);
    expect(new Set(labels).size).toBe(labels.length);   // still unique

    await flush(page);
    await page.reload();
    await expect(page.locator('#leftList .item.metric')).toHaveCount(2);
  });

  test('US-19  add an objective at 0% weight', { tag: '@US-19' }, async ({ page }) => {
    await openApp(page);
    await page.locator('#addObjective').click();

    await expect(page.locator('#rightList .item.objective')).toHaveCount(2);
    const s = await state(page);
    const pf = s.UI.rightPortfolio;
    expect(s.OA.objectivesByPortfolio[pf][1].weight).toBe(0);
  });

  test('US-20  rename a row', { tag: '@US-20' }, async ({ page }) => {
    await openApp(page);
    const label = page.locator('#leftList .item.metric .label').first();

    await label.dblclick();
    await label.fill('Revenue');
    await label.evaluate((el) => (el as HTMLElement).blur());

    await expect(label).toHaveText('Revenue');
    const s = await state(page);
    expect(s.OA.metricsByPortfolio[s.UI.leftPortfolio][0].label).toBe('Revenue');
  });

  test('US-21  reject a duplicate row name', { tag: '@US-21' }, async ({ page }) => {
    await openApp(page);
    await page.locator('#addMetric').click();

    let alertMsg = '';
    page.on('dialog', (d) => { alertMsg = d.message(); d.accept(); });

    const label = page.locator('#leftList .item.metric .label').nth(1);   // the 2nd metric, "Metric"
    await label.dblclick();
    await label.fill('Metric 1');
    await label.evaluate((el) => (el as HTMLElement).blur());

    expect(alertMsg).toContain('unique');
    await expect(page.locator('#leftList .item.metric .label').nth(1)).toHaveText('Metric');   // reverted
  });

  test('US-22  deactivate a row', { tag: '@US-22' }, async ({ page }) => {
    await openApp(page);
    const row = page.locator('#leftList .item.metric').first();

    await row.locator('.chk').click();

    await expect(row).toHaveClass(/inactive/);
    await expect(row.locator('.label')).toHaveClass(/locked/);
    const s = await state(page);
    expect(s.OA.metricsByPortfolio[s.UI.leftPortfolio][0].active).toBe(false);
  });

  test('US-23  delete a metric removes its links too', { tag: '@US-23' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);

    const before = await state(page);
    expect(before.OA.perfLinksByPortfolio[before.UI.leftPortfolio]).toHaveLength(1);

    await page.locator('#leftList .item.metric').first().locator('.del').click();

    await expect(page.locator('#leftList .item.metric')).toHaveCount(0);
    await expect(page.locator('svg path.link')).toHaveCount(0);
    const after = await state(page);
    expect(after.OA.perfLinksByPortfolio[after.UI.leftPortfolio]).toHaveLength(0);
  });

  test('US-24  delete an objective removes its links too', { tag: '@US-24' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);

    await page.locator('#rightList .item.objective').first().locator('.del').click();

    await expect(page.locator('#rightList .item.objective')).toHaveCount(0);
    await expect(page.locator('svg path.link')).toHaveCount(0);
    const after = await state(page);
    expect(after.OA.perfLinksByPortfolio[after.UI.leftPortfolio]).toHaveLength(0);
  });

  test('US-25  moving the weight slider updates the % and the total badge', { tag: '@US-25' }, async ({ page }) => {
    await openApp(page);
    const slider = page.locator('#rightList .item.objective .sliderRow input[type=range]').first();

    await setRange(slider, 50);

    await expect(page.locator('#rightList .item.objective .weightPct').first()).toHaveText('50%');
    await expect(page.locator('#totalBadge')).toHaveText('50%');
  });

  test('US-26  weights never push a pane total over 100%', { tag: '@US-26' }, async ({ page }) => {
    await openApp(page);
    await page.locator('#addObjective').click();

    const s1 = page.locator('#rightList .item.objective .sliderRow input[type=range]').nth(0);
    const s2 = page.locator('#rightList .item.objective .sliderRow input[type=range]').nth(1);

    await setRange(s1, 100);
    await setRange(s2, 50);            // would make 150 — must be capped

    const s = await state(page);
    const weights = s.OA.objectivesByPortfolio[s.UI.rightPortfolio].map((o: any) => o.weight);
    expect(weights.reduce((a: number, b: number) => a + b, 0)).toBeLessThanOrEqual(100);
    expect(weights[1]).toBe(0);
  });

  test('US-27  total badge is green at exactly 100%, red otherwise', { tag: '@US-27' }, async ({ page }) => {
    await openApp(page);
    const slider = page.locator('#rightList .item.objective .sliderRow input[type=range]').first();

    await setRange(slider, 100);
    await expect(page.locator('#totalBadge')).toHaveClass(/green/);

    await setRange(slider, 55);
    await expect(page.locator('#totalBadge')).toHaveClass(/red/);
  });

});
