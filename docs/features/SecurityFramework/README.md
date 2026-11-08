---
feature: SecurityFramework
status: Implemented
owner: Vinodkumar Javvadi
code: [TestController.Api/Security/SecurityServiceExtensions.cs, TestController.Api/Security/SecurityOptions.cs, TestController.Api/Controllers/SecurityController.cs]
tests: [TestController.WebApi.Tests/Security, TestControllerGrpc.Tests/Security]
last-verified: 2026-11-08
---

# Security Framework: multi-identity REST authentication

## What it does
- `AddMultiIdentitySecurity()` authenticates REST callers on both hosts. `Security:AuthMode` is `None`, `Domain` (Windows negotiate/NTLM with AD groups), `Local` (local accounts) or `Token` (bearer tokens).
- `Security:AuthMode` picks the mode; it is separate from [RBAC](../RBAC/README.md) mode (`RBAC:Enabled`). The two layers stack.
- `SecurityController` reports the active mode and the caller's identity for diagnostics.

## How to use
- Set `Security:AuthMode` in `appsettings.json` of the WPF controller and of the IIS web tier.
- Check a host with `GET /api/security/status` and `GET /api/security/whoami`.
- First-time deployment and migration steps: [DEPLOYMENT-GUIDE.md](DEPLOYMENT-GUIDE.md), [MIGRATION-GUIDE.md](MIGRATION-GUIDE.md); problems: [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

## Config / files
- `Security` section (`SecurityOptions.SectionName`): `AuthMode`, plus option groups `Domain`, `Local`, `Token`, `Audit`, `Transport` (gRPC TLS mode and certificate) and `RateLimit`.
- Token secrets live in the token store (`Token:TokenStore`, default `secrets.json`); certificate passwords and tokens are never committed.

## How to test
```powershell
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~Security"
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~Security"
```

## Known gaps
- `AuthMode` defaults to `None`, which treats every caller as an administrator. Tracked as REG-001 in [REGISTER.md](../Hardening/REGISTER.md), with the evidence in [SECURITY-GAPS.md](../Hardening/SECURITY-GAPS.md).
- No TLS on the REST and gRPC endpoints yet (REG-003, REG-022).

## Links
- [MultiIdentity_Security_Framework_Requirements.md](MultiIdentity_Security_Framework_Requirements.md), [DEPLOYMENT-GUIDE.md](DEPLOYMENT-GUIDE.md), [MIGRATION-GUIDE.md](MIGRATION-GUIDE.md), [TROUBLESHOOTING.md](TROUBLESHOOTING.md)
