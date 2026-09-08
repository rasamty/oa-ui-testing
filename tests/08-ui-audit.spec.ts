import { test } from './helpers';
import { auditPage } from './audit';

const APP = '/app/Objective%20Alignment%20v11.html';

/**
 * The deep sweep: walk the app through representative states at phone / tablet /
 * desktop widths, run axe-core and the English check at each, and collect every
 * finding into audit/findings.md. Findings are listed, not fatal — only a page
 * that scrolls sideways fails.
 */
test.describe('UI audit', () => {

  test('default state', { tag: '@audit' }, async ({ page }, testInfo) => {
    await page.goto(APP);
    await auditPage(page, testInfo, { english: true });
  });

  test('objectives mode', { tag: '@audit' }, async ({ page }, testInfo) => {
    await page.goto(APP);
    await page.locator('#modeObj').click();
    await auditPage(page, testInfo, { english: true });
  });

  test('light theme', { tag: '@audit' }, async ({ page }, testInfo) => {
    await page.goto(APP);
    await page.locator('#themeToggle').click();
    await auditPage(page, testInfo);
  });

  const modals: Array<[string, (p: any) => Promise<void>]> = [
    ['create-portfolio', async (p) => { await p.locator('#leftPortfolioSelect').selectOption('__ADD__'); }],
    ['view-and-edit', async (p) => { await p.locator('#managePortfolios').click(); }],
    ['organisation-map', async (p) => { await p.locator('#orgMapEye').last().click(); }],
  ];
  for (const [label, open] of modals) {
    test('modal: ' + label, { tag: '@audit' }, async ({ page }, testInfo) => {
      await page.goto(APP);
      await open(page);
      await auditPage(page, testInfo);
    });
  }

});
