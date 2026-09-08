import { useRef } from 'react';
import { countRender, uiPerfEnabled } from '../lib/uiPerf';

/**
 * TEMPORARY — see docs/reliability/UIPerformance-CopilotPrompts.md (P03).
 * Counts renders of a component. No-op unless uiPerf is enabled.
 * Remove by deleting this file and its call sites.
 */
export function useRenderCount(component: string): number {
  const n = useRef(0);
  if (uiPerfEnabled) {
    n.current++;
    countRender(component);
  }
  return n.current;
}
