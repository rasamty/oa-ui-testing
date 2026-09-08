import { test as base, expect, type Page } from '@playwright/test';
import { addCoverageReport } from 'monocart-reporter';

const FILES = {
  v11: '/app/Objective%20Alignment%20v11.html',
  v10: '/app/Objective%20Alignment%20v10.html',
} as const;

/**
 * Open the app and force a clean, known starting state.
 * The page persists to sessionStorage, localStorage AND the browser's private
 * file system (OPFS), so we wipe all three, then reload into the built-in defaults.
 */
export async function openApp(page: Page, file: keyof typeof FILES = 'v11') {
  await page.goto(FILES[file]);

  await page.evaluate(async () => {
    try { localStorage.clear(); sessionStorage.clear(); } catch { /* ignore */ }
    try {
      const root: any = await (navigator as any).storage.getDirectory();
      for await (const [name] of root.entries()) {
        try { await root.removeEntry(name, { recursive: true }); } catch { /* ignore */ }
      }
    } catch { /* OPFS not available */ }
  });

  await page.reload();
  await expect(page.locator('#leftList .item')).toHaveCount(1);   // back to defaults
  await expect(page.locator('#rightList .item')).toHaveCount(1);
}

/** The page's own JSON block at the bottom, parsed. Your primary source of truth. */
export async function state(page: Page): Promise<any> {
  return JSON.parse(await page.locator('#jsonView').innerText());
}

/** Force the app to flush its state to OPFS/localStorage right now (it is exposed on window). */
export async function flush(page: Page) {
  await page.evaluate(() => (window as any).OAStatePersistence?.saveNow?.());
  await page.waitForTimeout(200);
}

/** Read the in-memory objects directly when you need something not in the footer. */
export const readOA = (page: Page) => page.evaluate(() => (window as any).OA);
export const readUI = (page: Page) => page.evaluate(() => (window as any).UI);

/** Set a range slider's value deterministically and fire the events the app listens for. */
export async function setRange(locator: any, value: number) {
  await locator.evaluate((el: HTMLInputElement, v: number) => {
    el.value = String(v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }, value);
}

/** Performance mode: hover the first metric row, click the first eligible objective's "+". */
export async function linkFirstPerfPair(page: Page) {
  await page.locator('#leftList .item.metric').first().hover();
  const plus = page.locator('#rightList .item.objective .flashPlus').first();
  await expect(plus).toBeVisible();
  await plus.click();
  await expect(page.locator('svg path.link')).toHaveCount(1);
}

/**
 * US-45 — cross-cutting invariant, applied to every test automatically:
 * the page's hidden error bar must never surface, and the live JSON state
 * block must stay valid JSON for as long as we are on the app.
 */
export const test = base.extend<{ coverage: void; noErrors: void }>({
  // Collect V8 JS coverage on Chromium only and hand it to monocart-reporter.
  // Firefox / WebKit have no page.coverage — they still run the tests, just
  // don't contribute coverage numbers.
  coverage: [
    async ({ page, browserName }, use) => {
      const chromium = browserName === 'chromium';
      if (chromium) {
        await page.coverage.startJSCoverage({ resetOnNavigation: false });
      }
      await use();
      if (chromium) {
        const entries = await page.coverage.stopJSCoverage();
        await addCoverageReport(entries, test.info());
      }
    },
    { auto: true },
  ],

  noErrors: [
    async ({ page }, use) => {
      await use();

      const errShown = await page.locator('#err').isVisible().catch(() => false);
      expect(errShown, 'the #err bar became visible during this test').toBe(false);

      if (page.url().includes('Objective%20Alignment')) {
        const raw = await page.locator('#jsonView').innerText().catch(() => '');
        if (raw.trim()) {
          expect(() => JSON.parse(raw), '#jsonView is not valid JSON').not.toThrow();
        }
      }
    },
    { auto: true },
  ],
});

export { expect };
