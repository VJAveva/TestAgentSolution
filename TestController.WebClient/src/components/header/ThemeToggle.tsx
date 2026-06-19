import { Sun, Moon } from 'lucide-react';
import { useThemeStore } from '../../stores/themeStore';

/**
 * Header control to switch between light and dark themes. Flips whatever is
 * currently shown; the choice persists (localStorage) and overrides the OS
 * preference. Shows a Sun in dark mode (click → light) and a Moon in light
 * mode (click → dark).
 */
export default function ThemeToggle() {
  const resolved = useThemeStore((s) => s.resolved);
  const toggle = useThemeStore((s) => s.toggle);
  const next = resolved === 'dark' ? 'light' : 'dark';
  const label = `Switch to ${next} theme`;

  return (
    <button
      type="button"
      onClick={toggle}
      title={label}
      aria-label={label}
      className="flex items-center justify-center w-7 h-7 rounded border border-bdr text-text-secondary
                 hover:text-text-primary hover:bg-white/5 transition-colors"
    >
      {resolved === 'dark' ? <Sun size={15} /> : <Moon size={15} />}
    </button>
  );
}
