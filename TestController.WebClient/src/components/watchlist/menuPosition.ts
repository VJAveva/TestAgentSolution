const MARGIN = 8;

/** Keeps a menu of the given size inside the viewport, preferring the click point. */
export function clampToViewport(x: number, y: number, width: number, height: number, viewW: number, viewH: number) {
  const left = Math.max(MARGIN, Math.min(x, viewW - width - MARGIN));
  const top = Math.max(MARGIN, Math.min(y, viewH - height - MARGIN));
  return { left, top };
}
