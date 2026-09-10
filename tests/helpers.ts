import { test as base, expect, type Page } from '@playwright/test';
import { addCoverageReport } from 'monocart-reporter';
import { TEST_USER, TEST_PASS } from './auth-constants';

/**
 * Each test gets its own organisation id (stable per test, unique across the
 * parallel workers) so tests never share a database row set. The ?org= is
 * honoured by the server only when Alignment:TestMode is on.
 */
function testOrg(): string {
  return 'test-' + base.info().testId;
}

/**
 * Open the app and force a clean, known starting state: sign in through the panel
 * for this test, wipe this test's org, clear the browser's offline caches, reload.
 * The sign-in panel must never appear after this.
 *
 * Signing in through the UI resolves the page's own startup await and lands the
 * (single-use, rotating) refresh cookie on THIS test's context. Each test has its
 * own context, so the rotation never races another test.
 */
export async function openApp(page: Page) {
  const org = testOrg();
  await page.request.post(`/api/test/reset?org=${org}`);
  await page.goto(`/?org=${org}`);

  await expect(page.locator('#authGate')).toBeVisible();
  await page.locator('#authUser').fill(TEST_USER);
  await page.locator('#authPass').fill(TEST_PASS);
  await page.locator('#authSubmit').click();
  await expect(page.locator('#authGate'), 'sign-in should succeed').toBeHidden();

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
  await expect(page.locator('#authGate'), 'the test session should keep us signed in').toBeHidden();
  await expect(page.locator('#leftList .item')).toHaveCount(1);   // built-in defaults
  await expect(page.locator('#rightList .item')).toHaveCount(1);
}

/** The page's own JSON block at the bottom, parsed. In-memory truth. */
export async function state(page: Page): Promise<any> {
  return JSON.parse(await page.locator('#jsonView').innerText());
}

/**
 * What the server (i.e. the database) currently holds for this test's org.
 * Uses the TestMode-only /api/test/state so we don't have to thread the in-page
 * access token through page.request.
 */
export function serverState(page: Page): Promise<any> {
  return page.request.get(`/api/test/state?org=${testOrg()}`).then((r) => r.json());
}

/** Force an immediate PUT of the current state and wait for it to land. */
export async function flush(page: Page) {
  const ok = await page.evaluate(() => (window as any).serverSync?.flushNow?.());
  expect(ok, 'serverSync.flushNow() PUT did not succeed').toBe(true);
}

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
 * block must stay valid JSON while we are on the app.
 */
export const test = base.extend<{ coverage: void; noErrors: void }>({
  coverage: [
    async ({ page, browserName }, use) => {
      const chromium = browserName === 'chromium';
      if (chromium) await page.coverage.startJSCoverage({ resetOnNavigation: false });
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

      if (await page.locator('#jsonView').count()) {
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
