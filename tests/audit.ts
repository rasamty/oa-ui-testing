import { expect, type Page, type TestInfo } from '@playwright/test';
import { AxeBuilder } from '@axe-core/playwright';
import SpellChecker from 'simple-spellchecker';
import * as fs from 'node:fs';
import * as path from 'node:path';

export type Finding = {
  kind: 'responsive' | 'layout' | 'a11y' | 'english';
  level: 'error' | 'warn';
  detail: string;
  where?: string;
  viewport?: string;
};

const RAW = path.join(process.cwd(), 'audit', 'raw');

// Words that are correct for this app even though a plain dictionary flags them.
const ALLOW = new Set([
  'objective', 'objectives', 'portfolio', 'portfolios', 'organox', 'json',
  'repriori', 'dropdown', 'checkbox', 'unlinked', 'sublayer', 'metrics',
]);

/* 1 — responsiveness + layout, measured inside the page ----------------- */
async function layoutFindings(page: Page, viewport: string): Promise<Finding[]> {
  try {
    return await page.evaluate((vp: string) => {
      const F: any[] = [];
      const vw = window.innerWidth;
      const seg = (n: Element) =>
        n.nodeName.toLowerCase() +
        (typeof (n as HTMLElement).className === 'string' && (n as HTMLElement).className.trim()
          ? '.' + (n as HTMLElement).className.trim().split(/\s+/).slice(0, 2).join('.')
          : '');
      const cssPath = (el: Element) => {
        if ((el as HTMLElement).id) return '#' + (el as HTMLElement).id;
        const p: string[] = [];
        let n: Element | null = el;
        while (n && n.nodeType === 1 && p.length < 4) { p.unshift(seg(n)); n = n.parentElement; }
        return p.join(' > ');
      };
      const shown = (el: Element) => {
        const r = el.getBoundingClientRect();
        const s = getComputedStyle(el);
        return r.width > 1 && r.height > 1 && s.visibility !== 'hidden' && s.display !== 'none' && +s.opacity > 0.05;
      };

      // (a) the page must never scroll sideways
      if (document.documentElement.scrollWidth > vw + 1) {
        F.push({
          kind: 'responsive', level: 'error', viewport: vp,
          detail: 'Page scrolls sideways: ' + document.documentElement.scrollWidth + 'px of content in a ' + vw + 'px viewport.',
        });
      }

      // (b) text cut off by its own container
      document.querySelectorAll<HTMLElement>('body *').forEach((el) => {
        if (el.children.length || !shown(el) || !(el.textContent || '').trim()) return;
        const s = getComputedStyle(el);
        if ((s.overflowX === 'hidden' || s.textOverflow === 'ellipsis') && el.scrollWidth > el.clientWidth + 2) {
          F.push({
            kind: 'layout', level: 'warn', viewport: vp, where: cssPath(el),
            detail: 'Text is clipped: "' + (el.textContent || '').trim().slice(0, 45) + '..."',
          });
        }
      });

      // (c) two interactive elements sitting on top of each other
      const hot = [...document.querySelectorAll('a,button,select,input,[role=button],.chk,.del,.flashPlus,.icon,.iconBtn')]
        .filter(shown)
        .map((el) => ({ el, r: el.getBoundingClientRect() }));
      for (let i = 0; i < hot.length; i++) {
        for (let j = i + 1; j < hot.length; j++) {
          const a = hot[i], b = hot[j];
          if (a.el.contains(b.el) || b.el.contains(a.el)) continue;
          const ix = Math.max(0, Math.min(a.r.right, b.r.right) - Math.max(a.r.left, b.r.left));
          const iy = Math.max(0, Math.min(a.r.bottom, b.r.bottom) - Math.max(a.r.top, b.r.top));
          const over = ix * iy;
          const small = Math.min(a.r.width * a.r.height, b.r.width * b.r.height);
          if (small > 0 && over > small * 0.3) {
            F.push({
              kind: 'layout', level: 'warn', viewport: vp,
              where: cssPath(a.el) + '  X  ' + cssPath(b.el),
              detail: 'Two clickable elements overlap by ' + Math.round((over / small) * 100) + '%.',
            });
          }
        }
      }

      // (d) tap targets too small on a phone
      if (vw <= 480) {
        hot.forEach(({ el, r }) => {
          if (r.width < 24 || r.height < 24) {
            F.push({
              kind: 'responsive', level: 'warn', viewport: vp, where: cssPath(el),
              detail: 'Tap target only ' + Math.round(r.width) + 'x' + Math.round(r.height) + 'px (aim for at least 24x24).',
            });
          }
        });
      }

      return F;
    }, viewport);
  } catch {
    return [];
  }
}

/* 2 — accessibility, via axe-core ------------------------------------- */
async function a11yFindings(page: Page, viewport: string): Promise<Finding[]> {
  try {
    const { violations } = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    return violations.map((v) => ({
      kind: 'a11y' as const,
      level: 'warn' as const,
      viewport,
      where: v.nodes[0]?.target?.join(' '),
      detail: v.help + ' (' + v.id + ', impact: ' + (v.impact || 'n/a') + ')',
    }));
  } catch {
    return [];
  }
}

/* 3 — English: spelling + common copy mistakes ---------------------- */
let dict: any;
async function englishFindings(page: Page): Promise<Finding[]> {
  try {
    dict ??= await new Promise((ok, no) =>
      SpellChecker.getDictionary('en-GB', (e: any, d: any) => (e ? no(e) : ok(d))),
    );
  } catch {
    return [];
  }

  const texts: string[] = await page.evaluate(() => {
    const set = new Set<string>();
    // Skip machine text: the live JSON state block, and any code / pre / script.
    const SKIP = 'pre, code, script, style, #jsonView';
    document.querySelectorAll('body *').forEach((el) => {
      if ((el as HTMLElement).offsetParent === null && el.tagName !== 'BODY') return;
      if (el.closest(SKIP)) return;
      el.childNodes.forEach((n) => {
        if (n.nodeType === 3) {
          const t = (n.textContent || '').replace(/\s+/g, ' ').trim();
          if (t.length >= 4 && /[a-z]/i.test(t)) set.add(t);
        }
      });
    });
    return [...set];
  });

  const F: Finding[] = [];
  for (const t of texts) {
    if (/ {2,}/.test(t)) F.push({ kind: 'english', level: 'warn', detail: 'Double space: "' + t + '"' });
    if (/\s[,.;:!?]/.test(t)) F.push({ kind: 'english', level: 'warn', detail: 'Space before punctuation: "' + t + '"' });
    const rep = t.match(/\b(\w+)\s+\1\b/i);
    if (rep) F.push({ kind: 'english', level: 'warn', detail: 'Repeated word "' + rep[1] + '": "' + t + '"' });
    for (const w of t.split(/[^A-Za-z']+/)) {
      if (w.length < 3 || ALLOW.has(w.toLowerCase()) || /^[A-Z]/.test(w)) continue; // skip short / allow-listed / Proper Nouns
      if (dict.isMisspelled(w)) {
        F.push({ kind: 'english', level: 'warn', detail: 'Possible misspelling "' + w + '" in: "' + t + '"' });
      }
    }
  }
  return F;
}

/* helpers ---------------------------------------------------------- */
function record(info: TestInfo, findings: Finding[], suffix = '') {
  if (!findings.length) return;
  for (const f of findings) {
    info.annotations.push({ type: 'audit:' + f.kind + ':' + f.level, description: f.detail });
  }
  fs.mkdirSync(RAW, { recursive: true });
  fs.writeFileSync(
    path.join(RAW, info.testId + suffix + '.json'),
    JSON.stringify({ test: info.titlePath.join(' > '), findings }),
  );
}

/* 4 — the two entry points --------------------------------------- */
type Opts = { english?: boolean; viewports?: Array<[string, number, number]> };
const DEFAULT_VPS: Array<[string, number, number]> = [
  ['mobile', 375, 812],
  ['tablet', 768, 1024],
  ['desktop', 1280, 900],
];

/** The deep sweep: every viewport, plus axe, plus (optionally) English. */
export async function auditPage(page: Page, info: TestInfo, opts: Opts = {}) {
  const vps = opts.viewports ?? DEFAULT_VPS;
  const found: Finding[] = [];

  for (const [name, w, h] of vps) {
    await page.setViewportSize({ width: w, height: h });
    await page.waitForTimeout(120);
    found.push(...(await layoutFindings(page, name)));
    found.push(...(await a11yFindings(page, name)));
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  if (opts.english) found.push(...(await englishFindings(page)));

  const uniq = [...new Map(found.map((f) => [f.kind + f.detail + (f.where ?? ''), f])).values()];
  record(info, uniq);

  const errors = uniq.filter((f) => f.level === 'error');
  expect.soft(errors, '\n' + errors.map((e) => '- ' + e.detail).join('\n')).toHaveLength(0);
  return uniq;
}

/** The cheap check run after every test, at the current viewport. */
export async function quickAudit(page: Page, info: TestInfo) {
  const f = await layoutFindings(page, 'current');
  record(info, f, '.quick');
  expect.soft(f.filter((x) => x.level === 'error'), 'layout broke during this test').toHaveLength(0);
}
