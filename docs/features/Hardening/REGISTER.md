# Hardening Register (single source of truth)

**Built:** 2026-10-30 (Phase 0) - **No code changed.**
**Supersedes as a tracker:** `SECURITY-GAPS.md` (2026-09-25), `00`-`09` risk tables, `AUDIT.md` stage 1, the known-open
items in repo memory. Those documents stay as the *evidence*; this file holds the *status*.

## Status vocabulary

| Status | Meaning |
|---|---|
| **Open** | Re-checked against today's code; the defect is still there. Evidence column cites file:line. |
| **Partial** | Part fixed, part still open - the row says which half. |
| **Fixed** | Fixed in code; the guarding test is named. |
| **Verified OK** | Re-checked; never was a defect, or a guard already pins the correct behaviour. |
| **Ext** | Lives outside the repo (production box, PKI, git history, product test code) - needs a human or an on-box check. |
| **Imported** | Copied from a source doc's own status; **not** re-checked in Phase 0 (low risk, see Appendix A). |

Severity: **C**ritical (security, data loss, run corruption) / **H**igh / **M**edium / **L**ow.
Source keys: `SG` = SECURITY-GAPS.md section, `P0x` = per-area plan, `AUD` = AUDIT.md, `MEM` = repo memory, `PACK` = prompt-pack hotspot #.

---

## 1. Security - User -> API

| ID | Sev | Finding | Source | Evidence today | Status | Test to add / guard |
|---|---|---|---|---|---|---|
| REG-001 | C | `AuthMode` defaults to `None`; `NoneAuthenticationHandler` mints `Role=Admin` for every request on both hosts | SG 1.1 | `TestController.Api/Security/SecurityOptions.cs:12`; `TestController.WebApi/appsettings.json:207` `"AuthMode": "None"`; no `Security` section in `TestControllerGrpc/appsettings.json` | **Open** | `AuthModeTests.Startup_Should_Fail_When_AuthModeIsNone_And_EnvironmentIsProduction` |
| REG-002 | H | `RbacGate` fails open when RBAC is enabled but its services are not registered | SG 1.2 | `TestController.Api/Security/RbacGate.cs:30-33` | **Open** | `RbacGateTests.IsAuthorized_Should_Deny_When_RbacEnabled_And_ServicesMissing` |
| REG-003 | H | No HTTPS anywhere; HSTS only when already HTTPS; no `UseHttpsRedirection`/`UseHsts` | SG 1.3 | grep: zero `UseHttpsRedirection`/`UseHsts` in hosts | **Open** (blocked on cert, REG-060) | `TransportTests.Startup_Should_Fail_When_NoHttpsBinding_And_EnvironmentIsProduction` |
| REG-004 | H | CORS reflects any origin **with credentials** when `Cors:AllowedOrigins` is absent (it is absent) | SG 1.4; **contradicts P03 WEBAPI-006 "Done"** | `TestController.WebApi/Program.cs:215` `SetIsOriginAllowed(_ => true)`; no `Cors` key in either WebApi appsettings | **Open** | `CorsTests.Default_Should_Not_ReflectArbitraryOrigin_When_AllowedOriginsMissing` |
| REG-005 | C | `trigger-all`, `trigger-event`, `retry-tag`, `preflight` served locally with no permission check - sibling of the gated `trigger/{tag}` | SG 1.5 | `TestController.WebApi/Endpoints/ExecutionEndpoints.cs:14-18`, zero `Permission.` in file; group only `RequireAuthorization(User)` (`Program.cs:510`) | **Open** | `ExecutionEndpointsRbacTests.TriggerEvent_Should_Return403_When_CallerLacksPipelineTrigger` (+ trigger-all, retry-tag) |
| REG-006 | C | WatchList mutation (`PUT /`, `/import`, `/refresh`) gated at "any authenticated user"; no permission exists for it | SG 1.6 | `WatchListEndpoints.cs:15-18`; `Program.cs:508` | **Open** (needs decision D-5 + appended permission) | `WatchListEndpointsRbacTests.SaveWatchList_Should_Return403_When_CallerIsEngineer` |
| REG-007 | H | Maintenance POSTs (`revert`, `reboot`, `precheck`, `cancel`, `clear-quarantine`, update snooze/ack/reboot-ready) need only `User`; only `PUT policy` is Admin | SG 1.7 (was UNVERIFIED, now confirmed) | `TestController.Api/Controllers/MaintenanceController.cs:16,63,92,118,126,133,174,183,199`; zero `RequirePermission` | **Open** | `MaintenanceControllerRbacTests.Revert_Should_Return403_When_CallerLacksFleetMaintain` |
| REG-008 | H | `GetMySessions` (and two sibling actions) take identity from client header `X-User-Id` | SG 1.8 | `ExecutionController.cs:1172`, `:1226`, `:1249` | **Open** | `ExecutionControllerTests.GetMySessions_Should_IgnoreHeader_When_PrincipalIsAuthenticated` |
| REG-009 | M | `/api/clientlogs` anonymous, unbounded, unescaped body written to server log | SG 1.10 | `TestController.WebApi/Program.cs:526-532` | **Open** | `ClientLogTests.Post_Should_Return401_When_Anonymous`; `..._Should_TruncateAndEscape_When_BodyContainsNewlines` |
| REG-010 | - | `/api/tokens`, `/api/deployment` Admin-gated; Impact/ReportCard `[RequirePermission]`; Audit manual checks | SG 1.9 | `Program.cs:518-519` | **Verified OK** (attribute correct; inert until REG-001) | covered by REG-001 test |

## 2. Security - Web proxy -> Controller

| ID | Sev | Finding | Source | Evidence today | Status | Test to add / guard |
|---|---|---|---|---|---|---|
| REG-011 | H | Controller REST/SignalR host binds all interfaces, plaintext; proxy hop unauthenticated | SG 2.1 | `TestControllerGrpc/Services/ControllerWebApiHost.cs:147` `ListenAnyIP` | **Open** (decision: is 5200 reached remotely by Dashboard/other?) | `ControllerHostTests.WebApi_Should_BindLoopbackOnly_When_TopologyIsCoLocated` |
| REG-012 | H | Forwarding middleware passes inbound `X-User-Id`/`X-Source` through -> forged audit actor | SG 2.2 | `TestController.WebApi/Services/ControllerExecutionForwardingMiddleware.cs:43-44`; consumed at `ExecutionController.cs:313,760` | **Open** | `ForwardingMiddlewareTests.Should_OverwriteInboundUserIdHeader_With_AuthenticatedPrincipal` |
| REG-013 | C | `X-Source: WPF` header is an authorization input (`ClientKind`) | SG 2.3 | `RbacGate.cs:35-37`; also `SystemModeController.cs:50` | **Open** | `RbacGateTests.ClientKind_Should_BeWeb_When_RequestArrivesOverHttp_EvenIf_XSourceClaimsWpf` |
| REG-014 | - | `ProxyCapabilityResolver` fails closed when controller unreachable | SG 2.4 | `RbacGateProxyHostTests` exists | **Verified OK** | keep `RbacGateProxyHostTests` |
| REG-015 | H | Split-brain `LockRegistry`: WebApi registers its own in-memory registry while `/api/locks` proxies to WPF; web-originated locks invisible to WPF and vanish on resync | SG 2.5, P03 WEBAPI-002, MEM | `TestController.WebApi/Program.cs:141`; `ProxiedControllerExclusionProvider.cs:24` | **Open** (ADR candidate) | cross-host lock test once shared |
| REG-016 | M | `ILockRegistry? lockRegistry = null` optional ctor param: a host that forgets to register it silently disables locking | MEM | `ExecutionController.cs:85` | **Open** | `ExecutionControllerDiTests.Should_Resolve_LockRegistry_In_Both_Hosts` |

## 3. Security - Controller <-> Agent

| ID | Sev | Finding | Source | Evidence today | Status | Test to add / guard |
|---|---|---|---|---|---|---|
| REG-017 | C | Agent->controller auth fail-open: shared secret empty | SG 3.1 | `TestControllerGrpc/appsettings.json:108` `"AgentSharedSecret": ""`; interceptor registered `ControllerGrpcServerHost.cs:95`; agent client already sends header (`TestAgentGrpc/Clients/TestControllerClient.cs:24`) | **Open** (config; client half ready) | `AgentAuthTests.Register_Should_Return16_Unauthenticated_When_TokenMissing` |
| REG-018 | H | `Register` accepts any name (no roster allowlist) | SG 3.2 | `TestControllerGrpcService.cs:47-54` (empty + length only) | **Open** | `RegisterTests.Register_Should_Reject_When_AgentNameNotInRoster` |
| REG-019 | C | `ResolveAgentAddress` returns attacker-chosen advertised endpoint verbatim (only loopback rejected) -> dispatch + `_VCloudPassword` exfiltration, SSRF | SG 3.3 | `TestControllerGrpcService.cs:187-201` | **Open** | `RegisterTests.Register_Should_IgnoreAdvertisedEndpoint_When_HostDoesNotMatchPeerOrRoster` |
| REG-020 | H | `UnRegister` has no validation at all - evict any agent by name | SG 3.4 | `TestControllerGrpcService.cs:87` | **Open** | `UnRegisterTests.UnRegister_Should_Reject_When_CallerIsNotThatAgent` |
| REG-021 | C | Agent gRPC server authenticates nobody: no interceptor, `ListenAnyIP(5200)` plaintext -> unauthenticated `RunCommand` (RCE, LocalSystem on most nodes) | SG 3.5 | `TestAgentGrpc/Program.cs:138`; zero `Interceptors.Add` in `TestAgentGrpc` | **Open** | `AgentAuthTests.RunCommand_Should_Return16_Unauthenticated_When_TokenMissing`; `AgentHostTests.Grpc_Should_RegisterAuthInterceptor` |
| REG-022 | H | gRPC TLS built but off (`EnableTls: false`, `GrpcMode: Plaintext`) | SG 3.6, P00 SYS-001 | `TestAgentGrpc/appsettings.json:37`; `TestController.WebApi/appsettings.json:226` | **Open** (blocked on REG-060 decision) | `ConfigValidatorTests.Validate_Should_Error_When_GrpcModeIsPlaintext_And_EnvironmentIsProduction` |
| REG-023 | H | Controller->agent channel attaches no `x-agent-token` (must ship with REG-021) | SG 3.7 | `AgentGrpcDispatcher.cs` - no header name match | **Open** | `AgentGrpcDispatcherTests.Channel_Should_AttachAgentToken_When_SecretConfigured` |
| REG-024 | H | `PushExecutionEvents` publishes any received event onto the bus - fabricated results while REG-017 is open | SG 3.8 | `TestControllerGrpcService.cs` `PushExecutionEvents` | **Open** (closed by REG-017) | `PushExecutionEventsTests.Should_Reject_When_Unauthenticated` |

## 4. Security - Agent -> OS

| ID | Sev | Finding | Source | Evidence today | Status | Test to add / guard |
|---|---|---|---|---|---|---|
| REG-025 | H | Command policy is `AuditOnly` - logs and executes | SG 4.1, P02 AGENT-002 | `TestAgentGrpc/appsettings.json:46,50` | **Open** (decision D-4: soak window) | `CommandPolicyTests.Evaluate_Should_Deny_When_ModeIsEnforce_And_CommandNotAllowlisted` |
| REG-026 | M | Missing/mis-resolved `commandpolicy.json` silently degrades to allow-all | SG 4.2 | `EnhancedCommandPolicyEvaluator.cs:30,193`. Note: repo file `TestAgentGrpc/commandpolicy.json` exists with `BlockOnUnknown: true` - whether it is in the publish payload is **UNVERIFIED** | **Open** | `CommandPolicyTests.Constructor_Should_Throw_When_ModeIsEnforce_And_PolicyFileMissing` |
| REG-027 | H | Raw string interpolation into `cmd.exe /c` / `powershell -File` + unconditional `-ExecutionPolicy Bypass`; validator covers only caller-supplied trigger params | SG 4.3, MEM (Pass 5.1) | agent `CommandExecutor.cs:809`; controller copy `AgentGrpcDispatcher.cs:1211`; `TriggerParameterValidator.cs:19` exists | **Partial** (trigger params validated; file-sourced values not) - ADR candidate | `ResolveInterpreterTests.Should_NotAllowCommandSeparator_When_ArgumentsContainAmpersand` |
| REG-028 | M | No working-directory policy for uncredentialed runs | SG 4.4 | `CommandExecutor.cs:872` sets it only inside the credential path | **Open** | `CommandExecutorTests.Should_SetWorkingDirectory_ToConfiguredScratchRoot_When_NoCredentialsSupplied` |
| REG-029 | M | Agent execution identity varies per node (LocalSystem vs auto-logon user), not reported | SG 4.5 | setup scripts not read | **Ext** | `AgentSnapshotTests.Should_ReportExecutionIdentity` + fleet survey |
| REG-030 | M | Per-action credentials over plaintext gRPC / in WatchList.xml | SG 4.6, P01 CTRL-003, P02 AGENT-001 | validator warning exists | **Partial** (redacted in logs; transport + at-rest open) | `CommandExecutorTests.Should_RefuseCredentials_When_ChannelIsPlaintext` |

## 5. Security - Live data, redaction, secrets

| ID | Sev | Finding | Source | Evidence today | Status | Test to add / guard |
|---|---|---|---|---|---|---|
| REG-031 | H | Every SignalR broadcast goes to `global`; every client sees every pipeline's raw output | SG 5.1 | `TestController.Api/Services/SignalRNotifier.cs:522-524`; `ExecutionController.cs:599,622,643,1054,1272,1322` | **Open** (decision D-6) | `SignalRNotifierTests.AgentOutput_Should_PublishToSessionGroup_Not_Global` |
| REG-032 | H | `JoinSession`/`JoinAllSessions`/`JoinAsUser` unauthorized (must ship with REG-031) | SG 5.2 | `TestController.Api/Hubs/ControllerHub.cs:44,57,63` | **Open** | `ControllerHubTests.JoinAsUser_Should_Throw_When_UserIdIsNotCaller` |
| REG-033 | L | `ImpactProgressHub.JoinRun` joins any client-chosen group | SG 5.3 | `TestController.WebApi/Hubs/ImpactProgressHub.cs:13` | **Open** | `ImpactProgressHubTests.JoinRun_Should_Reject_When_RunWasNotStartedByCaller` |
| REG-034 | H | Agent execution history stores **raw** stdout/stderr; served by unauthenticated `GetExecutionHistory` | SG 5.4; **contradicts P02 AGENT-005 "Done"** (only `Arguments` is redacted) | `CommandExecutor.cs:675` raw `AddOutputLine`; `ExecutionTracker.cs:137` redacts args only, `:147-148` copies raw lines | **Partial** | `CommandExecutorTests.ExecutionHistory_Should_StoreRedactedLine_When_OutputContainsSecret` |
| REG-035 | - | Agent audit `CommandStarted` passes resolved args | SG §7 redaction table | `CommandExecutor.cs:342` passes raw, **but** `AuditLogger.cs:135-143` redacts every field on write | **Verified OK** (SG §7 row is stale) | - |
| REG-036 | M | `SecurityRedactor` is keyword-shaped only; bare PAT/JWT/base64 pass through | SG 5.5 | - | **Open** | `SecurityRedactorTests.Should_Redact_When_ValueIsBareAdoPatToken` / `..._WhenValueIsJwt` |
| REG-037 | M | Two `SecurityRedactor` classes (agent vs Core); only Core has the run-time secret registry | PACK #2, MEM | `TestAgentGrpc/Services/SecurityRedactor.cs`, `TestControllerGrpc.Core/Services/` | **Open** (ADR candidate) | path-coverage map in Phase 1 |
| REG-038 | C | `GetUserToken` lines carry password + JWT in WASSmokeTest output, TRX, Logs.zip | PACK #1, MEM | repo fixtures are sanitized (`docs/FIRFailedTest/Fir/README.md:12`); FIR masks them (`FirRedactionTests`) | **Ext** (source is product test code; TRX on result shares not scanned) | on-box scan with `sanitize-logs` skill |
| REG-039 | C | vCloud credentials: still in **git history** (`Utilites/RevertAgents/new 1.txt`), in log-redaction backups on JVGR22, password not rotated | MEM | file deleted 2026-10-05; history not rewritten | **Ext** | rotation + backup deletion + coordinated history purge |
| REG-040 | H | `RevertRcloudMachine.bat` echoed password; fixed in repo but node copy under `C:\TestSetup\RevertAgents\` is outside the deploy mirror | MEM | `RevertScriptSecretTests` (4) | **Partial** (repo Fixed; node **Ext**) | hand-copy before rotation |
| REG-041 | H | Password still travels as a command-line argument to revert actions; `SecretEnv` built but WatchList not edited | MEM | `ActionConfig.SecretEnv` shipped | **Partial** (code Fixed; WatchList edit **Ext**) | - |
| REG-042 | H | `_VCloudPassword` plaintext in `pipeline-config.json`, substituted into action params over plaintext gRPC | SG §7 | production file not read in Phase 0 | **Ext** | move to machine env var resolved at dispatch |
| REG-043 | L | SMTP: no credentials, no `EnableSsl`, port 25 | SG §7 | zero `EnableSsl` in compiled code | **Open** | - |

## 6. Startup posture, audit, permission catalogs

| ID | Sev | Finding | Source | Evidence today | Status | Test to add / guard |
|---|---|---|---|---|---|---|
| REG-044 | H | `ConfigValidator` never errors on an unsafe posture: explicit `AuthMode=None` silent; RBAC off, empty secret, Plaintext unchecked; WPF host has no validator | SG §6, P09 OPS-005 claims Done | `TestController.WebApi/Services/ConfigValidator.cs:173-211` warnings only | **Open** (ship behind `Security:EnforceProductionPosture`, warn first) | `ConfigValidatorTests.Validate_Should_Error_When_AuthModeIsNone_And_EnforcePosture` |
| REG-045 | M | SignalR `EnableDetailedErrors = true` unconditionally | SG §6 | `ControllerWebApiHost.cs:207` | **Open** | `ControllerWebApiHostTests.DetailedErrors_Should_BeOff_When_NotDevelopment` |
| REG-046 | H | Audit drain discards a failed batch (`batch.Clear()` on next loop); `DropOldest`; `TryWrite` result ignored | SG §8 | `TestController.Persistence/Audit/QueuedAuditWriter.cs:21,67,79-84` | **Open** (decision D-3) | `AuditDrainWorkerTests.Should_RetainBatch_When_SaveChangesThrows` |
| REG-047 | H | Pipeline trigger writes **no** audit record (only cancel/force-release log `[Audit]` lines) | SG §8 | `ExecutionController.cs:307` `TriggerWatchItem`; `"Audit"` only at `:1262,1316,1388,1416` | **Open** | `ExecutionControllerAuditTests.Trigger_Should_WriteAuditEntry_When_RbacDisabled` |
| REG-048 | M | TS `PERMISSION_CATALOG.Administrator` = 24 vs C# enum 26 (missing `System_ChangeMode`, `Fleet_InstallUpdates`); test pins the drifted count | SG §9, MEM | `capabilities.test.ts:22` `toHaveLength(24)`; `Permission.cs` 26 members (counts re-checked 2026-11-08) | **Open** | file-analysis parity test (C# enum vs `capabilities.ts`) |
| REG-049 | M | `CapabilityChecker.Can` vs `AuthorizationService.CanAsync` disagree on Engineer + pipeline permission + `resourceId == null` | SG §9, MEM | `CapabilityChecker.cs:52` vs `AuthorizationService.cs:95` | **Partial** (call sites avoid it; divergence remains) | `CapabilityParityTests` cross-product Theory |

## 7. Correctness / reliability / operations

| ID | Sev | Finding | Source | Evidence today | Status | Test to add / guard |
|---|---|---|---|---|---|---|
| REG-050 | H | **New config keys never reach production**: deploy preserves `appsettings.json`; `Invoke-Patch` key-diff only warns | MEM, SG §6 | `deploy/fleet-inventory.json:44` preserve list | **Open** | make the key-diff a hard stop listing missing keys |
| REG-051 | H | Controller crash dumps ~600 MB each have filled C: twice; `Invoke-LogCleanup` default retention 60 d | MEM | `deploy/Invoke-LogCleanup.ps1:51,155` | **Open** (cap count/size, not just age) | Pester: cleanup keeps at most N dumps |
| REG-052 | M | IIS `stdoutLogEnabled="true"` with no cap (170 MB seen) | MEM | `TestController.WebApi/web.config:8` | **Open** | - |
| REG-053 | M | `StandalonePipelineExecutor` returns **success** for `SendMail` without sending | AUD 1.3 | `TestController.WebApi/Services/StandalonePipelineExecutor.cs:78-80` | **Open** | `StandaloneExecutorTests.SendMail_Should_ReportSkippedNotSucceeded` |
| REG-054 | M | `PreflightRunner` reports access-denied as "Not found" | MEM | `TestControllerGrpc.Core/Preflight/PreflightRunner.cs:290` | **Open** | `PreflightRunnerTests.Should_ReportDenied_When_PathAccessIsDenied` |
| REG-055 | L | `AdoChangeTranslator.ToWorkItemRef` links the REST API URL (renders raw XML) | MEM | `TestControllerGrpc.Core/Ado/AdoChangeTranslator.cs:99` | **Open** | reuse `WorkItemWebUrl` |
| REG-056 | M | FleetVM/RegistryVM discard `Subscribe` handles; eager construction keeps probes running | MEM | `FleetVM.cs:128-134`, `RegistryVM.cs:67-69` | **Open** (perf, Phase 3) | - |
| REG-057 | M | Maintenance summary tiles have no "scan failed" tile - failed scans read "0 pending" | MEM | - | **Imported** (not re-checked) | - |
| REG-058 | M | `RegressionImpactMatcher.MatchAsync` blanket catch swallows index-unavailable into `[]` | MEM | - | **Imported** | - |
| REG-059 | M | `SP2026R2 - WARM GR Setup` Initialize points at a non-existent config file | MEM | production WatchList | **Ext** | preflight on JVGR22 |

## 8. Blocked on decisions / infrastructure

| ID | Sev | Item | Status |
|---|---|---|---|
| REG-060 | H | Server certificate for `JVGR22.magellandev2000.dev.wonderware.com` (EKU server auth), port 444; gRPC certs via `New-GrpcCertificates.ps1` | **Ext** (PKI team, D-2) |
| REG-061 | C | Choose AuthMode (Domain / Token / Entra) + real AD groups | **Ext** (D-1) |
| REG-062 | H | `RBAC:Enabled=true` after staging pass (P4-2) | **Ext** (gated on REG-061) |
| REG-063 | M | Agent nodes rebooted mid-install by ManageEngine (`Id=1074`) - fleet-wide exposure | **Ext** (ops policy with ManageEngine owners) |

## 9. Prompt-pack hotspots that are already done (re-run the red-proof, don't re-audit)

| ID | Hotspot | Status | Guard |
|---|---|---|---|
| REG-064 | PACK #4 permit released on cancel/exception; nested groups never deadlock | **Fixed** | `TestControllerGrpc.Tests/Scaling/PipelineConcurrencyTests.cs` |
| REG-065 | PACK #5 scoped-run pipeline lock released in `finally` / early return | **Fixed** | `TestControllerGrpc.Tests/Services/ScopedRunPipelineLockTests.cs` |
| REG-066 | PACK #6 fresh NodeId per fan-out clone (builder); progress resolved per pipeline (AsyncLocal) | **Fixed** | `PipelineBuilder*Tests` |
| REG-067 | PACK #7 GlobalVariables rank 35 + release-match guard through shared funnels | **Fixed** | `TestControllerGrpc.Tests/Services/GlobalVariablesLayerTests.cs` |
| REG-068 | PACK #11 tag max 36 + `+`/space rejected at write boundary | **Fixed** | `PipelineBuilderCreateTests` (`PipelineTag.IsValid`) |
| REG-069 | PACK #14 FIR cache key, workbook staleness guard, redaction | **Fixed** | `FirCacheKeyTests`, `FirServiceTests`, `FirRedactionTests` |
| REG-070 | Ref-aware agent resolution (outage 2026-10-04) | **Fixed** | `TestControllerGrpc.Tests/Services/AgentResolverTests.cs` |
| REG-071 | Proto duplicated in 4 projects (AUD 1.4, P00 SYS-004, P08 TEST-004) | **Fixed** - one file `TestControllerGrpc.Core/Protos/test_agent.proto` | `TestControllerGrpc.Tests/ProtoConsistencyTests.cs` |
| REG-072 | PACK #8, #9, #10, #12, #13 (memory growth, WPF CPU, FileWatcher races, both-host DI, deploy guards) | **Not yet audited** -> Phase 1 | - |

---

## Contradictions found in the source docs (fix the docs, not the code)

| Doc row | Claims | Reality |
|---|---|---|
| P03 WEBAPI-006 (CORS) | Done | REG-004 Open - insecure branch is the default |
| P02 AGENT-005 (history redaction) | Done | REG-034 Partial - args redacted, stdout/stderr raw |
| P09 OPS-005 (config validation) | Done | REG-044 - validator exists but treats every unsafe posture as a warning |
| P00 SYS-004 / P08 TEST-004 (proto) | Not started | REG-071 Fixed |
| SG §7 audit `CommandStarted` row | unredacted | REG-035 Verified OK - `AuditLogger` redacts on write |
| AUD 1.4 "proto copied 4x" | 4 copies | 1 copy since consolidation |

## Appendix A - imported plan rows not re-checked in Phase 0

Status taken from the source doc. All are P1/P2 reliability items marked Done there; Phase 1 re-checks any that sit in an
audited folder. Still-open ones are listed explicitly.

- **Claimed Done:** CTRL-001, -002, -004..-007; AGENT-003, -004, -006, -007; WEBAPI-001, -003, -004, -005, -007, -008;
  API-001..-004, -006, -007; CORE-002..-007; CLIENT-001..-006; DISPLAY-001..-004; OPS-001..-004, -006.
- **Still open per source:** WEBAPI-002 (= REG-015), API-005 (shared API vs minimal-API endpoint parity - same shape as
  REG-005), TEST-001 (in progress), TEST-002, TEST-003, TEST-006, TEST-007, SYS-005..SYS-009.
- **AUDIT.md open questions Q1-Q5** (TestAgentDisplay use, Dashboard/Diagnostics abandoned, thin React audit/report card,
  `docs/archive/Program.cs`, standalone web host deployed?) - **Q5 answered**: yes, IIS `TestControllerWeb` on :81.

## Summary counts (Phase 0)

| Status | Count |
|---|---|
| Open | 38 |
| Partial | 7 |
| Fixed | 8 |
| Verified OK | 3 |
| Ext | 11 |
| Imported | 2 |
| Not yet audited | 1 (REG-072, five hotspots) |
