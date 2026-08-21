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
  SubsystemRow,
} from '../types/api';

const API_BASE = import.meta.env.VITE_API_BASE_URL || '';

function toDateParam(d: Date): string {
  return d.toISOString().slice(0, 10);
}

function branchParam(branch: string | null): string {
  return branch ? `&branch=${encodeURIComponent(branch)}` : '';
}

export function useRegression() {
  const setConsolidated = useRegressionStore((s) => s.setConsolidated);
  const setScope = useRegressionStore((s) => s.setScope);
  const setSyncStatus = useRegressionStore((s) => s.setSyncStatus);
  const setConnection = useRegressionStore((s) => s.setConnection);
  const setSummary = useRegressionStore((s) => s.setSummary);
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
        const [consolidated, scope, sync, connection, summary] = await Promise.all([
          apiFetch<ConsolidatedImpact>(`/api/impact/consolidated${range}`),
          apiFetch<RegressionScope>(`/api/impact/scope${range}`),
          apiFetch<RegressionSyncStatus>('/api/impact/sync-status'),
          apiFetch<RegressionConnectionInfo>('/api/impact/connection').catch(() => null),
          apiFetch<ChurnSummary>(`/api/impact/summary${range}`).catch(() => null),
        ]);
        setConsolidated(consolidated);
        setScope(scope);
        setSyncStatus(sync);
        if (connection) setConnection(connection);
        setSummary(summary);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load regression data');
      } finally {
        setLoading(false);
      }
    },
    [setConsolidated, setScope, setSyncStatus, setConnection, setSummary, setLoading, setError],
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

  // Report is a file (text/csv or text/html) — bypass apiFetch (which JSON-parses / flags HTML) and stream a download.
  const downloadReport = useCallback(async (from: Date, to: Date, branch: string | null, format: 'csv' | 'html') => {
    const url = `${API_BASE}/api/impact/report?from=${toDateParam(from)}&to=${toDateParam(to)}${branchParam(branch)}&format=${format}`;
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

  const emailReport = useCallback(async (from: Date, to: Date, branch: string | null, recipients: string) => {
    await apiFetch<{ sent: boolean; recipients: string }>('/api/impact/email', {
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

  return { loadAll, fetchBranches, fetchComponents, fetchBuilds, fetchBuildImpact, downloadReport, emailReport, applySuiteEdit };
}
