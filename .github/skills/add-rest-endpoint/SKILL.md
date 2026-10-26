---
name: add-rest-endpoint
description: 'Add a REST endpoint to TestAgentSolution so it works in BOTH hosts (WPF-embedded API on :5200 and standalone WebApi/IIS on :81), enforces its RBAC permission server-side, and is covered by tests. Use for new API endpoint, new controller action, expose X over REST, WebClient needs an API.'
argument-hint: 'Route, verb, permission, and which host owns the data'
---

# Add a REST Endpoint

## The two hosts (get this right first)
- Shared controllers live in `TestController.Api/Controllers/`. Both hosts call `AddControllerApi()`: the WPF-embedded host via `TestControllerGrpc/Services/ControllerWebApiHost.cs`, the standalone host via `TestController.WebApi/Program.cs`. So a new controller in TestController.Api is served by **both**.
- **Two DI containers.** Each host has its own singletons. A service the action needs must be registered in **both** containers.
  - Put the registration in an `Add*Feature()` extension called from `AddControllerApi()`. That way the two containers cannot drift apart (pattern: `AddFirFeature()`).
  - If the service needs WPF-host singletons, `ControllerWebApiHost` must **bridge** them from `App.Services` before `AddControllerApi()`. Don't register them twice.
- **Primary-host-only data** (session DB, lock registry, mutes, pre-flight): the standalone WebApi has no database. Add the controller to `TestController.WebApi/Services/ProxiedControllerExclusionProvider.cs` **and** add a minimal-API proxy endpoint in `TestController.WebApi/Endpoints/` that forwards via `ControllerProxyService`. Without the exclusion you get an `AmbiguousMatchException`, or a 500 from the Null* stubs.
- **Optional service**: resolve it per request with `HttpContext.RequestServices.GetService<T>()` and return 404 when it's null (precedent: `ImpactController`, `ResultsController` FIR). A required ctor dependency that one host lacks breaks controller activation; the WPF host uses `ValidateOnBuild=true`.

## Route rules
- **No `/`, `+`, `%2B` or free text in path segments.** IIS rejects encoded separators and `+`, and the 404 comes back as `text/html`. Put tags, node paths and names in the **body** or the query string.
- REST semantics: GET is read-only, POST creates or acts, 404 for missing, 409 for lock conflicts, 403 for denied. Return `ProblemDetails` on errors.
- Validate input at the boundary. Anything that can reach a shell goes through `TriggerParameterValidator`-style checks (keys `^[A-Za-z0-9_]{1,64}$`, block `& | ; < > ^ \` "`, control chars, `$(`, `@(`).

## Permission (server-side, always)
- Add `[RequirePermission(Permission.X)]` (`TestController.Api/Security/RequirePermissionAttribute.cs` → `RbacGate`). The UI hiding a button is UX only.
- For pipeline-scoped permissions, call the scoped check with the **WatchItem Tag** as resourceId. An unscoped check denies Engineers in `AuthorizationService.CanAsync` while `CapabilityChecker.Can` allows them, which gives an enabled button that fails on click. Check **both layers** for the exact (role, permission, resourceId) triple.
- Both gates **fail open in Default mode** (`RBAC:Enabled=false`) by design.
- If the permission is new, use skill `add-rbac-permission` first.

## Tests (TestController.WebApi.Tests)
- Use the existing `TestWebAppFactory`. Override settings with `builder.UseSetting(...)`, **never** `ConfigureAppConfiguration` (minimal hosting gives ObjectDisposedException).
- RBAC deterministic on the standalone host: `WithWebHostBuilder` + `ConfigureTestServices`, replace `IRemoteCapabilityResolver`, and set `RBAC:Enabled=true`.
- Required cases, named `Method_Should_Expected_When_State`:
  1. allowed caller gets 2xx and the right body
  2. caller without the permission gets **403** in Secured mode
  3. Default mode behaves as intended (read allowed, mutation per policy)
  4. bad input gets 400, and missing resource gets 404
  5. for proxied endpoints, the proxy forwards (no AmbiguousMatch)
- Red-proof: remove the `[RequirePermission]` and confirm the 403 test **fails**, then restore it. After restoring, touch the file's LastWriteTime so the next build isn't stale.

## Client side (if asked)
- WebClient calls go through `apiFetch<T>()` in `src/lib/api.ts`, with state in a Zustand `useXxxStore`.
- WPF calls go straight to the service, not over HTTP.

## Done when
- `dotnet build` shows 0 errors.
- The new tests pass, and the WebApi suite passes (about 2 min).
- Report the route, the permission, which host serves it (local/proxied), and the files touched.
