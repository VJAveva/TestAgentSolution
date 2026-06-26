/** @type {import('tailwindcss').Config} */

// Every token resolves to a CSS variable holding space-separated RGB channels,
// wrapped so Tailwind's `<alpha-value>` opacity modifiers (e.g. bg-accent/20)
// keep working. The two value-sets live in src/index.css keyed off [data-theme].
const v = (name) => `rgb(var(${name}) / <alpha-value>)`;

export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        bg: {
          DEFAULT: v('--bg-app'),
          card: v('--bg-card'),
          panel: v('--bg-panel'),
          surface: v('--bg-surface'),
          ribbon: v('--bg-ribbon'),
        },
        accent: v('--accent'),
        'acc-green': v('--acc-green'),
        'acc-red': v('--acc-red'),
        'acc-yellow': v('--acc-yellow'),
        'acc-amber': v('--acc-amber'),
        'acc-teal': v('--acc-teal'),
        'acc-peach': v('--acc-peach'),
        'acc-mauve': v('--acc-mauve'),
        'acc-blue': v('--acc-blue'),
        text: {
          primary: v('--text-primary'),
          secondary: v('--text-secondary'),
          muted: v('--text-muted'),
        },
        bdr: v('--border-default'),
        // Tree-state semantic tokens
        'state-triggerable': v('--state-triggerable'),
        'state-viewonly': v('--state-viewonly'),
        'state-disabled': v('--state-disabled'),
        'state-locked': v('--state-locked'),
        // Node-type badge tokens (WatchList tree)
        'badge-evt': v('--badge-evt'),
        'badge-seq': v('--badge-seq'),
        'badge-init': v('--badge-init'),
        'badge-ref': v('--badge-ref'),
        'badge-rmt': v('--badge-rmt'),
        'badge-cmd': v('--badge-cmd'),
        'badge-par': v('--badge-par'),
        // Build Report Card — fixed dark palette (matches the HTML mockup)
        rc: {
          'bg-primary': v('--rc-bg-primary'),
          'bg-secondary': v('--rc-bg-secondary'),
          'bg-tertiary': v('--rc-bg-tertiary'),
          border: v('--rc-border'),
          'text-primary': v('--rc-text-primary'),
          'text-secondary': v('--rc-text-secondary'),
          'text-tertiary': v('--rc-text-tertiary'),
          success: v('--rc-success'),
          'bg-success': v('--rc-bg-success'),
          info: v('--rc-info'),
          'bg-info': v('--rc-bg-info'),
          warning: v('--rc-warning'),
          'bg-warning': v('--rc-bg-warning'),
          danger: v('--rc-danger'),
          'bg-danger': v('--rc-bg-danger'),
        },
      },
      fontFamily: {
        sans: ['Segoe UI', 'Arial', 'sans-serif'],
        mono: ['JetBrains Mono', 'Consolas', 'Cascadia Code', 'monospace'],
      },
    },
  },
  plugins: [],
};
