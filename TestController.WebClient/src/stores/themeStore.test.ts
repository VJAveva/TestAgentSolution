import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, it, expect } from 'vitest';
import { nextTheme } from './themeStore';

/**
 * A CSS custom property that a theme forgets to define does not error - it silently inherits the
 * previous theme's value, so a missing token in the high-contrast set renders as a light-theme
 * colour on a black surface. The WPF side has the same guard (ThemeResourceParityTests) and it
 * caught three live rendering bugs, so the web client gets one too.
 */
const css = stripComments(readFileSync(resolve(__dirname, '../index.css'), 'utf8'));

function stripComments(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, '');
}

/** Every `--token` declared under the given selector, across all blocks that list it. */
function tokensFor(selector: string): Set<string> {
  const found = new Set<string>();

  for (const block of css.matchAll(/([^{}]*)\{([^}]*)\}/g)) {
    const selectors = block[1].replace(/\s+/g, ' ').split(',').map((s) => s.trim());
    if (!selectors.includes(selector)) continue;

    for (const decl of block[2].matchAll(/--([a-z0-9-]+)\s*:/g)) found.add(decl[1]);
  }

  return found;
}

describe('theme token parity', () => {
  // Light carries the complete set; :root holds the report-card defaults that dark inherits.
  const baseline = new Set([...tokensFor(':root'), ...tokensFor("[data-theme='light']")]);

  it('has a non-trivial baseline', () => {
    expect(baseline.size).toBeGreaterThan(40);
  });

  it('defines every baseline token in the high-contrast theme', () => {
    const hc = tokensFor("[data-theme='hc']");
    const missing = [...baseline].filter((t) => !hc.has(t));

    expect(missing, `high-contrast is missing: ${missing.join(', ')}`).toEqual([]);
  });

  it('defines every baseline token in the dark theme, directly or via :root', () => {
    const dark = new Set([...tokensFor(':root'), ...tokensFor("[data-theme='dark']")]);
    const missing = [...baseline].filter((t) => !dark.has(t));

    expect(missing, `dark is missing: ${missing.join(', ')}`).toEqual([]);
  });
});

describe('nextTheme', () => {
  it('cycles light -> dark -> hc -> light so every theme is reachable from the toggle', () => {
    expect(nextTheme('light')).toBe('dark');
    expect(nextTheme('dark')).toBe('hc');
    expect(nextTheme('hc')).toBe('light');
  });
});
