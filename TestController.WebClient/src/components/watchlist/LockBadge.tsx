import { useState, useEffect, useRef } from 'react';
import { Lock, Play } from 'lucide-react';
import type { PipelineLockDto } from '../../stores/lockStore';

interface LockBadgeProps {
  lock: PipelineLockDto | undefined;
  isOwn: boolean;
}

function formatElapsed(acquiredUtc: string): string {
  const elapsed = Math.max(0, Math.floor((Date.now() - new Date(acquiredUtc).getTime()) / 1000));
  const m = Math.floor(elapsed / 60);
  const s = elapsed % 60;
  return `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
}

export default function LockBadge({ lock, isOwn }: LockBadgeProps) {
  const [elapsed, setElapsed] = useState('00:00');
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => {
    if (lock && isOwn) {
      setElapsed(formatElapsed(lock.acquiredUtc));
      intervalRef.current = setInterval(() => {
        setElapsed(formatElapsed(lock.acquiredUtc));
      }, 1000);
    }
    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
      }
    };
  }, [lock, isOwn]);

  if (!lock) return null;

  if (isOwn) {
    return (
      <span className="inline-flex items-center gap-1 px-2 py-0.5 bg-blue-900/30 text-blue-400 text-[9px] font-bold rounded-full shrink-0">
        <Play size={9} />
        Your run · {elapsed}
      </span>
    );
  }

  return (
    <span className="inline-flex items-center gap-1 px-2 py-0.5 bg-amber-900/30 text-amber-400 text-[9px] font-bold rounded-full shrink-0">
      <Lock size={9} />
      Locked by {lock.ownerDisplayName} ({lock.ownerClientKind})
    </span>
  );
}
