import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * globalTeardown — runs once after the whole suite. Rolls every per-test audit
 * finding into a single grouped audit/findings.md, then clears the raw folder.
 */
export default async function () {
  const raw = path.join(process.cwd(), 'audit', 'raw');
  if (!fs.existsSync(raw)) return;

  const rows = fs
    .readdirSync(raw)
    .filter((f) => f.endsWith('.json'))
    .flatMap((f) => {
      try {
        const { test, findings } = JSON.parse(fs.readFileSync(path.join(raw, f), 'utf8'));
        return (findings as any[]).map((x) => ({ test, ...x }));
      } catch {
        return [];
      }
    });

  const pick = (k: string) => rows.filter((r) => r.kind === k);
  const block = (title: string, list: any[]) =>
    list.length
      ? '\n## ' + title + ' (' + list.length + ')\n\n' +
        list
          .map(
            (r) =>
              '- ' + (r.viewport ? '**[' + r.viewport + ']** ' : '') + r.detail +
              (r.where ? '  \n  `' + r.where + '`' : '') +
              '  \n  _seen in: ' + r.test + '_',
          )
          .join('\n') + '\n'
      : '\n## ' + title + '\n\nNone found.\n';

  const md =
    '# UI audit — consolidated findings\n\n' +
    'Generated ' + new Date().toISOString() + '. ' + rows.length + ' finding(s) across the run.\n' +
    block('Responsiveness', pick('responsive')) +
    block('Layout & overlap', pick('layout')) +
    block('Accessibility', pick('a11y')) +
    block('English', pick('english'));

  fs.mkdirSync(path.join(process.cwd(), 'audit'), { recursive: true });
  fs.writeFileSync(path.join(process.cwd(), 'audit', 'findings.md'), md);
  fs.rmSync(raw, { recursive: true, force: true });
  console.log('\nUI audit written to audit/findings.md (' + rows.length + ' findings)\n');
}
