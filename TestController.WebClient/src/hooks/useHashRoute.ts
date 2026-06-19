import { useSyncExternalStore, useCallback } from 'react';

const subscribe = (cb: () => void) => {
  window.addEventListener('hashchange', cb);
  return () => window.removeEventListener('hashchange', cb);
};

// "#/monitor/timeline" -> "monitor/timeline"
const getSnapshot = () => window.location.hash.replace(/^#\/?/, '');

/**
 * Zero-dependency hash route backed by the URL fragment.
 *
 * Survives page refresh, gives browser back/forward for free, and makes
 * every tab deep-linkable without a router dependency. Returns the current
 * route (without the leading "#/") and a navigate function.
 */
export function useHashRoute(): [string, (route: string) => void] {
  const route = useSyncExternalStore(subscribe, getSnapshot);
  const navigate = useCallback((r: string) => { window.location.hash = `/${r}`; }, []);
  return [route, navigate];
}
