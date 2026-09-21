import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export const MIN_PANEL_WIDTH = 200;
export const MAX_PANEL_WIDTH = 700;
export const DEFAULT_PANEL_WIDTH = 320;

interface PanelLayout {
  width: number;
  collapsed: boolean;
}

interface PanelLayoutState {
  /** Keyed by panel title, so resizing the WatchList does not move the Sessions or Builds panel. */
  panels: Record<string, PanelLayout>;
  setWidth: (panel: string, width: number) => void;
  setCollapsed: (panel: string, collapsed: boolean) => void;
}

/** Rejects a persisted width that would render the panel unusable with no handle to drag back. */
function sanitize(width: number): number {
  if (!Number.isFinite(width)) return DEFAULT_PANEL_WIDTH;
  return Math.min(Math.max(width, MIN_PANEL_WIDTH), MAX_PANEL_WIDTH);
}

export const usePanelLayoutStore = create<PanelLayoutState>()(
  persist(
    (set) => ({
      panels: {},
      setWidth: (panel, width) =>
        set((s) => ({
          panels: {
            ...s.panels,
            [panel]: { collapsed: s.panels[panel]?.collapsed ?? false, width: sanitize(width) },
          },
        })),
      setCollapsed: (panel, collapsed) =>
        set((s) => ({
          panels: {
            ...s.panels,
            [panel]: { width: s.panels[panel]?.width ?? DEFAULT_PANEL_WIDTH, collapsed },
          },
        })),
    }),
    {
      name: 'tc-panel-layout',
      // A corrupt or out-of-range stored value must never leave a pane invisible on next load.
      merge: (persisted, current) => {
        const stored = (persisted as PanelLayoutState | undefined)?.panels ?? {};
        const panels: Record<string, PanelLayout> = {};
        for (const [key, value] of Object.entries(stored)) {
          panels[key] = { width: sanitize(value?.width), collapsed: value?.collapsed === true };
        }
        return { ...current, panels };
      },
    },
  ),
);

/**
 * Selects each field separately and assembles the result in the caller.
 *
 * A selector returning `s.panels[panel] ?? { ... }` hands Zustand a NEW object on every render for
 * any panel with no stored entry; its Object.is check never matches, so the component re-renders
 * forever ("Maximum update depth exceeded"). Primitive selectors compare cleanly.
 */
export function usePanelLayout(panel: string): PanelLayout {
  const width = usePanelLayoutStore((s) => s.panels[panel]?.width ?? DEFAULT_PANEL_WIDTH);
  const collapsed = usePanelLayoutStore((s) => s.panels[panel]?.collapsed ?? false);
  return { width, collapsed };
}
