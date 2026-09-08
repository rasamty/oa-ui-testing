import * as XLSX from 'xlsx';
import { test, expect, openApp, state } from './helpers';

test.describe('Reorder, resize, navigation, map, export', () => {

  test.fixme('US-38  reorder rows by dragging', { tag: '@US-38' }, async ({ page }) => {
    // The app arms `draggable` on pointerdown of the handle and then relies on
    // native HTML5 drag events. Playwright's dragTo does not reliably trigger it;
    // needs a synthetic DragEvent helper. Revisit.
    await openApp(page);
  });

  test('US-39  resize the left pane within its limits', { tag: '@US-39' }, async ({ page }) => {
    await openApp(page);
    const handle = page.locator('#leftResizeHandle');
    const box = await handle.boundingBox();
    if (!box) throw new Error('resize handle has no box');

    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x - 120, box.y + box.height / 2, { steps: 12 });
    await page.mouse.up();

    const w = await page.locator('.grid').evaluate((el) =>
      getComputedStyle(el).getPropertyValue('--leftPaneW').trim(),
    );
    expect(w).toMatch(/px$/);           // a pixel width was committed
    expect(parseFloat(w)).toBeGreaterThan(0);
  });

  test('US-40  navigate to the Performance page', { tag: '@US-40' }, async ({ page }) => {
    await openApp(page);

    // The real Performance page is not part of this suite. Intercept the navigation
    // and fulfil it with a throwaway page that echoes what the app handed over.
    await page.route(/Performance.*V3\.html/, (route) =>
      route.fulfill({
        contentType: 'text/html',
        body:
          '<!doctype html><title>Performance</title><span id="pf"></span>' +
          "<script>document.getElementById('pf').textContent=" +
          "(JSON.parse(sessionStorage.getItem('oa_ui_state')||'{}').leftPortfolio)||'';</script>",
      }),
    );

    await page.locator('#navigate_to_Performance_page').click();
    await expect(page.locator('#portfolioPickerOverlay')).toBeVisible();

    const picker = page.locator('#portfolioPickerSelect');
    await picker.selectOption('Portfolio B');
    await expect(picker).toHaveValue('Portfolio B');

    await page.locator('#ppNext').click();
    await expect(page.locator('#portfolioConfirmOverlay')).toBeVisible();
    await expect(page.locator('#portfolioConfirmBody')).toContainText('Portfolio B');

    await Promise.all([
      page.waitForURL(/Performance.*V3\.html/),
      page.locator('#pcConfirm').click(),
    ]);

    expect(page.url()).toContain('Performance');
    await expect(page.locator('#pf')).toHaveText('Portfolio B');   // the selected portfolio was saved before navigating
  });

  test('US-41  cancelling the navigation keeps you on the page', { tag: '@US-41' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#navigate_to_Performance_page').click();
    await expect(page.locator('#portfolioPickerOverlay')).toBeVisible();
    await page.locator('#ppCancel').click();

    await expect(page.locator('#portfolioPickerOverlay')).toBeHidden();
    expect(page.url()).toContain('Objective%20Alignment');
  });

  test('US-42  open the relationship map', { tag: '@US-42' }, async ({ page }) => {
    await openApp(page);

    // create one objective-to-objective link so the map has something to show
    await page.locator('#modeObj').click();
    await page.locator('#leftList .item.objectiveL').first().hover();
    await page.locator('#rightList .item.objective .flashPlus').first().click();

    await page.locator('#orgMapEye').last().click();
    await expect(page.locator('#orgMapOverlay')).toBeVisible();
    await expect(page.locator('#orgMapOverlay')).toContainText('Organization Relationship Map');
    await expect(page.locator('#orgMapCanvas svg')).toBeVisible();

    // the legend lists every portfolio
    const legend = page.locator('#orgMapCanvas table');
    await expect(legend).toContainText('Portfolio A');
    await expect(legend).toContainText('Portfolio B');
  });

  test.fixme('US-43  empty map message', { tag: '@US-43' }, async ({ page }) => {
    // v11's active map renderer always lists UI.portfolios as nodes, so "nothing
    // to display" may not apply. Confirm intended behaviour, then assert on the
    // absence of connections instead.
    await openApp(page);
  });

  test('US-44  export the workbook with the expected sheets', { tag: '@US-44' }, async ({ page }) => {
    await openApp(page);

    await page.locator('#Root_Org_Name').click();
    await page.locator('#Root_Org_Name').fill('Acme Health');
    await page.locator('#Root_Org_Name').press('Enter');

    await page.locator('#orgMapEye').last().click();
    await expect(page.locator('#orgMapOverlay')).toBeVisible();

    const [download] = await Promise.all([
      page.waitForEvent('download'),
      page.locator('#Save_Workbook').first().click(),
    ]);

    expect(download.suggestedFilename()).toMatch(/Acme Health\.xlsx$/);

    const file = await download.path();
    const wb = XLSX.readFile(file);
    expect(wb.SheetNames).toEqual(
      expect.arrayContaining([
        'OBJ-OBJ Align',
        'Metric-OBJ Align',
        'ReadMe',
        'Portfolio Mind Map',
        'OBJ Weight',
      ]),
    );
  });

});
