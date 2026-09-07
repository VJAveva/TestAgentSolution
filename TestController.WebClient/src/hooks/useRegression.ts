import { useCallback } from 'react';
import { apiFetch } from '../lib/api';
import { getUserId } from '../lib/userIdentity';
import { useRegressionStore } from '../stores/regressionStore';
import type {
  ConsolidatedImpact,
  RegressionScope,
  RegressionSyncStatus,
  RegressionConnectionInfo,
  RegressionComponentRef,
  RegressionBuildRef,
  ChurnSummary,
  ImpactedComponentAnalysis,
  SubsystemRow,
  ImpactIndexHealth,
} from '../types/api';
import type { FilterState } from '../components/regression/helpers';

const API_BASE = import.meta.env.VITE_API_BASE_URL || '';

function toDateParam(d: Date): string {
  return d.toISOString().slice(0, 10);
}

function branchParam(branch: string | null): string {
  return branch ? `&branch=${encodeURIComponent(branch)}` : '';
}

function filterQuery(f: FilterState): string {
  const p = new URLSearchParams({
    showRuntime: String(f.showRuntime),
    showConfig: String(f.showConfig),
    hideAutomated: String(f.hideAutomated),
    showAllChanges: String(f.showAllChanges),
    filterBug: String(f.filterBug),
    filterStory: String(f.filterStory),
    filterIms: String(f.filterIms),
  });
  if (f.component) p.set('component', f.component);
  return p.toString();
}

export function useRegression() {
  const setConsolidated = useRegressionStore((s) => s.setConsolidated);
  const setScope = useRegressionStore((s) => s.setScope);
  const setSyncStatus = useRegressionStore((s) => s.setSyncStatus);
  const setConnection = useRegressionStore((s) => s.setConnection);
  const setSummary = useRegressionStore((s) => s.setSummary);
  const setIndexHealth = useRegressionStore((s) => s.setIndexHealth);
  const setAiLoading = useRegressionStore((s) => s.setAiLoading);
  const setBranches = useRegressionStore((s) => s.setBranches);
  const setComponents = useRegressionStore((s) => s.setComponents);
  const setLoading = useRegressionStore((s) => s.setLoading);
  const setError = useRegressionStore((s) => s.setError);

  const loadAll = useCallback(
    async (from: Date, to: Date, branch: string | null) => {
      setLoading(true);
      setError(null);
      const range = `?from=${toDateParam(from)}&to=${toDateParam(to)}${branchParam(branch)}`;
      try {
        const [consolidated, scope, sync, connection, summary, health] = await Promise.all([
          apiFetch<ConsolidatedImpact>(`/api/impact/consolidated${range}`),
          apiFetch<RegressionScope>(`/api/impact/scope${range}`),
          apiFetch<RegressionSyncStatus>('/api/impact/sync-status'),
          apiFetch<RegressionConnectionInfo>('/api/impact/connection').catch(() => null),
          apiFetch<ChurnSummary>(`/api/impact/summary${range}`).catch(() => null),
          apiFetch<ImpactIndexHealth>('/health/impact-index').catch(() => null),
        ]);
        setConsolidated(consolidated);
        setScope(scope);
        setSyncStatus(sync);
        if (connection) setConnection(connection);
        setSummary(summary);
        setIndexHealth(health);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load regression data');
      } finally {
        setLoading(false);
      }
    },
    [setConsolidated, setScope, setSyncStatus, setConnection, setSummary, setIndexHealth, setLoading, setError],
  );

  const fetchBranches = useCallback(async () => {
    try {
      setBranches(await apiFetch<string[]>('/api/impact/branches'));
    } catch {
      /* best-effort */
    }
  }, [setBranches]);

  const fetchComponents = useCallback(async () => {
    try {
      setComponents(await apiFetch<RegressionComponentRef[]>('/api/impact/components'));
    } catch {
      /* best-effort */
    }
  }, [setComponents]);

  // Build inspector: list a component's recent builds, then fetch one build's impact row.
  const fetchBuilds = useCallback(
    (definitionId: number) => apiFetch<RegressionBuildRef[]>(`/api/impact/components/${definitionId}/builds`),
    [],
  );

  const fetchBuildImpact = useCallback(
    (definitionId: number, buildId: number) => apiFetch<SubsystemRow>(`/api/impact/components/${definitionId}/builds/${buildId}`),
    [],
  );

  // Impact-mapped test cases + change-analysis fallback for one component (grid row-expand).
  const fetchTestMatches = useCallback(
    (row: SubsystemRow) =>
      apiFetch<ImpactedComponentAnalysis>('/api/impact/test-matches', {
        method: 'POST',
        body: JSON.stringify(row),
      }),
    [],
  );

  // Report is a file (text/csv or text/html) — bypass apiFetch (which JSON-parses / flags HTML) and stream a download.
  const downloadReport = useCallback(async (from: Date, to: Date, branch: string | null, format: 'csv' | 'html' | 'xlsx', filter: FilterState) => {
    const url = `${API_BASE}/api/impact/report?from=${toDateParam(from)}&to=${toDateParam(to)}${branchParam(branch)}&format=${format}&${filterQuery(filter)}`;
    const headers: Record<string, string> = { 'X-User-Id': getUserId(), 'X-Source': 'WebClient' };
    const token = sessionStorage.getItem('auth_token');
    if (token) headers['Authorization'] = `Bearer ${token}`;
    const res = await fetch(url, { headers });
    if (!res.ok) throw new Error(`Report failed (${res.status})`);
    const blob = await res.blob();
    const objUrl = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = objUrl;
    a.download = `churn-report-${toDateParam(from)}-${toDateParam(to)}.${format}`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(objUrl);
  }, []);

  const emailReport = useCallback(async (from: Date, to: Date, branch: string | null, recipients: string, filter: FilterState) => {
    await apiFetch<{ sent: boolean; recipients: string }>(`/api/impact/email?${filterQuery(filter)}`, {
      method: 'POST',
      body: JSON.stringify({ from: toDateParam(from), to: toDateParam(to), branch, recipients }),
    });
  }, []);

  const applySuiteEdit = useCallback(
    async (subsystem: string, isManual: boolean, suiteId: string, remove: boolean) => {
      await apiFetch<void>('/api/impact/suites', {
        method: 'POST',
        body: JSON.stringify({ subsystem, isManual, suiteId, remove }),
      });
    },
    [],
  );

  // On-demand diff-grounded LLM summary; replaces the banner's summary with the AI version.
  const summarizeWithAi = useCallback(
    async (from: Date, to: Date, branch: string | null, filter: FilterState) => {
      setAiLoading(true);
      try {
        const range = `?from=${toDateParam(from)}&to=${toDateParam(to)}${branchParam(branch)}&${filterQuery(filter)}`;
        setSummary(await apiFetch<ChurnSummary>(`/api/impact/ai-summary${range}`));
      } catch (err) {
        setError(err instanceof Error ? err.message : 'AI summary failed');
      } finally {
        setAiLoading(false);
      }
    },
    [setSummary, setAiLoading, setError],
  );

  return { loadAll, fetchBranches, fetchComponents, fetchBuilds, fetchBuildImpact, fetchTestMatches, downloadReport, emailReport, applySuiteEdit, summarizeWithAi };
}
