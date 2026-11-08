# SECURITY-GAPS.md — Requirement 11: Security Boundaries and Policy

**Assessed:** 2026-09-25 · **Method:** direct source read of the repo working tree. No code changed.
**Scope:** the five boundaries in requirement 11, plus startup posture, credential handling, audit durability, and permission-catalog drift.

> Every "Current state" row cites a file and a method. Where a claim could not be proven from source it is marked **UNVERIFIED** and says what evidence would settle it.

---

## 0. Verification of the stated starting points

| Claim to verify | Verdict | Evidence |
|---|---|---|
| gRPC is plaintext h2c on 5100/5200 | **Confirmed, and worse than stated** | Controller gRPC: `ControllerGrpcServerHost.RunServerAsync` → `kestrel.ListenAnyIP(_port, o => o.Protocols = HttpProtocols.Http2)`, port from `TestControllerGrpc/appsettings.json` `ControllerGrpcPort: 5100`. Agent gRPC: `TestAgentGrpc/Program.cs` `options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2)` with `AgentSettings:GrpcPort: 5200`. **Also plaintext and bound to all interfaces:** the controller's REST/SignalR host — `ControllerWebApiHost.RunServerAsync` → `ListenAnyIP(_port, Http1AndHttp2)` on `WebApiPort: 5200`. So three listeners, all `ListenAnyIP`, all plaintext. TLS exists in code (`AgentKestrel:EnableTls`, `GrpcTlsChannelFactory`) but ships `false` / `GrpcMode: "Plaintext"`. |
| RBAC has Default vs Secured | **Confirmed, and Default is the shipped default** | `TestControllerGrpc/appsettings.json` → `"RBAC": { "Enabled": false }`. `RbacGate.IsAuthorizedAsync` returns `true` immediately when `RbacOptions.Enabled` is false. |
| `SecurityRedactor` exists for agent command lines | **Confirmed, and it covers more than command lines** | `TestAgentGrpc/Services/SecurityRedactor.cs`. Applied in `CommandExecutor.BuildEvent` to `OutputLine`, `ErrorMessage`, `Command`, `Arguments`, `Detail` — so stdout/stderr *on the wire* is redacted. Gaps below. |
| Audit is enqueued to `IAuditWriter` | **Confirmed** | `TestController.Persistence/Audit/QueuedAuditWriter.cs`. In-memory bounded channel, `AuditDrainWorker` persists. Durability gaps below. |

### The finding that changes the meaning of everything else

`TestController.Api/Security/SecurityOptions.cs`:

```csharp
public AuthMode AuthMode { get; set; } = AuthMode.None;
```

`TestController.WebApi/appsettings.json` sets `"Security": { "AuthMode": "None" }` explicitly, `appsettings.Production.json` does **not** override it, and `TestControllerGrpc/appsettings.json` has **no `Security` section at all** (so it takes the `None` default too).

`TestController.Api/Security/NoneAuthModeProvider.cs`:

```csharp
protected override Task<AuthenticateResult> HandleAuthenticateAsync()
{
    var claims = new[] { ..., new Claim(ClaimTypes.Role, "Admin") };
    ...
    return Task.FromResult(AuthenticateResult.Success(ticket));
}
public UserRole ResolveRole(ClaimsPrincipal user) => UserRole.Admin;
```

Consequence: the global `SetFallbackPolicy(RequireAuthenticatedUser())` in `SecurityServiceExtensions.AddMultiIdentitySecurity` is satisfied by a forged principal on **every** request, `SecurityPolicies.Admin` (`AdminRoleRequirement`) is satisfied because the role claim is literally `"Admin"`, `SessionOwnershipChecker.CanAccessSession` returns `true` unconditionally via its `role == UserRole.Admin` short-circuit, and `RbacGate` never runs because RBAC is off.

**As shipped, every REST/SignalR endpoint on both hosts is anonymous-Administrator.** Authorization attributes across the codebase are correct in structure and inert in effect. Read the rest of this document with that in mind: most "missing permission check" findings are second-order until this is fixed, and several "present permission check" reassurances are worthless until it is fixed.

---

## 1. Boundary: User → API

| # | Current state (file / method) | Gap vs requirement | Risk if exploited | Effort | Test that proves it fixed |
|---|---|---|---|---|---|
| 1.1 | `SecurityOptions.AuthMode` defaults to `None`; `TestController.WebApi/appsettings.json` §`Security` sets `"None"`; `TestControllerGrpc/appsettings.json` has no `Security` section. `NoneAuthenticationHandler.HandleAuthenticateAsync` mints `Role=Admin`. | Requirement says Secured mode must be enforced. Nothing is enforced: auth is off and every caller is Admin. | Anyone who can reach port 81 or 5200 is a full administrator — trigger pipelines (= remote code execution on 9 test machines), create/delete users, export the audit log, flip system mode, force-release locks. No credential needed. | **S** (config) / **M** (pick a real mode + groups) | `AuthModeTests.Startup_Should_Fail_When_AuthModeIsNone_And_EnvironmentIsProduction`; integration test asserting an unauthenticated `GET /api/execution/sessions` returns **401**, not 200. |
| 1.2 | `RbacGate.IsAuthorizedAsync` — `if (rbacOptions is null \|\| !rbacOptions.CurrentValue.Enabled) return true;` and `if (authzService is null \|\| authInterceptor is null) return true;`. `RBAC:Enabled = false` shipped. | Fine-grained permission checks are a no-op in the shipped configuration, and also silently no-op on any host that forgets to register the RBAC services. | Every `[RequirePermission]` and `IsRbacAuthorizedAsync` call site is decorative. A DI wiring mistake downgrades security with no signal. | **S** (make missing-service a deny + log) / **M** (flip RBAC on) | `RbacGateTests.IsAuthorized_Should_Deny_When_RbacEnabled_And_ServicesMissing` — proven red by restoring the `return true`. |
| 1.3 | Transport: `ControllerWebApiHost.RunServerAsync` → `ListenAnyIP`, plaintext. `TestController.WebApi/Program.cs` has security headers and sets HSTS **only** `if (context.Request.IsHttps)`. No `UseHttpsRedirection`, no `UseHsts`. IIS site is `http/*:81:`. | No HTTPS anywhere. | Session bearer tokens, `X-Ado-Token`, WatchList payloads and all pipeline output cross the wire in clear. Anyone on the segment can replay a session token. | **M** (needs a cert — see Decisions) | `TransportTests.Startup_Should_Fail_When_NoHttpsBinding_And_EnvironmentIsProduction`; live probe asserting `http://host:81/` returns 307/308 to `https://`. |
| 1.4 | CORS: `TestController.WebApi/Program.cs` — when `Cors:AllowedOrigins` is empty it registers `p.SetIsOriginAllowed(_ => true) ... .AllowCredentials()`. **No `Cors` section exists in either appsettings file.** | Reflect-any-origin with credentials. | If auth is ever fixed to `Domain` (Negotiate), any web page a QA engineer visits can silently drive the API as that engineer, because the browser attaches Windows credentials cross-origin and CORS permits reading the response. This gap gets *worse* the moment 1.1 is fixed. | **S** | `CorsTests.Default_Should_Not_ReflectArbitraryOrigin_When_AllowedOriginsMissing`. |
| 1.5 | `TestController.WebApi/Endpoints/ExecutionEndpoints.cs` maps `trigger-all`, `trigger-event/{tag}/{eventIndex}`, `retry/{sessionId}`, `retry-tag/{tag}`, `preflight/{tag}` with **zero** permission calls (grep for `Permission.` in that file returns nothing). `ControllerExecutionForwardingMiddleware.ShouldForward` only forwards `/api/execution/trigger/`, `/api/execution/*/cancel`, `/api/pipelines/*/retry`. | Two routes reach the same action; only one is gated. `POST /api/execution/trigger/{tag}` checks `Pipeline_Trigger` in `ExecutionController`; `POST /api/execution/trigger-event/{tag}/0` is served locally by the WebApi with no check at all. | A user correctly denied `Pipeline_Trigger` on pipeline X triggers it anyway by calling the un-forwarded sibling route. Pure authorization bypass, independent of 1.1. | **M** | `ExecutionEndpointsRbacTests.TriggerEvent_Should_Return403_When_CallerLacksPipelineTrigger` (+ same for `trigger-all` → `Pipeline_TriggerAll`, `retry-tag` → `Pipeline_Retry`). |
| 1.6 | `TestController.WebApi/Endpoints/WatchListEndpoints.cs` — `MapPut("/")`, `MapPost("/import")`, `MapPost("/refresh")` under `RequireAuthorization(SecurityPolicies.User)` only. No RBAC permission exists for config mutation. | WatchList.xml is the definition of what commands run on which machines. Editing it is strictly more powerful than triggering. It is gated at "any authenticated user". | Write a new WatchItem whose action is `cmd.exe /c <anything>` against any agent, then trigger it with the weakest trigger permission. Full fleet compromise from a low-privilege account. | **M** (needs a new permission — append-only) | `WatchListEndpointsRbacTests.SaveWatchList_Should_Return403_When_CallerIsEngineer`. |
| 1.7 | `TestController.Api/Controllers/MaintenanceController.cs` — class has `[Authorize(SecurityPolicies.User)]`; the POST actions (`revert`, `reboot`, `precheck`, `{id}/cancel`, `nodes/{nodeId}/clear-quarantine`) have no `[RequirePermission]`. **UNVERIFIED in detail** — confirm by grepping `Permission.` in that file. | Destructive fleet operations at "any authenticated user". | Any authenticated user can revert a VM to snapshot mid-run, or reboot a node under test. Data loss and denial of service. Note the repo already records that no distinct RBAC permission exists for refresh. | **M** | `MaintenanceControllerRbacTests.Revert_Should_Return403_When_CallerLacksFleetMaintain`. |
| 1.8 | `ExecutionController.GetMySessions` — `var userId = HttpContext.Request.Headers["X-User-Id"].FirstOrDefault() ?? "";` then filters sessions by that value. | Identity for a sensitive read is taken from a client-controlled header. | Set `X-User-Id: <colleague>` and read their session list, including pipeline tags and run history. Trivial, needs no auth bypass. | **S** | `ExecutionControllerTests.GetMySessions_Should_IgnoreHeader_When_PrincipalIsAuthenticated`. |
| 1.9 | Correctly gated, for the record: `app.MapGroup("/api/tokens").…RequireAuthorization(SecurityPolicies.Admin)` and `app.MapGroup("/api/deployment").…RequireAuthorization(SecurityPolicies.Admin)` in `TestController.WebApi/Program.cs`. `ImpactController` / `BuildReportCardController` carry `[RequirePermission(CodeChurn_*/ReportCard_View)]`. `AuditController` resolves `Audit_View` / `Audit_Export` manually. | No gap in the attribute itself — but all of these are satisfied by the synthetic Admin of 1.1, and the `[RequirePermission]` ones additionally no-op under 1.2. | Token provisioning (credential minting) and audit export are reachable anonymously **because of 1.1**, not because the attribute is missing. | — | Covered by the 1.1 test. |
| 1.10 | `app.MapPost("/api/clientlogs", …).AllowAnonymous()` writes the raw request body to the server log via `logger.Warn("ClientLog", body)`. | Unauthenticated, unbounded, unredacted write into the operational log. | Log flooding (the C: drive has already been filled to zero bytes twice per repo history) and log-forging: injected text lands beside real `[Audit]` lines in the same file. | **S** | `ClientLogTests.Post_Should_Return401_When_Anonymous`; `…Should_TruncateAndEscape_When_BodyContainsNewlines`. |

---

## 2. Boundary: Web proxy → Controller

| # | Current state (file / method) | Gap vs requirement | Risk if exploited | Effort | Test that proves it fixed |
|---|---|---|---|---|---|
| 2.1 | `TestController.WebApi/Services/ControllerProxyService` — base URL from `ControllerProxyUrl` = `http://localhost:5200`, plain `HttpClient`, no client certificate, no shared secret, no signed header. The controller's listener is `ListenAnyIP`, **not** `ListenLocalhost`. | The proxy→controller hop is unauthenticated, and the hop's target is reachable from the network, not just loopback. | Anything that can reach `JVGR22:5200` **is** the web proxy. The "trusted internal hop" is trusted by convention only. | **S** (bind 5200 to loopback + firewall) / **L** (mutual auth) | `ControllerHostTests.WebApi_Should_BindLoopbackOnly_When_TopologyIsCoLocated`; a live probe from another host asserting `:5200` refuses connection. |
| 2.2 | `ControllerProxyService.ForwardExecutionAsync` adds `X-User-Id` and `X-Source` outbound. `ControllerExecutionForwardingMiddleware` reads `context.Request.Headers["X-User-Id"]` **from the inbound browser request** and passes it through — it does not strip or overwrite a caller-supplied value. | A client-supplied header becomes the actor identity downstream. There is no marker distinguishing "the proxy asserted this" from "the browser asked for this". | Attribution forgery: trigger a pipeline as `X-User-Id: someone.else`. The controller's audit line (`_appLogger.Log(…, "Audit", $"…actor={userId}…")`) records the forged name. Blame lands on the wrong person and the real actor is unrecoverable. | **S** | `ForwardingMiddlewareTests.Should_OverwriteInboundUserIdHeader_With_AuthenticatedPrincipal`. |
| 2.3 | `RbacGate.IsAuthorizedAsync`: <br>`var source = http.Request.Headers["X-Source"].FirstOrDefault();`<br>`var clientKind = string.Equals(source, "WPF", …) ? ClientKind.Wpf : ClientKind.Web;` <br>and `AuthorizationService.EvaluateDefaultMode`: `if (user.ClientKind == ClientKind.Wpf) return AuthDecision.Allow("default-mode-wpf");` | **A client-controlled header is an authorization input.** | In Default mode the web client is meant to be read-only. Sending `X-Source: WPF` on any request flips `ClientKind` to `Wpf` and `EvaluateDefaultMode` returns *unconditional allow*. The entire Default-mode read-only guarantee is one header away from bypass. This is a real bypass even with RBAC nominally engaged. | **S** | `RbacGateTests.ClientKind_Should_BeWeb_When_RequestArrivesOverHttp_EvenIf_XSourceClaimsWpf` — proven red by restoring the header read. |
| 2.4 | `TestController.WebApi/Services/ProxyCapabilityResolver.GetCapabilitiesAsync` forwards the caller's `Authorization` header to `/api/auth/me` on the controller and reads the `capabilities` array; returns `null` (→ deny) when the controller is unreachable. | No gap — this one is correct and fails closed. Documented here so it is not "fixed" into something worse. | — | — | Existing `RbacGateProxyHostTests`. Keep them. |
| 2.5 | `ControllerProxyService` HttpClient timeout is 5 s (per proxy code); `AuthController` and `LocksController` are in `ProxiedControllerExclusionProvider.ExcludedControllers`. Lock registry is an in-memory singleton duplicated in both processes (recorded in repo memory as a known split-brain). | Not strictly a security boundary defect, but it means a lock taken on the web tier is invisible to the controller — the pipeline lock is not a reliable security control across hosts. | Two users can believe they hold the same pipeline. Concurrency, not confidentiality. | **M** | Existing lock tests + a cross-host test once the registry is shared. |

---

## 3. Boundary: Controller ↔ Agent (both directions)

| # | Current state (file / method) | Gap vs requirement | Risk if exploited | Effort | Test that proves it fixed |
|---|---|---|---|---|---|
| 3.1 | `TestControllerGrpc/Services/AgentAuthInterceptor.Authenticate`:<br>`if (_secret is null) { warn once; return; }`<br>Secret source: `ControllerGrpcServerHost` ctor → `config["Controller:AgentSharedSecret"] ?? Environment.GetEnvironmentVariable("AGENT_SHARED_SECRET")`. Shipped value: `"AgentSharedSecret": ""`. | Agent→controller authentication is **fail-open and off**. The interceptor is correct (constant-time compare, covers unary + client-streaming, which is all `TestControllerService` exposes) — it is simply never armed. | Any host on the network can call `Register`, `UnRegister`, `Heartbeat` and `PushExecutionEvents` on :5100. See 3.2 and 3.3 for what that buys. | **S** (set the secret) | `AgentAuthTests.Register_Should_Return16_Unauthenticated_When_TokenMissing` against a host configured with a secret; plus a startup test that **fails** when the secret is empty in Production. |
| 3.2 | `TestControllerGrpcService.Register` — identity is `request.Name`, validated only for non-empty and length ≤ `MaxAgentNameLength`. No allowlist, no certificate, no binding of name to peer address. | A node name is treated as a machine identity. | Register as `"JVGR1"` from any machine and you become JVGR1 in the dispatcher. Combined with 3.3 this redirects that agent's work to you. | **M** (allowlist from `Agents[]` config, already present in appsettings) / **L** (per-agent credentials) | `RegisterTests.Register_Should_Reject_When_AgentNameNotInRoster`. |
| 3.3 | `TestControllerGrpcService.ResolveAgentAddress` — returns `advertisedEndpoint` verbatim whenever it parses as an absolute URI and is not loopback. Loopback is the **only** rejection. | The controller lets an unauthenticated caller choose the address the controller will subsequently dial for that agent. | **This is the worst single finding.** Register as `JVGR1` with `Endpoint = http://attacker:5200`. Every subsequent `RunCommand` for JVGR1 is dispatched to the attacker — including `action.Parameters` after `ParameterResolver.Resolve` has substituted `[_VCloudPassword]` and friends. It is simultaneously credential exfiltration, an SSRF primitive against anything the controller can reach, and a silent denial of service for the real node. | **M** | `RegisterTests.Register_Should_IgnoreAdvertisedEndpoint_When_HostDoesNotMatchPeerOrRoster` — proven red by restoring the verbatim return. |
| 3.4 | `TestControllerGrpcService.UnRegister` — `_dispatcher.UnregisterAgent(request.Name);` with no validation at all (no length check, no ownership check, no auth while 3.1 is open). | Any caller can evict any agent by name. | Unregister all 9 agents in a loop; the fleet goes dark and in-flight dispatch fails. Recovery needs a human on each node. | **S** | `UnRegisterTests.UnRegister_Should_Reject_When_CallerIsNotThatAgent`. |
| 3.5 | `TestAgentGrpc/Program.cs` — `builder.Services.AddGrpc(options => { MaxReceive…; MaxSend…; EnableDetailedErrors… });`. **No `Interceptors.Add` anywhere in the project** (verified by grep). Listener is `ListenAnyIP(5200)` plaintext h2c. | The agent authenticates nobody. The controller→agent direction has no authentication in either the client or the server. | Anyone who can reach `<agent>:5200` can call `RunCommand` / `RunCommandStreamed` → arbitrary process execution as the agent's service account (LocalSystem on `Setup-AgentNode.ps1`-provisioned nodes). They can also call `GetExecutionHistory` and `GetAuditLog` to read prior runs. **This is unauthenticated RCE on every agent node, reachable from anywhere the network allows.** | **M** (reuse `AgentAuthInterceptor` on the agent) | `AgentAuthTests.RunCommand_Should_Return16_Unauthenticated_When_TokenMissing`; `AgentHostTests.Grpc_Should_RegisterAuthInterceptor`. |
| 3.6 | `TestAgentGrpc/appsettings.json` → `AgentKestrel.EnableTls: false`, `TlsPort: 5443`, `RequireClientCertificate: false`. `TestController.WebApi/appsettings.json` → `Security.Transport.GrpcMode: "Plaintext"`, `RequireMutualTls: false`. TLS machinery exists and looks sound (`GrpcTlsChannelFactory.ValidateServerCertificate` supports CA-thumbprint pinning; `Program.cs` enforces `Tls12\|Tls13`; `deploy/New-GrpcCertificates.ps1` exists). | The whole TLS/mTLS layer is built, correct-looking, and switched off. `ConfigValidator` only checks cert config when `GrpcMode` is already TLS — it never objects to `Plaintext`. | Command lines (with substituted secrets), stdout, and credentials in `RunCommandRequest.userName/password` all cross the wire unencrypted between the controller and 9 nodes. | **L** (certificate issuance + rollout ordering) | `ConfigValidatorTests.Validate_Should_Error_When_GrpcModeIsPlaintext_And_EnvironmentIsProduction`; live handshake test asserting a plaintext dial to :5443 fails. |
| 3.7 | `AgentGrpcDispatcher` outbound channel (`GrpcChannel.ForAddress(address, …)`) attaches no `x-agent-token` metadata (no match in that file), and `NormalizeAddress` defaults a bare host to `http://{host}:5200`. | Controller→agent calls carry no credential. Symmetric with 3.5: nothing to present because nothing checks. | Fixing 3.5 alone will break dispatch unless the token is added here in the same change. | **S** (part of the 3.5 change) | `AgentGrpcDispatcherTests.Channel_Should_AttachAgentToken_When_SecretConfigured`. |
| 3.8 | Direction of travel, for completeness: agent→controller is `Register` / `UnRegister` / `UpdateClientState` / `Heartbeat` / `PushExecutionEvents` (client-stream). Controller→agent is everything on `TestAgentService`, incl. two server-streaming RPCs. `PushExecutionEvents` publishes every received `ExecutionEvent` straight onto the in-process event bus. | With 3.1 open, an unauthenticated caller can inject arbitrary `ExecutionEvent`s that flow to the WPF UI, SignalR and the results pipeline. | Fabricated "all tests passed" output and fabricated node state. Integrity of the test record, which is the product this system exists to produce. | **S** (closed by 3.1) | `PushExecutionEventsTests.Should_Reject_When_Unauthenticated`. |

---

## 4. Boundary: Agent → OS

| # | Current state (file / method) | Gap vs requirement | Risk if exploited | Effort | Test that proves it fixed |
|---|---|---|---|---|---|
| 4.1 | `TestAgentGrpc/appsettings.json` → `CommandPolicy: { "Mode": "AuditOnly", "AllowedCommandPrefixes": [], "AllowlistPath": "commandpolicy.json", "BlockOnUnknown": false }`. `CommandExecutor.EvaluateCommandPolicy` ends with `if (_enhancedPolicy.IsEnforced) { … return false; } return true;`. | There is a real allow/block engine (`EnhancedCommandPolicyEvaluator`: blocklist → allowlist → path roots → shell-chaining/exec-path) and it is configured to **log and proceed**. No approved-action-type list is in force. | Nothing the agent is asked to run is ever refused. The policy produces a warning line and executes the command anyway. | **S** (flip to `Enforce` after a soak in AuditOnly) — the risk is operational, not technical | `CommandPolicyTests.Evaluate_Should_Deny_When_ModeIsEnforce_And_CommandNotAllowlisted`; plus a deployment test asserting `commandpolicy.json` exists next to the agent binary (an absent file silently disables allowlist + blocklist — see 4.2). |
| 4.2 | `EnhancedCommandPolicyEvaluator` ctor: the policy file is only loaded `if (!string.IsNullOrEmpty(_policyFilePath) && File.Exists(_policyFilePath))`. `AllowlistPath` is the **relative** string `"commandpolicy.json"`, resolved against the process working directory. `CheckAllowlist` returns `Allowed()` when the list is empty *or* when `BlockOnUnknown != true`. | A missing or mis-resolved policy file degrades silently to allow-all. No startup log distinguishes "no policy configured" from "policy file not found". | The control can be defeated by a deployment accident. Someone turns `Mode: Enforce` on, the file is not in the publish output, and everyone believes the fleet is protected. | **S** | `CommandPolicyTests.Constructor_Should_Throw_When_ModeIsEnforce_And_PolicyFileMissing`. |
| 4.3 | `CommandExecutor.ResolveInterpreter`:<br>`".bat" or ".cmd" => ("cmd.exe", $"/c \"{cmd}\" {arguments}")`<br>`".ps1" => ("powershell.exe", $"-ExecutionPolicy Bypass -NoProfile -File \"{cmd}\" {arguments}")` | `arguments` is interpolated into a raw command line with no quoting or escaping. `-ExecutionPolicy Bypass` is unconditional. | Argument injection into `cmd.exe /c`. This is the exact path `TriggerParameterValidator` was written to defend, and that validator only covers **caller-supplied** trigger parameters — anything sourced from `pipeline-config.json`, `WatchList.xml` or a trigger file reaches the shell unvalidated. Combined with 1.6 (ungated WatchList write) it is a clean privilege escalation to LocalSystem. | **M** (cannot simply switch to `ArgumentList` — WatchList authors supply one raw args string, so semantics would change) | `ResolveInterpreterTests.Should_NotAllowCommandSeparator_When_ArgumentsContainAmpersand`; keep the existing 27 `TriggerParameterValidatorTests` and extend the validator to file-sourced values behind a flag. |
| 4.4 | `CommandExecutor.ExecuteAsync` builds `ProcessStartInfo` with no `WorkingDirectory` unless `ApplyCredentials` runs; `ApplyCredentials` sets it to `%WINDIR%\Temp` only because `CreateProcessWithLogonW` demands one. | No working-directory policy. The child inherits the agent service's CWD (its install directory) in the common case. | A relative path in a WatchList action resolves against the agent install folder — including relative writes. Makes DLL/script planting in the agent directory a viable pivot. | **S** | `CommandExecutorTests.Should_SetWorkingDirectory_ToConfiguredScratchRoot_When_NoCredentialsSupplied`. |
| 4.5 | Execution account: repo memory records `Setup-AgentNode.ps1` provisions the agent as **LocalSystem**, while `Setup-InteractiveAgent.ps1` uses an auto-logon user — so it varies per node. **UNVERIFIED here** (the setup scripts were not read in this pass). | No documented or enforced execution identity, and no runtime report of it. | Under LocalSystem, 3.5 (unauthenticated `RunCommand`) is unauthenticated SYSTEM-level RCE. Under the interactive user it is still that user's full rights. | **M** (survey + standardise) / **S** (report it) | `AgentSnapshotTests.Should_ReportExecutionIdentity` + a fleet survey script asserting every node's service account matches policy. |
| 4.6 | `CommandExecutor.ExecuteAsync` accepts `userName` / `password` from the `RunCommandRequest` and builds a `SecureString`. `WatchListValidator` (`Core/Services`) already raises a warning: *"Password is stored in plaintext in the XML."* | Per-action credentials travel over the plaintext gRPC channel (3.6) and may be stored in plaintext in `WatchList.xml`. | Harvest a privileged domain credential by sniffing one dispatch, or by reading `WatchList.xml` (which is world-readable on the controller and in `preserveFiles`). | **M** | `WatchListValidatorTests` already covers the warning; add `CommandExecutorTests.Should_RefuseCredentials_When_ChannelIsPlaintext`. |

---

## 5. Boundary: User → live data

| # | Current state (file / method) | Gap vs requirement | Risk if exploited | Effort | Test that proves it fixed |
|---|---|---|---|---|---|
| 5.1 | `TestController.Api/Services/SignalRNotifier` — **every** broadcast goes through `SendSafe`/`SendSafeAsync`, which do `_hub.Clients.Group("global").SendAsync(...)`. `ControllerHub.OnConnectedAsync` auto-joins every connection to `"global"`. Broadcast methods include `AgentOutputBatch`, `ActionProgress`, `ExecutionStarted`, `ExecutionCompleted`, `AgentStatusChanged`, `AgentLocksChanged`. | Live data is not authorized per resource — it is not segmented at all. The session groups exist but nothing publishes to them. | Every connected client receives the raw stdout/stderr of every pipeline on every agent, regardless of pipeline assignment. An Engineer assigned to one pipeline sees all of them. Redaction (5.4) is the only thing between that stream and any secret a script prints. | **M** | `SignalRNotifierTests.AgentOutput_Should_PublishToSessionGroup_Not_Global`; `…_Should_NotReachSubscriber_When_SubscriberIsNotAuthorizedForPipeline`. |
| 5.2 | `ControllerHub.JoinSession(string sessionId)`, `JoinAsUser(string userId)`, `JoinAllSessions()` — each is a bare `Groups.AddToGroupAsync(...)` with no ownership or permission check. | Group subscription is unauthorized and the key is a guessable/observable id. | Once 5.1 is fixed these become the bypass: join `user:<colleague>` or `all-sessions` and get the data back. Fixing 5.1 without 5.2 achieves nothing. | **S** (but must ship with 5.1) | `ControllerHubTests.JoinAsUser_Should_Throw_When_UserIdIsNotCaller`; `JoinAllSessions_Should_Throw_When_CallerLacksPipelineViewAll`. |
| 5.3 | `TestController.WebApi/Hubs/ImpactProgressHub.JoinRun(string correlationId) => Groups.AddToGroupAsync(Context.ConnectionId, correlationId);` — no check. `app.MapHub<ImpactProgressHub>("/hubs/impact")` with no `RequireAuthorization` (inherits the fallback policy only). | Impact results streamed to a client-chosen group name. | Observe another user's impact-mapping run if you learn their correlation id. Lower severity — the correlation id is client-generated and not enumerable — but it is still unauthorized-by-resource. | **S** | `ImpactProgressHubTests.JoinRun_Should_Reject_When_RunWasNotStartedByCaller`. |
| 5.4 | `CommandExecutor.BuildEvent` applies `SecurityRedactor.Redact` to `OutputLine`/`ErrorMessage`/`Command`/`Arguments`/`Detail`. But `StreamOutputAsync` calls `record.AddOutputLine(kind, line)` with the **raw, unredacted** line, and that record is served by the `GetExecutionHistory` and `GetAuditLog` RPCs. | Redaction covers the live wire but not the retained history — and those two RPCs sit on the agent, which has no authentication (3.5). | A secret printed by a script is scrubbed from the dashboard and kept verbatim in the agent's execution history, retrievable by anyone who can reach the agent's port. The dashboard being clean actively hides this. | **S** | `CommandExecutorTests.ExecutionHistory_Should_StoreRedactedLine_When_OutputContainsSecret` — proven red by restoring the raw `AddOutputLine`. |
| 5.5 | `SecurityRedactor` regexes require a `key=value` / `key: value` / `-key value` / `"key":"value"` shape around one of 12 keywords. | A bare secret with no adjacent keyword (a token echoed on its own line, a base64 blob, a connection string in an unusual form, a PAT in a URL) passes through untouched. `ContainsSensitiveKeyword` is keyword-based too. | False confidence. "SecurityRedactor exists" is not the same as "output is safe to broadcast to everyone", and 5.1 currently broadcasts to everyone. | **M** (add entropy/format detectors for PAT, JWT, base64 blobs; treat redaction as defence-in-depth, not the control) | `SecurityRedactorTests.Should_Redact_When_ValueIsBareAdoPatToken` / `…_WhenValueIsJwt`. |
| 5.6 | `WatchListController.GetParameters` redacts values whose key contains `password\|passwd\|pwd\|secret\|token\|apikey\|api_key\|credential` → `********`. `ExecutionController.GetRecentLogs` and `CancelSession` do check `_ownershipChecker.CanAccessSession`. | These are the *correct* patterns in the codebase — but `SessionOwnershipChecker.CanAccessSession` short-circuits on `role == UserRole.Admin`, and under 1.1 everyone is Admin. | The ownership control is real code that currently returns `true` for everybody. Do not mistake its presence for protection. | — | Covered by the 1.1 test; add `SessionOwnershipCheckerTests.Should_Deny_When_CallerIsNotOwner_And_ModeIsDomain`. |

---

## 6. Startup posture — does production reject an unsafe configuration?

**No.** `TestController.WebApi/Program.cs` does fail fast in non-Development:

```csharp
var validationResult = configValidator.Validate();
if (!validationResult.IsValid && !app.Environment.IsDevelopment())
    throw new InvalidOperationException(...);
```

but `ConfigValidator.ValidateSecurityConfig` (`TestController.WebApi/Services/ConfigValidator.cs`) never produces an **error** for an unsafe posture:

- Missing `Security` section → `warnings.Add("... AuthMode=None (no authentication) will be used.")`
- `AuthMode` present but blank → warning
- **`AuthMode` explicitly `"None"` → no message at all.** The shipped config takes this branch, so production starts completely silently with authentication disabled.
- `GrpcMode: "Plaintext"` → no check (cert validation only fires for `TlsOnly` / `PlaintextAndTls`)
- `RBAC:Enabled` → never examined
- `Controller:AgentSharedSecret` empty → never examined (only a once-per-process `_logger.Warn` from `AgentAuthInterceptor`)
- No HTTPS binding → never examined

The WPF host has **no equivalent validation at all** — `ControllerWebApiHost.RunServerAsync` and `ControllerGrpcServerHost.RunServerAsync` just start.

The agent does slightly better: `TestAgentGrpc/Program.cs` *throws* on a scheme mismatch between `AgentEndpoint` and `EnableTls`, and warns (does not fail) on plaintext h2c when `WarnOnPlaintextHttp2 && !IsDevelopment()`.

### Where a dev bypass leaks into the release package

| Bypass | How it reaches production |
|---|---|
| `AuthMode: "None"` | It is in `TestController.WebApi/appsettings.json`, which is in the publish output. `appsettings.Production.json` does not override it. |
| `RBAC:Enabled: false` | In `TestControllerGrpc/appsettings.json`, shipped. |
| `AgentSharedSecret: ""` | Shipped in both controller and agent appsettings. |
| `EnableTls: false` / `GrpcMode: "Plaintext"` | Shipped. |
| `CommandPolicy.Mode: "AuditOnly"` | Shipped in the agent package. |
| CORS reflect-any-origin | Reached by the **absence** of a `Cors` section — nothing to review in a diff. This is the most insidious one: the insecure branch is the default branch. |
| `EnableDetailedErrors = true` | `ControllerWebApiHost` sets it unconditionally for SignalR (`o.EnableDetailedErrors = true;`), not gated on environment. The agent gates its gRPC equivalent on `IsDevelopment()`. |
| Anything new | The deploy procedure deliberately excludes `appsettings*.json` to preserve hand-tuned production config (documented in repo memory). So a security default hardened in source **can never reach production by deploying**. Any config-only fix below requires a separate, manual, per-host edit. |

---

## 7. Credentials

| Secret | Where it lives | Gap |
|---|---|---|
| ADO PAT | `ADO_PAT` machine env var, read by `PatTokenProvider.GetPat()`. Never in config. | **Good pattern.** Keep it. Known operational issue: a PAT survives ~2 days in this tenant under Conditional Access. |
| ADO client secret | `ADO_CLIENT_SECRET` env var (`ServicePrincipalTokenProvider`). | Good pattern; path is inert (no config deployed). |
| Agent shared secret | `Controller:AgentSharedSecret` in appsettings **or** `AGENT_SHARED_SECRET` env var. | Config path exists and invites storing it in a file that sits in the publish payload. Prefer env-var-only; make the config key a startup error in Production. |
| vCloud credentials | `RCLOUD_USER` / `RCLOUD_PASSWORD` machine env vars, consumed by `Vm-Ops.vcloud.ps1`. | Good pattern. |
| `_VCloudPassword` | **Plaintext in `C:\TestControllerService\Parameters\SP2023R2SP2\pipeline-config.json`**, substituted by `ParameterResolver` into `action.Parameters` and shipped to agents over plaintext gRPC. | The highest-value secret in the system is on disk in clear, crosses the network in clear, and is the payload an attacker gets for free from gap 3.3. |
| WatchList action credentials | Optionally plaintext in `WatchList.xml` (`WatchListValidator` warns). | Same class as above. |
| SMTP | `ResultsEndpoints.SendReport` → `new SmtpClient(config.SmtpServer, config.SmtpPort)`. No credentials, no `EnableSsl`. Port 25. | Unauthenticated anonymous relay, cleartext. Low direct risk, but the report body carries build/test detail. |
| Session tokens | `TestController.Persistence/Identity/SessionStore` (note: returns a copy with plaintext token bytes for the caller). | Travels over HTTP (1.3). |

### Redaction coverage

| Surface | Covered? |
|---|---|
| Agent stdout/stderr on the gRPC wire | **Yes** — `CommandExecutor.BuildEvent`. |
| Agent execution history (`GetExecutionHistory`) | **No** — raw line stored (5.4). |
| Agent audit log file | Partly — `EvaluateCommandPolicy` logs `SecurityRedactor.RedactCommandLine(...)`, but `_audit.Log("CommandStarted", … arguments: resolvedArgs …)` passes the **resolved, unredacted** arguments. |
| SignalR broadcasts | Inherits agent-side redaction only; no server-side redaction layer before `Clients.Group("global")`. |
| ADO error bodies | **Yes** — `AdoClient.SummarizeErrorBody` (40 KB → 535 chars, verified in prod per repo memory). Good precedent to copy. |
| `/api/watchlist/{tag}/parameters` | **Yes** — key-fragment redaction to `********`. |
| Consolidated run email | **Yes** — whitelist, never enumerates `ResolvedParameters`. |
| `/api/clientlogs` | **No** — raw body written to the log (1.10). |
| Controller `[Audit]` app-log lines | No redaction, but they carry only actor/source/tag. |

---

## 8. Audit durability

`QueuedAuditWriter`:

```csharp
_channel = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(capacity) {
    FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, });
public void Enqueue(AuditEntry entry) => _channel.Writer.TryWrite(entry);
```

`AuditDrainWorker.ExecuteAsync` reads up to 100 entries into `batch`, writes them, and on failure:

```csharp
catch (Exception ex) { _logger.LogError(...); await Task.Delay(1000, stoppingToken); }
```

The next iteration calls `batch.Clear()`. **The entries were already removed from the channel, so a failed batch is discarded permanently.** There is no retry, no dead-letter, no file fallback.

Three independent loss modes:

1. **Crash** — everything queued but not yet drained is gone. The process force-kill path is well-trodden here (`CrashDumpHelper` writes ~626 MB per kill, per repo history), so this is not hypothetical.
2. **Store outage** — SQLite locked, disk full (C: has hit zero bytes twice), or `orchestrator.db` mid-deploy: up to 100 entries lost *per second of outage*, silently. The only trace is an `AuditDrainWorker failed to flush` line in the same log that is also full.
3. **Burst** — `DropOldest` at 10 000 means a flood of low-value entries evicts the high-value ones, and `TryWrite`'s return value is discarded so nothing notices.

### Which critical events are lost

| Event | Current audit path | Survives a crash? |
|---|---|---|
| **Pipeline trigger** (`ExecutionController.TriggerWatchItem`) | **None.** No `_auditWriter.Enqueue`, no `[Audit]` app-log line (grep for `"Audit"` in that file returns only lines 1262/1316/1388/1416 — cancel-all, cancel-session, force-release, force-release-all). In Default mode `RbacGate` returns before `CanAsync`, so `AuthorizationService.EnqueueAudit` never fires either. | **Never recorded at all.** The single most consequential action in the system — remote code execution across the fleet — has no audit record in the shipped configuration. |
| Cancel session / cancel all | `_appLogger.Log(LogLevel.Warning, "Audit", …)` → app log file, plus `_auditLogger.LogAuthorization` on denial | App log survives (file, flushed); the DB row does not exist. Actor is the forgeable `X-User-Id` (2.2). |
| Force release (agent / all) | Same app-log pattern | As above. |
| Privilege change (user create/update/delete/assign) | `UserService` → `_auditWriter.Enqueue` ×7 | **Lost on crash or store outage.** |
| Mode change Default↔Secured | `RbacModeTransitionService` → `_auditWriter.Enqueue` ×3 | **Lost on crash or store outage.** This is the one event that must never be lost — it is the record of security being turned off. |
| Lock acquire / force-release | `LockService` → `_auditWriter.Enqueue` ×2 | Lost on crash. |
| Every authorization decision | `AuthorizationService.EnqueueAudit` | Lost on crash. **Not written at all while RBAC is disabled.** |

Effort to fix: **M**. Minimum viable: (a) write the batch *before* removing it from the channel, or re-enqueue on failure with a bounded retry; (b) a synchronous append-only file sink for a small "critical" set — trigger, mode change, cancel, force release, user mutation — so those survive both a crash and a DB outage; (c) surface drop counts as a metric and fail readiness when non-zero. See Decisions for the policy question.

Test: `AuditDrainWorkerTests.Should_RetainBatch_When_SaveChangesThrows` (red-proved by restoring the `batch.Clear()` discard); `QueuedAuditWriterTests.Should_IncrementDropCounter_When_ChannelFull`; `ExecutionControllerAuditTests.Trigger_Should_WriteAuditEntry_When_RbacDisabled`.

---

## 9. Permission catalog drift

There are **five** definitions of the permission model, not two:

| # | Definition | File | Content |
|---|---|---|---|
| 1 | The enum (wire/persistence contract) | `TestControllerGrpc.Core/Authorization/Permission.cs` | 23 members, no explicit values (ordinal 0–22), append-only by convention |
| 2 | The authority | `TestController.Persistence/Authorization/AuthorizationService.cs` | 4 buckets: `IsReadPermission` (2), `IsAdminOnlyPermission` (10), `IsSeniorManagerOnlyPermission` (7), `IsPipelineScopedPermission` (3) + `Notification_Mute` handled inline = 23 |
| 3 | The capability list for `/api/auth/me` | `TestControllerGrpc.Core/Authorization/PermissionCatalog.cs` | Admin = `Enum.GetValues<Permission>()` (23); SrMgr 13; Engineer 6; Guest 2 |
| 4 | The TypeScript mirror | `TestController.WebClient/src/lib/capabilities.ts` | Admin **22**; SrMgr 13; Engineer 6; Guest 2 |
| 5 | The WPF UX gate | `TestControllerGrpc/Services/CapabilityChecker.cs` | Independent re-implementation of the assignment rule |

### Where they disagree

| Disagreement | Detail | Consequence |
|---|---|---|
| **TS Administrator is missing `System_ChangeMode`** | C# `AdminPermissions` = `Enum.GetValues` = 23. TS `PERMISSION_CATALOG.Administrator` lists 22 — `System_ChangeMode` is absent. | The web UI hides or disables the mode switch for a genuine Administrator. Since `/api/auth/me` returns the C# list, the UI's own catalog and the server's answer disagree about the same user. Currently masked because `capabilities.test.ts` pins exact counts to the *drifted* numbers. |
| **`CapabilityChecker.Can` vs `AuthorizationService.CanAsync` on `resourceId == null`** | For an Engineer + a pipeline-scoped permission with `resourceId = null`: `CapabilityChecker` skips the assignment branch (`if (resourceId is not null && role == Engineer)`) and returns **true**; `AuthorizationService` falls through `IsPipelineScopedPermission(p) && resourceId is not null` (false) to `Deny("no-role")`. | An **enabled WPF button that fails on click**, for Engineers only. Admin and SrMgr never see it, so it survives manual testing. Recorded in repo memory; still open. |
| **The enum is ordinal, `AuditEntry.ActionName` is a string** | `EnqueueAudit` writes `ActionName = permission.ToString()`. | Fine — string storage means a reorder corrupts live decisions but not history. Worth pinning with a test so nobody "tidies" the enum into explicit values inconsistently. |
| **No catalog covers WatchList mutation or fleet maintenance** | Gaps 1.6 and 1.7 have no permission to reference. | Those endpoints cannot be gated without appending to definition #1 first, and then to #2, #3, #4 and possibly #5 in the same change. |

Effort: **S** for the `System_ChangeMode` drift; **M** for a generated single source of truth.

Test: `PermissionCatalogConsistencyTests` already exists for #2↔#3 — extend it. Add a build step that emits `capabilities.ts` from the C# enum, or a test that reads the `.ts` file and asserts set equality per role (file-analysis, same shape as the existing `Ui/` guards, cannot flake). Add `CapabilityParityTests.CapabilityChecker_Should_MatchAuthorizationService_ForEveryRolePermissionResourceTriple` — a `Theory` over the full cross-product; it red-proves the `resourceId == null` divergence immediately.

---

## 10. Ranked fix plan

Ordered by (risk removed) ÷ (effort), quick wins first, certificate work last. Items in a group can land together.

### Group A — config-only, hours, no code (do these first)

| # | Action | Closes | Effort |
|---|---|---|---|
| A1 | Set `Security:AuthMode` to `Domain` (or `Token`) on **both** hosts + populate `Domain:AdminGroup` / `UserGroup`. | 1.1 — and re-arms 1.9, 5.6, and every ownership check in the codebase | S |
| A2 | Add `Cors:AllowedOrigins` to both appsettings. | 1.4 (which becomes critical the moment A1 lands) | S |
| A3 | Set `Controller:AgentSharedSecret` / `AGENT_SHARED_SECRET` on the controller **and** all 9 agents. Roll controller **after** agents or registration breaks. | 3.1, 3.4, 3.8 | S |
| A4 | Bind the controller's `:5200` REST host to loopback when `Topology = CoLocated`; firewall `:5100` and agent `:5200` to the controller/agent subnet only. | 2.1, and reduces the blast radius of 3.5 from "the network" to "the subnet" | S |
| A5 | Set `RBAC:Enabled = true` on the controller. **Gated on A1** and on a staging pass (this is the long-standing P4-2 item). | 1.2 | S config / M validation |

> Every one of these is an `appsettings.json` edit on a live host, because the deploy procedure excludes `appsettings*.json`. They must be applied by hand, per host, and verified by re-reading the deployed file. Back up first; the deployed configs are hand-tuned and ahead of source.

### Group B — small code changes, high value

| # | Action | Closes | Effort |
|---|---|---|---|
| B1 | `RbacGate`: derive `ClientKind` from the authenticated principal / connection, never from `X-Source`. | **2.3 — a live authorization bypass** | S |
| B2 | `ControllerExecutionForwardingMiddleware` + `ExecutionController`: always overwrite `X-User-Id` from the authenticated principal; treat the inbound header as untrusted. | 2.2, 1.8 | S |
| B3 | `TestControllerGrpcService.ResolveAgentAddress`: accept an advertised endpoint only when its host matches the gRPC peer or an entry in the configured `Agents[]` roster. | **3.3 — credential exfiltration + SSRF** | M |
| B4 | `TestControllerGrpcService.Register`: reject agent names not in the roster. `UnRegister`: require the caller to be that agent. | 3.2, 3.4 | M |
| B5 | `CommandExecutor.StreamOutputAsync`: redact before `record.AddOutputLine`. `_audit.Log("CommandStarted", …)`: pass redacted args. | 5.4, redaction table row 3 | S |
| B6 | `/api/clientlogs`: require auth, cap body size, strip control characters. | 1.10 | S |
| B7 | `ConfigValidator`: make `AuthMode == None`, `RBAC:Enabled == false`, empty `AgentSharedSecret`, `GrpcMode == Plaintext` and "no HTTPS binding" **errors** in non-Development. Add the same validation to the WPF host, which has none. Gate SignalR `EnableDetailedErrors` on environment. | §6 | M |
| B8 | Fix the `System_ChangeMode` TS drift; add the generated-or-asserted catalog parity test and the `CapabilityChecker` cross-product test. | §9 | S |

### Group C — medium, needs design or a new permission

| # | Action | Closes | Effort |
|---|---|---|---|
| C1 | Audit durability: retain a failed batch instead of discarding it; add a synchronous append-only file sink for the critical event set; emit drop counters. | §8 | M |
| C2 | Add `Pipeline_Trigger` audit at the trigger call site, independent of the RBAC decision path, so it is recorded in Default mode too. | §8 row 1 | S |
| C3 | Append `Config_Modify` and `Fleet_Maintain` to the permission enum (+ all 5 catalogs); gate `WatchListEndpoints` mutations and `MaintenanceController` POSTs. | 1.6, 1.7 | M |
| C4 | Gate `ExecutionEndpoints` (`trigger-all`, `trigger-event`, `retry-tag`, `preflight`) with the same permissions the controller enforces, or route them through the forwarding middleware. | **1.5 — a live authorization bypass** | M |
| C5 | SignalR segmentation: publish to `session:{id}` / pipeline groups instead of `global`, **and** authorize `JoinSession` / `JoinAsUser` / `JoinAllSessions` / `JoinRun` in the same change. Either half alone is ineffective. | 5.1, 5.2, 5.3 | M |
| C6 | Agent: register an auth interceptor on `TestAgentService` (reuse `AgentAuthInterceptor`) and attach `x-agent-token` in `AgentGrpcDispatcher`'s channel factory. Ship the agent side first, then the controller, per the established rollout-ordering rule. | **3.5 — unauthenticated RCE**, 3.7 | M |
| C7 | Command policy: fail startup when `Mode == Enforce` and the policy file is missing; ship `commandpolicy.json` in the agent publish output; soak in `AuditOnly`, then flip to `Enforce`. | 4.1, 4.2 | M |
| C8 | Set an explicit `WorkingDirectory` (a dedicated scratch root) for every execution, not just credentialed ones. | 4.4 | S |
| C9 | Extend `TriggerParameterValidator` to file-sourced parameter values behind a flag; add a separator guard in `ResolveInterpreter`. | 4.3 | M |
| C10 | Survey every node's agent service account; report it in `GetAgentSnapshot`; standardise. | 4.5 | M |
| C11 | Move `_VCloudPassword` out of `pipeline-config.json` into a machine env var resolved at dispatch time, so it is never at rest in a file and never in a stored parameter set. | §7 | M |
| C12 | Harden `SecurityRedactor` with format/entropy detectors (PAT, JWT, long base64). Treat as defence-in-depth, not as the control. | 5.5 | M |

### Group D — TLS / certificates (last, longest lead time)

| # | Action | Closes | Effort |
|---|---|---|---|
| D1 | Obtain a Web Server certificate (EKU 1.3.6.1.5.5.7.3.1) with SAN = `JVGR22.magellandev2000.dev.wonderware.com`. Blocked on the PKI team — no usable cert exists on the box today and `certutil -ADCA` finds no reachable enterprise CA. Bind on **444** (443 is held by another app). | 1.3, and unblocks delegated Entra sign-in | L |
| D2 | `UseHttpsRedirection` + unconditional `UseHsts` in non-Development. | 1.3 | S (after D1) |
| D3 | gRPC TLS: issue agent certs via `deploy/New-GrpcCertificates.ps1`, set `AgentKestrel:EnableTls = true` + `Security:Transport:GrpcMode`, pin `TrustedCaThumbprint`. | 3.6, and encrypts 4.6 | L |
| D4 | Mutual TLS: `RequireClientCertificate = true` on agents, client cert on the controller. Replaces the shared secret with per-node identity and makes 3.2/3.3 structurally impossible rather than validated. | 3.2, 3.3 permanently | L |

---

## 11. What blocks production

Treating "production" as the current deployment on JVGR22 reachable from the corporate network:

**Hard blockers — the system is currently open:**

1. **1.1** — authentication is off on both hosts; every caller is Administrator.
2. **3.5** — unauthenticated `RunCommand` on every agent's `:5200`. Arbitrary code execution as the agent service account (LocalSystem on most nodes) from anywhere the network reaches.
3. **3.1 + 3.3** — unauthenticated agent registration with an attacker-chosen callback address. Redirects dispatch and exfiltrates the substituted `_VCloudPassword`.
4. **2.3** — `X-Source: WPF` defeats the Default-mode read-only guarantee with a single header. This one is a bypass *within* the security model, so it survives fixing 1.1.
5. **1.5** — `trigger-event` / `trigger-all` bypass the `Pipeline_Trigger` gate the controller enforces on the sibling route.

**Blocks "we can claim RBAC works":**

6. **1.2 / A5** — RBAC is disabled, so every permission check in the codebase is inert.
7. **1.6 / 1.7** — WatchList and fleet-maintenance mutations have no permission to check even once RBAC is on.
8. **§8 row 1** — pipeline trigger produces no audit record at all. An access-control claim with no record of the controlled action is not auditable.

**Blocks "we can claim data is confidential":**

9. **1.3 / 3.6** — everything is plaintext.
10. **5.1** — all live output goes to every connected client.

**Not blocking, fix on the normal cycle:** 1.10, 4.4, 5.3, 5.5, §9 drift, SMTP.

---

## 12. Decisions needed from you

| # | Decision | Why it cannot be made from the code | Options |
|---|---|---|---|
| **D-1** | **Enterprise identity.** Which `AuthMode` replaces `None`? | `Domain` needs the real AD group names (config currently holds placeholders `XYZ\TestAdmins` / `XYZ\TestUsers`). `Token` needs a provisioning and rotation owner. The RBAC session model is a third, independent identity layer that already coexists with these. | (a) `Domain` + Negotiate, mapping AD groups → Admin/User, with RBAC roles on top — lowest friction, matches the existing `AddMultiIdentitySecurity` design. (b) `Token` + `DpapiTokenStore` — works for service-to-service, poor UX for humans. (c) Entra/OIDC — biggest change, but the delegated-ADO work already points that way and it is the only option that satisfies the tenant's Conditional Access policy. |
| **D-2** | **Certificate enrollment method** for HTTPS (D1) and gRPC/mTLS (D3/D4). | No enterprise CA is reachable from JVGR22 and every existing Server-Auth cert is for the old machine name `JVGR2`. Self-signed works for gRPC (thumbprint pinning is already implemented in `GrpcTlsChannelFactory`) but not for browsers. | (a) PKI team issues a Web Server cert for the FQDN (browser-trusted) **and** you self-sign the gRPC fleet with `New-GrpcCertificates.ps1` + thumbprint pinning — pragmatic split, unblocks D1 without waiting for 10 more certs. (b) PKI for everything — cleanest, slowest, needs a renewal process for 10 machines. (c) An internal CA you run for the fleet, PKI only for the browser edge. Also: confirm port **444** is acceptable. |
| **D-3** | **Audit-outage behaviour.** What should happen when the audit store is unavailable? | This is a policy call, not an engineering one: it trades availability for accountability, and the correct answer differs per event class. | (a) *Best-effort* (today): drop and log. Cheapest, but a mode change can vanish. (b) *Durable buffer*: append-only file sink, replay into SQLite when it recovers. Recommended default. (c) *Fail-closed for critical events only*: trigger / mode change / privilege change / force release refuse to execute if their audit write cannot be durably recorded; everything else stays best-effort. Strongest, and it will occasionally stop a pipeline — I need you to accept that trade explicitly before I build it. |
| **D-4** | **Command policy enforcement.** Flip agents from `AuditOnly` to `Enforce`? | Nobody knows what the current pipelines actually execute; `AllowedCommandPrefixes` is empty and `commandpolicy.json` may not even be deployed. Flipping blind will break production runs. | (a) Soak in `AuditOnly` with violation reporting for N weeks, build the allowlist from observed traffic, then flip. Recommended — tell me the soak window. (b) Flip now with a permissive allowlist. (c) Leave advisory and rely on C6 (agent authentication) as the real control. |
| **D-5** | **WatchList mutation authority.** Who may edit `WatchList.xml` through the API? | Editing it is strictly more powerful than triggering, yet today it needs less. There is no permission for it, so this decides an append to the enum. | (a) Administrators only. (b) A new `Config_Modify` held by Admin + SrMgr. (c) Remove the API mutations entirely and make WatchList.xml a file-on-disk change gated by filesystem ACLs and the existing hot-reload watcher. |
| **D-6** | **Live-data segmentation model.** Who may see which pipeline's output? | `SignalRNotifier` broadcasts everything to `global`. Segmenting requires a rule, and the natural one (Engineers see only assigned pipelines) may not match how the team actually works. | (a) Everyone authenticated sees everything (status quo, made explicit) — cheapest, and honest. (b) Per-pipeline, using the existing `PipelineAssignments`. (c) Per-session ownership + elevated roles see all. |
| **D-7** | **Rollout order and downtime.** | A3 and C6 change the agent wire contract; the repo's established rule is agents last for payload changes but **agents first** for an auth handshake, or dispatch breaks mid-flight. The controller is a manually launched desktop app with no scheduled task. | Confirm a maintenance window with zero active sessions, and who will be on JVGR22 to restart `TestControllerGrpc.exe`. |

---

### Pause

This is the assessment and the ranked plan. **No code has been changed.** Tell me which decisions above you want to settle and which group you want implemented first — my recommendation is A1–A4 and B1–B3 in one pass, because they remove the two live bypasses (2.3, 3.3) and the unauthenticated-Admin problem without touching certificates or the wire contract.
