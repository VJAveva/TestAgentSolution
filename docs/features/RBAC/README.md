---
feature: RBAC
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/Authorization/Permission.cs, TestController.Persistence/Authorization/AuthorizationService.cs, TestController.Api/Interceptors/SessionAuthInterceptor.cs, TestController.Api/Security/PermissionGuard.cs, TestControllerGrpc.Core/Locking/LockRegistry.cs, TestController.WebClient/src/lib/capabilities.ts]
tests: [TestControllerGrpc.Tests/Rbac, TestController.WebApi.Tests/Rbac, TestController.WebApi.Tests/WatchListEndpointsTests.cs]
last-verified: 2026-11-09
---

# RBAC: users, roles, permissions, pipeline locks

## What it does
- Session-based role-based access control (Administrator, SeniorManager, Engineer, Guest) on top of the existing REST security layer ([SecurityFramework](../SecurityFramework/README.md)); the two coexist.
- Two modes: **Default** (no login, everyone may run pipelines) and **Secured** (login or guest; every action checked against a `Permission`).
- Users, sessions, pipeline assignments and the append-only audit log live in SQLite (`TestController.Persistence`).
- Pipeline locks stop two people running the same WatchItem at once; this is separate from `AgentLockManager`, which locks agent machines.

## How to use
- Switch mode with `RBAC:Enabled` (or the WPF security mode screen in the ADMIN tab). Secured mode shows a login screen in WPF and the web client.
- Administrators manage users, pipeline assignments and the audit viewer from the WPF **ADMIN** tab.
- REST: `/api/auth/*`, `/api/security/capabilities`, `/api/audit`, `/api/locks`, `/api/notifications/mutes`.
- WatchList writes on the web tier (`PUT /api/watchlist`, `POST /api/watchlist/import`) replace pipelines wholesale, so they need `Pipeline_Author` and are refused for **every** caller in Default mode (`X-Source` is not trusted). Minimal-API endpoints use `.RequirePermission(permission, denyInDefaultMode)` from `TestController.Api/Security/PermissionGuard.cs`. The web Import button is disabled with the same reason.

## Config / files
- `RBAC` section in `appsettings.json` (`RBAC:Enabled`), `Locks` section for lock timeouts.
- Database: `OrchestratorDbContext` (SQLite), migrations in `TestController.Persistence/Migrations`.
- Adding a permission: follow the `add-rbac-permission` skill. The enum is append-only and mirrored in four catalogs.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~Rbac"
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~Rbac"
```

## Known gaps
- Open hardening items for auth and audit are tracked in [REGISTER.md](../Hardening/REGISTER.md) (for example REG-031: SignalR broadcasts go to all clients).
- The TS permission catalog lags the C# enum: `Permission.cs` has 26 members, `PERMISSION_CATALOG.Administrator` in `capabilities.ts` has 24; `System_ChangeMode` and `Fleet_InstallUpdates` are missing on the web side (REG-048).

## Links
- Start here: [07_Implementation_Toolkit.md](07_Implementation_Toolkit.md), then [00_Master_Plan.md](00_Master_Plan.md) and [01_System_Design.md](01_System_Design.md)
- [02_Implementation_Roadmap.md](02_Implementation_Roadmap.md), [03_Integration_With_Lock_Spec.md](03_Integration_With_Lock_Spec.md), [04_UI_Mockup_Catalog.md](04_UI_Mockup_Catalog.md), [05_Default_Mode_Design.md](05_Default_Mode_Design.md), [06_Copilot_Implementation_Plan.md](06_Copilot_Implementation_Plan.md)
- [RBAC_Architecture_And_Change_Map.md](RBAC_Architecture_And_Change_Map.md), phase packs in [phase-packs/](phase-packs/), mockup [mockups/RBAC_TreeView_RealHierarchy_Mockup.html](mockups/RBAC_TreeView_RealHierarchy_Mockup.html)
