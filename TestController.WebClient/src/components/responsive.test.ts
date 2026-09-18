import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * G-8 fluidity. A fixed pixel width on an overlay makes the dialog wider than a small viewport, and an
 * unprefixed multi-column grid crushes its cells rather than reflowing. Neither shows up in a unit test
 * of behaviour, so this walks the source instead.
 */
const SRC = resolve(__dirname, '..');

function tsxFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) return tsxFiles(full);
    return full.endsWith('.tsx') ? [full] : [];
  });
}

const sources = tsxFiles(SRC).map((path) => ({ path, text: readFileSync(path, 'utf8') }));

describe('responsive layout', () => {
  it('finds components to scan', () => {
    expect(sources.length).toBeGreaterThan(50);
  });

  it('never pins an overlay to a width a small viewport cannot show', () => {
    const offenders = sources
      .filter(({ text }) => /className="[^"]*\bfixed inset-0[^"]*"/.test(text))
      .filter(({ text }) =>
        // A bare w-[...px] is only safe when a max-w- cap lets it shrink.
        /\bw-\[\d+px\]/.test(text) && !/\bmax-w-\[/.test(text))
      .map(({ path }) => path.replace(SRC, ''));

    expect(offenders).toEqual([]);
  });

  it('reflows multi-column grids instead of crushing them', () => {
    const offenders = sources
      .flatMap(({ path, text }) =>
        [...text.matchAll(/(?<![a-z:-])grid-cols-(\d+)/g)]
          .filter((m) => Number(m[1]) >= 4)
          .map(() => path.replace(SRC, '')))
      .filter((v, i, a) => a.indexOf(v) === i);

    expect(offenders).toEqual([]);
  });

  // The nav collapses to icons below lg; without aria-label those buttons lose their only name.
  it('keeps nav buttons named when their labels are hidden', () => {
    const shell = sources.find(({ path }) => path.endsWith('AppShell.tsx'));
    expect(shell).toBeDefined();

    const hidesLabels = /hidden lg:inline/.test(shell!.text);
    expect(hidesLabels).toBe(true);
    expect(/aria-label=\{t\.label\}/.test(shell!.text)).toBe(true);
  });
});
