import { test, expect, openApp, state, setRange, linkFirstPerfPair } from './helpers';

test.describe('Linking & link editing', () => {

  test('US-28  hovering an eligible metric reveals the "+" targets', { tag: '@US-28' }, async ({ page }) => {
    await openApp(page);
    await page.locator('#leftList .item.metric').first().hover();

    await expect(page.locator('#leftList .item.metric .flashPlus').first()).toBeVisible();
    await expect(page.locator('#rightList .item.objective .flashPlus').first()).toBeVisible();
  });

  test('US-29  clicking a "+" creates a link at 5% strength', { tag: '@US-29' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);

    const s = await state(page);
    const links = s.OA.perfLinksByPortfolio[s.UI.leftPortfolio];
    expect(links).toHaveLength(1);
    expect(links[0].strength).toBe(5);
    await expect(page.locator('svg path.link')).toHaveCount(1);
  });

  test('US-30  a metric at 100% capacity shows no "+"', { tag: '@US-30' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);

    await page.locator('svg path.link').first().dispatchEvent('click');
    await expect(page.locator('#strengthModal')).toBeVisible();
    await setRange(page.locator('#strengthSlider'), 100);
    await page.waitForTimeout(900);                 // clear the popover's "sticky" window
    await page.mouse.click(3, 3);                    // close it

    await page.locator('#leftList .item.metric').first().hover();
    await expect(page.locator('#leftList .item.metric .flashPlus').first()).toBeHidden();
  });

  test('US-31  the balance flag shows when a linked metric is not at 100%', { tag: '@US-31' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);        // link sits at 5%, not 100

    await expect(page.locator('#balanceFlag')).toBeVisible();
  });

  test('US-32  create an objective-to-objective link in Objectives mode', { tag: '@US-32' }, async ({ page }) => {
    await openApp(page);
    await page.locator('#modeObj').click();

    await page.locator('#leftList .item.objectiveL').first().hover();
    const rightPlus = page.locator('#rightList .item.objective .flashPlus').first();
    await expect(rightPlus).toBeVisible();
    await rightPlus.click();

    const s = await state(page);
    const key = `${s.UI.leftPortfolio}→${s.UI.rightPortfolio}`;
    expect(s.OA.ooLinksByPair[key]).toHaveLength(1);
    await expect(page.locator('svg path.link')).toHaveCount(1);
  });

  test.fixme('US-33  reverse-relationship guard', { tag: '@US-33' }, async ({ page }) => {
    // Needs left=B / right=A with A->B links already present. Reaching that portfolio
    // arrangement through the dropdowns takes a 3-portfolio shuffle — revisit and
    // stabilise before enabling.
    await openApp(page);
  });

  test('US-34  clicking a link opens the strength control and it updates the link', { tag: '@US-34' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);

    await page.locator('svg path.link').first().dispatchEvent('click');
    await expect(page.locator('#strengthModal')).toBeVisible();

    await setRange(page.locator('#strengthSlider'), 25);
    await expect(page.locator('#strengthValue')).toHaveText('25%');

    const s = await state(page);
    expect(s.OA.perfLinksByPortfolio[s.UI.leftPortfolio][0].strength).toBe(25);
  });

  test('US-35  dragging strength to 0% prompts to delete the link', { tag: '@US-35' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);

    await page.locator('svg path.link').first().dispatchEvent('click');
    await expect(page.locator('#strengthModal')).toBeVisible();
    await setRange(page.locator('#strengthSlider'), 0);

    await expect(page.locator('#confirmOverlay')).toBeVisible();
  });

  test('US-36  Ctrl+click a link and confirm deletes it', { tag: '@US-36' }, async ({ page }) => {
    await openApp(page);
    await linkFirstPerfPair(page);

    await page.locator('svg path.link').first().dispatchEvent('click', { ctrlKey: true });
    await expect(page.locator('#confirmOverlay')).toBeVisible();
    await page.locator('#confirmOk').click();

    await expect(page.locator('svg path.link')).toHaveCount(0);
    const s = await state(page);
    expect(s.OA.perfLinksByPortfolio[s.UI.leftPortfolio]).toHaveLength(0);
  });

  test('US-37  a link is not drawn while an endpoint is scrolled out of its pane', { tag: '@US-37' }, async ({ page }) => {
    await openApp(page);
    // enough objectives that the right pane scrolls
    for (let i = 0; i < 18; i++) await page.locator('#addObjective').click();
    await linkFirstPerfPair(page);
    await expect(page.locator('svg path.link')).toHaveCount(1);

    // scroll the first objective (the link's right end) out of view
    await page.locator('#rightPane').evaluate((el) => (el.scrollTop = el.scrollHeight));
    await expect(page.locator('svg path.link')).toHaveCount(0);

    await page.locator('#rightPane').evaluate((el) => (el.scrollTop = 0));
    await expect(page.locator('svg path.link')).toHaveCount(1);
  });

});
