import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import Sidebar from './Sidebar';
import { usePanelLayoutStore, MAX_PANEL_WIDTH } from '../../stores/panelLayoutStore';

/**
 * Rendering is the assertion here.
 *
 * A Zustand selector that builds a new object (`s.panels[x] ?? { ... }`) fails Object.is on every
 * render and React aborts with "Maximum update depth exceeded" — which took down the whole WatchList
 * page. That blows up during render, so simply mounting the component catches it.
 */
describe('Sidebar', () => {
  beforeEach(() => usePanelLayoutStore.setState({ panels: {} }));

  it('Should_RenderWithoutLooping_When_PanelHasNoStoredWidth', () => {
    render(<Sidebar title="WatchList"><div>tree</div></Sidebar>);

    expect(screen.getByText('tree')).toBeTruthy();
    expect(screen.getByText('WatchList')).toBeTruthy();
  });

  it('Should_RenderWithoutLooping_When_PanelHasAStoredWidth', () => {
    usePanelLayoutStore.getState().setWidth('WatchList', 512);

    render(<Sidebar title="WatchList"><div>tree</div></Sidebar>);

    expect(screen.getByRole('separator', { name: /Resize WatchList panel/i })
      .getAttribute('aria-valuenow')).toBe('512');
  });

  it('Should_PersistCollapse_When_Toggled', () => {
    render(<Sidebar title="WatchList"><div>tree</div></Sidebar>);

    fireEvent.click(screen.getByRole('button', { name: /Collapse WatchList panel/i }));

    expect(usePanelLayoutStore.getState().panels['WatchList'].collapsed).toBe(true);
    expect(screen.getByRole('button', { name: /Expand WatchList panel/i })).toBeTruthy();
  });

  it('Should_ResizeByKeyboard_When_ArrowPressedOnTheHandle', () => {
    usePanelLayoutStore.getState().setWidth('WatchList', 400);
    render(<Sidebar title="WatchList"><div>tree</div></Sidebar>);
    const handle = screen.getByRole('separator', { name: /Resize WatchList panel/i });

    fireEvent.keyDown(handle, { key: 'ArrowRight' });
    expect(usePanelLayoutStore.getState().panels['WatchList'].width).toBe(416);

    fireEvent.keyDown(handle, { key: 'ArrowLeft' });
    expect(usePanelLayoutStore.getState().panels['WatchList'].width).toBe(400);
  });

  it('Should_SnapToMaxWidth_When_HandleIsDoubleClicked', () => {
    usePanelLayoutStore.getState().setWidth('WatchList', 320);
    render(<Sidebar title="WatchList"><div>tree</div></Sidebar>);

    fireEvent.doubleClick(screen.getByRole('separator', { name: /Resize WatchList panel/i }));

    expect(usePanelLayoutStore.getState().panels['WatchList'].width).toBe(MAX_PANEL_WIDTH);
  });

  it('Should_KeepPanelsIndependent_When_TwoAreRendered', () => {
    usePanelLayoutStore.getState().setWidth('WatchList', 500);

    render(
      <>
        <Sidebar title="WatchList"><div>tree</div></Sidebar>
        <Sidebar title="Sessions"><div>sessions</div></Sidebar>
      </>,
    );

    expect(screen.getByRole('separator', { name: /Resize WatchList panel/i })
      .getAttribute('aria-valuenow')).toBe('500');
    expect(screen.getByRole('separator', { name: /Resize Sessions panel/i })
      .getAttribute('aria-valuenow')).toBe('320');
  });
});
