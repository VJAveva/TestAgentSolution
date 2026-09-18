import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type ThemeChoice = 'light' | 'dark' | 'hc' | 'system';
export type ResolvedTheme = 'light' | 'dark' | 'hc';

const MEDIA_QUERY = '(prefers-color-scheme: dark)';
const CONTRAST_QUERY = '(prefers-contrast: more)';

/** jsdom and some embedded webviews have `window` but no `matchMedia`; a bare typeof check throws there. */
function media(query: string): MediaQueryList | null {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return null;
  return window.matchMedia(query);
}

function systemPrefersDark(): boolean {
  return media(MEDIA_QUERY)?.matches ?? false;
}

function systemPrefersContrast(): boolean {
  return media(CONTRAST_QUERY)?.matches ?? false;
}

/** Maps a user choice to a concrete theme. Contrast outranks colour scheme: it is an accessibility need. */
function resolve(choice: ThemeChoice): ResolvedTheme {
  if (choice !== 'system') return choice;
  if (systemPrefersContrast()) return 'hc';
  return systemPrefersDark() ? 'dark' : 'light';
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
  /** Step to the next theme in the light -> dark -> high-contrast cycle. */
  toggle: () => void;
}

const CYCLE: ResolvedTheme[] = ['light', 'dark', 'hc'];

/** The theme one step on from what is currently shown. Exported so the button can label itself. */
export function nextTheme(current: ResolvedTheme): ResolvedTheme {
  return CYCLE[(CYCLE.indexOf(current) + 1) % CYCLE.length];
}

export const useThemeStore = create<ThemeState>()(
  persist(
    (set, get) => ({
      theme: 'system',
      resolved: resolve('system'),
      setTheme: (theme) => set({ theme, resolved: apply(theme) }),
      toggle: () => {
        const next = nextTheme(get().resolved);
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

// Follow live OS changes, but only while the user is on 'system'.
const reapply = () => {
  if (useThemeStore.getState().theme === 'system') {
    useThemeStore.setState({ resolved: apply('system') });
  }
};
media(MEDIA_QUERY)?.addEventListener('change', reapply);
media(CONTRAST_QUERY)?.addEventListener('change', reapply);
