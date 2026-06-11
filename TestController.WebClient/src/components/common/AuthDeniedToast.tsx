import { create } from 'zustand';

interface AuthDeniedToastState {
  visible: boolean;
  message: string;
  show: (msg: string) => void;
  hide: () => void;
}

let hideTimer: ReturnType<typeof setTimeout> | null = null;

export const useAuthDeniedToast = create<AuthDeniedToastState>((set) => ({
  visible: false,
  message: '',
  show: (msg) => {
    if (hideTimer) clearTimeout(hideTimer);
    set({ visible: true, message: msg });
    hideTimer = setTimeout(() => {
      set({ visible: false, message: '' });
      hideTimer = null;
    }, 5000);
  },
  hide: () => {
    if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
    set({ visible: false, message: '' });
  },
}));

/**
 * Toast notification for authorization denied (403) responses.
 * Amber border, shield icon, auto-dismiss after 5s.
 * Mount once at the app root (e.g., in AppShell).
 */
export default function AuthDeniedToast() {
  const { visible, message, hide } = useAuthDeniedToast();

  if (!visible) return null;

  return (
    <div className="fixed top-4 right-4 z-[9999] animate-in slide-in-from-top-2 fade-in duration-200">
      <div className="flex items-start gap-3 px-4 py-3 rounded-lg border border-amber-500/50 bg-bg-card shadow-lg max-w-sm">
        {/* Shield icon */}
        <span className="text-amber-400 text-lg flex-shrink-0">&#x1F6E1;</span>
        <div className="flex-1 min-w-0">
          <p className="text-sm font-medium text-text-primary">Permission Denied</p>
          <p className="text-xs text-text-secondary mt-0.5 break-words">{message}</p>
        </div>
        <button
          onClick={hide}
          className="text-text-secondary hover:text-text-primary text-sm flex-shrink-0"
        >
          ✕
        </button>
      </div>
    </div>
  );
}
