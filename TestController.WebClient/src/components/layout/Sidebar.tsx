import { useState, useCallback, useRef } from 'react';
import { PanelLeftClose, PanelLeft } from 'lucide-react';

interface SidebarProps {
  title: string;
  children: React.ReactNode;
}

export default function Sidebar({ title, children }: SidebarProps) {
  const [collapsed, setCollapsed] = useState(false);
  const [width, setWidth] = useState(320);
  const isResizing = useRef(false);

  const handleMouseDown = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    isResizing.current = true;
    const startX = e.clientX;
    const startWidth = width;

    const onMouseMove = (ev: MouseEvent) => {
      if (!isResizing.current) return;
      const newWidth = Math.min(Math.max(startWidth + (ev.clientX - startX), 200), 700);
      setWidth(newWidth);
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
  }, [width]);

  if (collapsed) {
    return (
      <div className="w-10 bg-bg-panel border-r border-bdr flex flex-col items-center pt-2 shrink-0">
        <button onClick={() => setCollapsed(false)} className="p-1.5 hover:bg-white/10 rounded text-text-muted hover:text-text-primary">
          <PanelLeft size={16} />
        </button>
      </div>
    );
  }

  return (
    <aside className="bg-bg-panel border-r border-bdr flex flex-col shrink-0 relative" style={{ width }}>
      <div className="flex items-center justify-between px-3 py-2 border-b border-bdr">
        <span className="text-xs font-semibold text-text-secondary uppercase tracking-wider">{title}</span>
        <button onClick={() => setCollapsed(true)} className="p-1 hover:bg-white/10 rounded text-text-muted hover:text-text-primary">
          <PanelLeftClose size={14} />
        </button>
      </div>
      <div className="flex-1 overflow-auto">
        {children}
      </div>
      {/* Resize handle */}
      <div
        className="absolute top-0 right-0 w-1.5 h-full cursor-col-resize hover:bg-accent/30 active:bg-accent/50 transition-colors"
        onMouseDown={handleMouseDown}
      />
    </aside>
  );
}
