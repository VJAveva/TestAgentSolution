import { describe, expect, it } from 'vitest';
import { clampToViewport } from './menuPosition';

describe('clampToViewport', () => {
  it('keeps the click point when the menu fits', () => {
    expect(clampToViewport(100, 100, 200, 150, 1280, 800)).toEqual({ left: 100, top: 100 });
  });

  it('pulls the menu back inside near the bottom-right edge', () => {
    expect(clampToViewport(1250, 780, 200, 150, 1280, 800)).toEqual({ left: 1072, top: 642 });
  });

  it('never goes past the top-left margin when the menu is taller than the viewport', () => {
    expect(clampToViewport(10, 10, 200, 900, 1280, 800)).toEqual({ left: 10, top: 8 });
  });
});
