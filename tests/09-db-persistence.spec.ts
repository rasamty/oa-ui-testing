import { test, expect, openApp, flush, serverState, setRange, linkFirstPerfPair } from './helpers';

async function clearBrowserCache(page: any) {
  await page.evaluate(async () => {
    try { localStorage.clear(); sessionStorage.clear(); } catch {}
    try {
      const root: any = await (navigator as any).storage.getDirectory();
      for await (const [n] of root.entries()) { try { await root.removeEntry(n, { recursive: true }); } catch {} }
    } catch {}
  });
}

test.describe('Database persistence (@db)', () => {

  test('DB-1  a weight change reaches the database', { tag: '@db' }, async ({ page }) => {
    await openApp(page);

    const slider = page.locator('#rightList .item.objective .sliderRow input[type=range]').first();
    await setRange(slider, 55);
    await flush(page);

    const s = await serverState(page);
    const pf = s.UI.leftPortfolio;
    expect(s.OA.objectivesByPortfolio[pf][0].weight).toBe(55);
  });

  test('DB-2  a new metric and its link are stored, and survive a reload from the DB', { tag: '@db' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#addMetric').click();
    await expect(page.locator('#leftList .item.metric')).toHaveCount(2);
    await linkFirstPerfPair(page);
    await flush(page);

    const s = await serverState(page);
    const pf = s.UI.leftPortfolio;
    expect(s.OA.metricsByPortfolio[pf]).toHaveLength(2);
    expect(s.OA.perfLinksByPortfolio[pf]).toHaveLength(1);
    expect(s.OA.perfLinksByPortfolio[pf][0].strength).toBe(5);

    // wipe every browser cache, reload — the only source left is SQLite
    await clearBrowserCache(page);
    await page.reload();

    await expect(page.locator('#leftList .item.metric')).toHaveCount(2);
    await expect(page.locator('svg path.link')).toHaveCount(1);
  });

  test('DB-3  renaming a portfolio keeps its rows and links', { tag: '@db' }, async ({ page }) => {
    await openApp(page);
    await page.locator('#addObjective').click();
    await linkFirstPerfPair(page);

    await page.locator('#leftPaneTitleLabel').dblclick();
    const editor = page.locator('#leftPane h3 input');
    await editor.fill('R&D');
    await editor.press('Enter');
    await flush(page);

    const s = await serverState(page);
    expect(s.UI.portfolios).toContain('R&D');
    expect(s.UI.portfolios).not.toContain('Portfolio A');
    expect(s.OA.objectivesByPortfolio['R&D']).toHaveLength(2);
    expect(s.OA.perfLinksByPortfolio['R&D']).toHaveLength(1);
  });

  test('DB-4  deleting a metric removes its link rows too', { tag: '@db' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);
    await flush(page);
    expect((await serverState(page)).OA.perfLinksByPortfolio[(await serverState(page)).UI.leftPortfolio]).toHaveLength(1);

    await page.locator('#leftList .item.metric').first().locator('.del').click();
    await expect(page.locator('#leftList .item.metric')).toHaveCount(0);
    await flush(page);

    const s = await serverState(page);
    const pf = s.UI.leftPortfolio;
    expect(s.OA.metricsByPortfolio[pf] ?? []).toHaveLength(0);
    expect(s.OA.perfLinksByPortfolio[pf] ?? []).toHaveLength(0);
  });

  test('DB-5  no "--" and no zero-strength links ever reach the DB', { tag: '@db' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);

    // drive the link strength to 0 -> the app deletes it (confirm dialog)
    page.once('dialog', (d) => d.accept());
    await page.locator('svg path.link').first().dispatchEvent('click');
    await setRange(page.locator('#strengthSlider'), 0);
    await page.locator('#confirmOk').click().catch(() => {});
    await flush(page);

    const raw = JSON.stringify(await serverState(page));
    expect(raw).not.toContain('"strength":0');
    expect(raw).not.toContain('--');
  });

  test('DB-6  the API is the source of truth, not sessionStorage', { tag: '@db' }, async ({ page }) => {
    await openApp(page);

    const name = page.locator('#Root_Org_Name');
    await name.click();
    await page.keyboard.press('Control+A');
    await page.keyboard.type('Persisted Ltd');
    await page.keyboard.press('Enter');
    await expect(name).toHaveText('Persisted Ltd');

    await flush(page);
    expect((await serverState(page))['Root Org Name']).toBe('Persisted Ltd');

    await clearBrowserCache(page);
    await page.reload();
    await expect(page.locator('#Root_Org_Name')).toHaveText('Persisted Ltd');
  });

});
