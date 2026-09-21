import { useCallback, useRef } from 'react';
import { PanelLeftClose, PanelLeft } from 'lucide-react';
import {
  usePanelLayout, usePanelLayoutStore, MIN_PANEL_WIDTH, MAX_PANEL_WIDTH,
} from '../../stores/panelLayoutStore';

interface SidebarProps {
  title: string;
  children: React.ReactNode;
}

export default function Sidebar({ title, children }: SidebarProps) {
  // Width and collapsed state live in a persisted store keyed by title: as local component state
  // they were lost on every unmount, so leaving and returning to a view reset the drag.
  const { width, collapsed } = usePanelLayout(title);
  const setWidth = usePanelLayoutStore(s => s.setWidth);
  const setCollapsed = usePanelLayoutStore(s => s.setCollapsed);
  const isResizing = useRef(false);

  const handleMouseDown = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    isResizing.current = true;
    const startX = e.clientX;
    const startWidth = width;

    const onMouseMove = (ev: MouseEvent) => {
      if (!isResizing.current) return;
      setWidth(title, startWidth + (ev.clientX - startX));
    };

    const onMouseUp = () => {
      isResizing.current = false;
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };

    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  }, [width, title, setWidth]);

  /** Double-click toggles between the widest and narrowest size, as an escape hatch from dragging. */
  const handleDoubleClick = useCallback(() => {
    setWidth(title, width >= MAX_PANEL_WIDTH ? MIN_PANEL_WIDTH : MAX_PANEL_WIDTH);
  }, [width, title, setWidth]);

  if (collapsed) {
    return (
      <div className="w-10 bg-bg-panel border-r border-bdr flex flex-col items-center pt-2 shrink-0">
        <button
          onClick={() => setCollapsed(title, false)}
          className="p-1.5 hover:bg-white/10 rounded text-text-muted hover:text-text-primary"
          aria-label={`Expand ${title} panel`}
        >
          <PanelLeft size={16} />
        </button>
      </div>
    );
  }

  return (
    <aside className="bg-bg-panel border-r border-bdr flex flex-col shrink-0 relative" style={{ width }}>
      <div className="flex items-center justify-between px-3 py-2 border-b border-bdr">
        <span className="text-xs font-semibold text-text-secondary uppercase tracking-wider">{title}</span>
        <button
          onClick={() => setCollapsed(title, true)}
          className="p-1 hover:bg-white/10 rounded text-text-muted hover:text-text-primary"
          aria-label={`Collapse ${title} panel`}
        >
          <PanelLeftClose size={14} />
        </button>
      </div>
      <div className="flex-1 overflow-auto">
        {children}
      </div>
      {/* Resize handle - also keyboard-operable so the panel is not mouse-only. */}
      <div
        role="separator"
        aria-orientation="vertical"
        aria-label={`Resize ${title} panel`}
        aria-valuenow={width}
        aria-valuemin={MIN_PANEL_WIDTH}
        aria-valuemax={MAX_PANEL_WIDTH}
        tabIndex={0}
        className="absolute top-0 right-0 w-1.5 h-full cursor-col-resize hover:bg-accent/30 active:bg-accent/50
          focus:bg-accent/50 focus:outline-none transition-colors"
        onMouseDown={handleMouseDown}
        onDoubleClick={handleDoubleClick}
        onKeyDown={(e) => {
          if (e.key === 'ArrowLeft') { e.preventDefault(); setWidth(title, width - 16); }
          if (e.key === 'ArrowRight') { e.preventDefault(); setWidth(title, width + 16); }
        }}
      />
    </aside>
  );
}
