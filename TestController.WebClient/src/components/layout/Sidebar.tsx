import { useState } from 'react';
import { PanelLeftClose, PanelLeft } from 'lucide-react';

interface SidebarProps {
  title: string;
  children: React.ReactNode;
}

export default function Sidebar({ title, children }: SidebarProps) {
  const [collapsed, setCollapsed] = useState(false);

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
    <aside className="w-72 bg-bg-panel border-r border-bdr flex flex-col shrink-0">
      <div className="flex items-center justify-between px-3 py-2 border-b border-bdr">
        <span className="text-xs font-semibold text-text-secondary uppercase tracking-wider">{title}</span>
        <button onClick={() => setCollapsed(true)} className="p-1 hover:bg-white/10 rounded text-text-muted hover:text-text-primary">
          <PanelLeftClose size={14} />
        </button>
      </div>
      <div className="flex-1 overflow-auto">
        {children}
      </div>
    </aside>
  );
}
