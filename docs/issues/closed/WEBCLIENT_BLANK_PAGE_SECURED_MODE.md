# WebClient Blank Page in RBAC Secured Mode

**Date**: 2026-06-13  
**Status**: Diagnosed — awaiting fix  
**Severity**: Critical (blocks web testing in Secured mode)

---

## Summary

After successful RBAC login (login 200, /api/auth/me 200, /api/system/mode 200), the WebClient renders a blank/empty dashboard. The browser console shows a repeating 404 on `/api/execution/proxy/dashboard-sessions`.

---

## Root Cause

**Endpoint mismatch between React client and WPF Controller host.**

| Layer | Path Used | Exists? |
|-------|-----------|---------|
| React `useExecutionDashboard` hook | `/api/execution/proxy/dashboard-sessions` | Only in standalone IIS WebApi (`ExecutionEndpoints.cs`) |
| WPF Controller embedded API (port 5200) | `/api/execution/dashboard-sessions` | ✅ Yes (shared `ExecutionController.cs`) |

The `/proxy/` prefix routes are standalone-WebApi-only features (they merge local sessions with proxied WPF data). The WPF Controller's `ControllerWebApiHost` only calls `UseControllerApi()` which maps shared MVC controllers — it does NOT register the minimal API proxy endpoints.

When the React app calls the `proxy/` path against the WPF host, no route matches → SPA fallback returns `index.html` → `apiFetch` detects HTML on an `/api` path → synthesizes a 404 error.

---

## Findings Table

| # | Symptom | File | Root Cause | Severity | Fix |
|---|---------|------|------------|----------|-----|
| 1 | `GET /api/execution/proxy/dashboard-sessions → 404` | `useExecutionDashboard.tsx:335` | Endpoint only exists in standalone WebApi, not WPF host | **Critical** | Change path to `/api/execution/dashboard-sessions` |
| 2 | Blank page (no error boundary triggered) | `App.tsx` | Error is caught and swallowed — dashboard loads with empty state; combined with `!user` gate blocking render | **Medium** | Previous App.tsx fix (loading states) should resolve; verify `fetchMe` works against WPF host |
| 3 | 30s retry loop (noisy console) | `useExecutionDashboard.tsx:340-345` | Polling interval retries the broken endpoint every 3-8s | **Low** | Fix #1 eliminates this |
| 4 | SignalR "failed during negotiation" then self-recovers | `useSignalR.ts:55-80` | Hook called at top of `App.tsx` before auth completes; first negotiation has no token | **Low** | Move `useSignalR()` below auth gate, or add `accessTokenFactory` |
| 5 | 20-30s call durations | Agent connectivity | `AgentGrpcDispatcher` timeout reaching offline agents (jvkbak, VinodJHist) — unrelated | **Out of scope** | — |
| 6 | IIS WebApi (port 81) returns 500.30 | `Program.cs` startup | App process crashes during startup (likely `HostedService.StartAsync` throwing from agent timeouts or DI failure) | **High** | Separate investigation needed |

---

## Highest-Priority Fix (Unblocks Web Testing)

**File**: `TestController.WebClient/src/hooks/useExecutionDashboard.tsx`  
**Line**: 335

```diff
- return apiFetch<{ active: SessionSummary[]; history: SessionSummary[] }>('/api/execution/proxy/dashboard-sessions')
+ return apiFetch<{ active: SessionSummary[]; history: SessionSummary[] }>('/api/execution/dashboard-sessions')
```

This single change makes the React dashboard call the endpoint that actually exists on the WPF Controller's embedded API.

---

## Secondary Fixes

### 2. SignalR auth timing (`useSignalR.ts`)

Move `useSignalR()` inside the authenticated shell (below the `!isAuthenticated` guard in App.tsx), or pass the session token via `accessTokenFactory`:

```typescript
.withUrl(hubUrl, {
  withCredentials: true,
  accessTokenFactory: () => useAuthStore.getState().token ?? '',
})
```

### 3. IIS WebApi startup crash (port 81)

Requires separate investigation:
- Run with `ASPNETCORE_ENVIRONMENT=Development` to get developer exception page
- Likely cause: a `HostedService` (`AgentEventRelayService` or `DatabaseInitializerService`) throws during `StartAsync` when agents are unreachable
- Fix: wrap those hosted services with try/catch in `StartAsync` so agent connectivity failures don't kill the process

---

## How to Verify

1. Apply fix #1 (one-line path change)
2. Rebuild React: `cd TestController.WebClient && npm run build`
3. Copy dist to WPF host's wwwroot (or run `npm run dev` with Vite proxy to port 5200)
4. Login via Secured mode → dashboard should show sessions (or empty list, not blank page)
5. Console should no longer show repeating 404 errors

---

## Related Files

- `TestController.WebClient/src/hooks/useExecutionDashboard.tsx` — the broken call
- `TestController.WebClient/src/App.tsx` — bootstrap/auth flow
- `TestController.WebClient/src/hooks/useSignalR.ts` — premature connection
- `TestController.WebClient/src/lib/api.ts` — `apiFetch` HTML detection logic
- `TestController.Api/Controllers/ExecutionController.cs` — real endpoint (shared)
- `TestController.WebApi/Endpoints/ExecutionEndpoints.cs` — proxy endpoint (standalone only)
- `TestControllerGrpc/Services/ControllerWebApiHost.cs` — WPF embedded API (no proxy routes)
