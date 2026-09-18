import { Sun, Moon, Contrast } from 'lucide-react';
import { useThemeStore, nextTheme } from '../../stores/themeStore';

const ICONS = { light: Moon, dark: Sun, hc: Contrast } as const;
const LABELS = { light: 'light', dark: 'dark', hc: 'high contrast' } as const;

/**
 * Header control that steps through light -> dark -> high contrast. The choice persists
 * (localStorage) and overrides the OS preference. The icon shows what a click will switch TO.
 */
export default function ThemeToggle() {
  const resolved = useThemeStore((s) => s.resolved);
  const toggle = useThemeStore((s) => s.toggle);
  const next = nextTheme(resolved);
  const label = `Switch to ${LABELS[next]} theme`;
  const Icon = ICONS[resolved];

  return (
    <button
      type="button"
      onClick={toggle}
      title={label}
      aria-label={label}
      className="flex items-center justify-center w-7 h-7 rounded border border-bdr text-text-secondary
                 hover:text-text-primary hover:bg-white/5 transition-colors"
    >
      <Icon size={15} />
    </button>
  );
}
