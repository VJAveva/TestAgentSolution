import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type ThemeChoice = 'light' | 'dark' | 'system';
export type ResolvedTheme = 'light' | 'dark';

const MEDIA_QUERY = '(prefers-color-scheme: dark)';

function systemPrefersDark(): boolean {
  return typeof window !== 'undefined' && window.matchMedia(MEDIA_QUERY).matches;
}

/** Maps a user choice to a concrete light/dark theme. */
function resolve(choice: ThemeChoice): ResolvedTheme {
  if (choice === 'system') return systemPrefersDark() ? 'dark' : 'light';
  return choice;
}

/** Writes the resolved theme to <html data-theme="..."> and returns it. */
function apply(choice: ThemeChoice): ResolvedTheme {
  const resolved = resolve(choice);
  if (typeof document !== 'undefined') {
    document.documentElement.dataset.theme = resolved;
  }
  return resolved;
}

interface ThemeState {
  /** The user's choice; 'system' follows the OS. */
  theme: ThemeChoice;
  /** The concrete theme currently applied. */
  resolved: ResolvedTheme;
  /** Set an explicit choice (overrides and persists). */
  setTheme: (theme: ThemeChoice) => void;
  /** Flip between light and dark based on what's currently shown. */
  toggle: () => void;
}

export const useThemeStore = create<ThemeState>()(
  persist(
    (set, get) => ({
      theme: 'system',
      resolved: resolve('system'),
      setTheme: (theme) => set({ theme, resolved: apply(theme) }),
      toggle: () => {
        const next: ResolvedTheme = get().resolved === 'dark' ? 'light' : 'dark';
        set({ theme: next, resolved: apply(next) });
      },
    }),
    {
      name: 'theme-storage',
      // Re-apply on rehydrate so the store and the <html> attribute agree
      // (the inline script in index.html already set it pre-paint).
      onRehydrateStorage: () => (state) => {
        if (state) state.resolved = apply(state.theme);
      },
    },
  ),
);

// Follow live OS theme changes, but only while the user is on 'system'.
if (typeof window !== 'undefined') {
  window.matchMedia(MEDIA_QUERY).addEventListener('change', () => {
    if (useThemeStore.getState().theme === 'system') {
      useThemeStore.setState({ resolved: apply('system') });
    }
  });
}
