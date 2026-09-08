import { test as base, expect, type Page } from '@playwright/test';

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

export const test = base;
export { expect };
