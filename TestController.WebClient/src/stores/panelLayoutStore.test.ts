import { describe, it, expect, beforeEach } from 'vitest';
import {
  usePanelLayoutStore, usePanelLayout,
  MIN_PANEL_WIDTH, MAX_PANEL_WIDTH, DEFAULT_PANEL_WIDTH,
} from './panelLayoutStore';

function reset() {
  usePanelLayoutStore.setState({ panels: {} });
}

describe('panelLayoutStore', () => {
  beforeEach(reset);

  it('Should_ReturnDefault_When_PanelHasNeverBeenResized', () => {
    const layout = usePanelLayoutStore.getState().panels['WatchList'];
    expect(layout).toBeUndefined();
    expect(usePanelLayout.length).toBe(1);
  });

  it('Should_RememberWidth_When_PanelIsResized', () => {
    usePanelLayoutStore.getState().setWidth('WatchList', 460);

    expect(usePanelLayoutStore.getState().panels['WatchList'].width).toBe(460);
  });

  // The panel unmounts whenever the user leaves the view; the store is what survives that.
  it('Should_KeepWidth_When_ReadBackAfterUnmount', () => {
    usePanelLayoutStore.getState().setWidth('WatchList', 512);

    const afterRemount = usePanelLayoutStore.getState().panels['WatchList'];

    expect(afterRemount.width).toBe(512);
    expect(afterRemount.collapsed).toBe(false);
  });

  it('Should_KeepPanelsIndependent_When_OneIsResized', () => {
    usePanelLayoutStore.getState().setWidth('WatchList', 500);
    usePanelLayoutStore.getState().setWidth('Sessions', 240);

    expect(usePanelLayoutStore.getState().panels['WatchList'].width).toBe(500);
    expect(usePanelLayoutStore.getState().panels['Sessions'].width).toBe(240);
  });

  // A stored 0 would render the panel invisible with no handle left to drag it back.
  it.each([
    [0, MIN_PANEL_WIDTH],
    [-50, MIN_PANEL_WIDTH],
    [10_000, MAX_PANEL_WIDTH],
    [Number.NaN, DEFAULT_PANEL_WIDTH],
  ])('Should_ClampToUsableRange_When_WidthIs_%s', (input, expected) => {
    usePanelLayoutStore.getState().setWidth('WatchList', input);

    expect(usePanelLayoutStore.getState().panels['WatchList'].width).toBe(expected);
  });

  it('Should_PreserveWidth_When_Collapsed', () => {
    usePanelLayoutStore.getState().setWidth('WatchList', 480);
    usePanelLayoutStore.getState().setCollapsed('WatchList', true);

    const layout = usePanelLayoutStore.getState().panels['WatchList'];
    expect(layout.collapsed).toBe(true);
    expect(layout.width).toBe(480);
  });

  it('Should_PreserveCollapsed_When_WidthChanges', () => {
    usePanelLayoutStore.getState().setCollapsed('WatchList', true);
    usePanelLayoutStore.getState().setWidth('WatchList', 400);

    expect(usePanelLayoutStore.getState().panels['WatchList'].collapsed).toBe(true);
  });
});
