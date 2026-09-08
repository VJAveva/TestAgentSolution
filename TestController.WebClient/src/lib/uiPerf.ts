/**
 * TEMPORARY UI performance instrumentation — see docs/reliability/UIPerformance-CopilotPrompts.md (P03).
 *
 * Enable at runtime with NO rebuild:
 *     localStorage.setItem('uiPerf', '1'); location.reload();
 * Disable:
 *     localStorage.removeItem('uiPerf'); location.reload();
 * Or build-time: VITE_UI_PERF=1
 *
 * To remove entirely: delete this file plus the call sites in
 * lib/api.ts, hooks/useSignalR.ts, and the useRenderCount() lines in components.
 */

function readFlag(): boolean {
  try {
    if (localStorage.getItem('uiPerf') === '1') return true;
  } catch {
    /* private mode */
  }
  return import.meta.env.VITE_UI_PERF === '1';
}

export const uiPerfEnabled = readFlag();

const SIGNALR_REPORT_MS = 5_000;
const REST_REPORT_MS = 30_000;

interface Counter {
  count: number;
  bytes: number;
}

const signalrCounts = new Map<string, Counter>();
const renderCounts = new Map<string, number>();
const restCounts = new Map<string, Counter>();

/** Approximate wire size without paying for a full serialize on huge payloads. */
function approxBytes(payload: unknown): number {
  if (payload == null) return 0;
  try {
    if (Array.isArray(payload) && payload.length > 200) {
      // Sample the first element instead of stringifying 50k rows.
      return JSON.stringify(payload[0]).length * payload.length;
    }
    return JSON.stringify(payload).length;
  } catch {
    return 0;
  }
}

export function countSignalR(event: string, payload?: unknown): void {
  if (!uiPerfEnabled) return;
  const c = signalrCounts.get(event) ?? { count: 0, bytes: 0 };
  c.count++;
  c.bytes += approxBytes(payload);
  signalrCounts.set(event, c);
}

export function countRest(url: string, bytes: number): void {
  if (!uiPerfEnabled) return;
  // Collapse ids so /api/execution/<guid>/recent-logs aggregates into one row.
  const key = url
    .replace(/\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, '/{id}')
    .replace(/\/\d+/g, '/{n}')
    .split('?')[0];
  const c = restCounts.get(key) ?? { count: 0, bytes: 0 };
  c.count++;
  c.bytes += bytes;
  restCounts.set(key, c);
}

export function countRender(component: string): void {
  if (!uiPerfEnabled) return;
  renderCounts.set(component, (renderCounts.get(component) ?? 0) + 1);
}

export function mark(name: string): void {
  if (!uiPerfEnabled) return;
  try {
    performance.mark(name);
  } catch {
    /* ignore */
  }
}

export function measure(name: string, startMark: string): void {
  if (!uiPerfEnabled) return;
  try {
    performance.mark(`${startMark}:end`);
    performance.measure(name, startMark, `${startMark}:end`);
    const entries = performance.getEntriesByName(name);
    const last = entries[entries.length - 1];
    if (last) console.log(`[uiPerf] ${name} = ${last.duration.toFixed(1)}ms`);
  } catch {
    /* start mark missing */
  }
}

/**
 * Wraps HubConnection.on so every subscription in the app is counted without
 * touching individual handlers. Safe to call more than once.
 */
export function instrumentHubConnection(conn: {
  on: (event: string, handler: (...args: unknown[]) => void) => void;
  __uiPerfWrapped?: boolean;
}): void {
  if (!uiPerfEnabled || conn.__uiPerfWrapped) return;
  conn.__uiPerfWrapped = true;
  const original = conn.on.bind(conn);
  conn.on = (event: string, handler: (...args: unknown[]) => void) => {
    original(event, (...args: unknown[]) => {
      countSignalR(event, args[0]);
      handler(...args);
    });
  };
}

function reportSignalRAndRenders(): void {
  if (signalrCounts.size) {
    const rows = [...signalrCounts.entries()]
      .sort((a, b) => b[1].count - a[1].count)
      .map(([e, c]) => ({
        event: e,
        'msg/s': +(c.count / (SIGNALR_REPORT_MS / 1000)).toFixed(2),
        count: c.count,
        KB: +(c.bytes / 1024).toFixed(1),
      }));
    console.log(`[uiPerf] SignalR (last ${SIGNALR_REPORT_MS / 1000}s)`);
    console.table(rows);
    signalrCounts.clear();
  }

  if (renderCounts.size) {
    const rows = [...renderCounts.entries()]
      .sort((a, b) => b[1] - a[1])
      .slice(0, 10)
      .map(([component, renders]) => ({ component, renders }));
    console.log(`[uiPerf] Renders (last ${SIGNALR_REPORT_MS / 1000}s)`);
    console.table(rows);
    renderCounts.clear();
  }
}

function reportRest(): void {
  if (!restCounts.size) return;
  const rows = [...restCounts.entries()]
    .sort((a, b) => b[1].count - a[1].count)
    .map(([url, c]) => ({ url, count: c.count, KB: +(c.bytes / 1024).toFixed(1) }));
  console.log(`[uiPerf] REST (last ${REST_REPORT_MS / 1000}s)`);
  console.table(rows);
  restCounts.clear();
}

if (uiPerfEnabled) {
  console.log('[uiPerf] ENABLED. localStorage.removeItem("uiPerf") + reload to disable.');
  setInterval(reportSignalRAndRenders, SIGNALR_REPORT_MS);
  setInterval(reportRest, REST_REPORT_MS);
}
