# Fix Spec: Agent Registration Fails - Missing IAppLogger DI Registration

| Field | Value |
|---|---|
| **Problem** | All agents stuck `NeverRegistered`; every `Register` gRPC call fails |
| **Root cause** | `IAppLogger` is not registered in the controller's DI container |
| **Component** | TestControllerGrpc (controller) |
| **Severity** | P0 - entire fleet cannot register |
| **Fix size** | One registration line (plus verification) |

---

## Instructions for Copilot

This is a targeted dependency-injection fix. The controller's gRPC service
cannot be constructed because one of its constructor dependencies (`IAppLogger`)
is not registered in the DI container. Your job:

1. Find the concrete class that implements `IAppLogger`
2. Find where the controller configures its DI services
3. Add the missing registration with the correct lifetime
4. Verify no other dependencies of the gRPC service are also unregistered

Read the actual code before editing. Do not guess the implementation class name
or the lifetime - determine both from the code.

---

## The Exact Error

From the controller's Windows Event Viewer, repeating for every agent's
registration attempt:

```
Category: Grpc.AspNetCore.Server.ServerCallHandler  EventId: 6
RequestPath: /testagent.TestControllerService/Register
Error when executing service method 'Register'.

System.InvalidOperationException: Unable to resolve service for type
'TestControllerGrpc.Services.IAppLogger' while attempting to activate
'TestControllerGrpc.Services.TestControllerGrpcService'.
   at Microsoft.Extensions.DependencyInjection.ActivatorUtilities.ThrowHelperUnableToResolveService(Type type, Type requiredBy)
   at Grpc.AspNetCore.Server.Internal.DefaultGrpcServiceActivator`1.Create(IServiceProvider serviceProvider)
   at Grpc.Shared.Server.UnaryServerMethodInvoker`3.Invoke(...)
```

### What this means

`TestControllerGrpcService` has a constructor parameter of type `IAppLogger`.
When an agent calls `Register`, ASP.NET Core tries to activate a
`TestControllerGrpcService` instance and inject its dependencies from the DI
container. `IAppLogger` is not registered, so activation throws, the handler
never runs, and every agent fails to register.

Because EVERY `Register` call needs this service, and the service can never be
built, the ENTIRE fleet is stuck `NeverRegistered`. This is not a network,
proto, or framing problem - it is a missing DI registration.

---

## Step 1: Find the IAppLogger implementation

Search the codebase for the concrete class implementing `IAppLogger`:

```
: IAppLogger
```

or

```
class AppLogger
```

Likely locations:
- `TestControllerGrpc/Services/` (same folder as the interface)
- `TestControllerGrpc.Core/` (if logging is shared)

Report:
- The interface's full definition and namespace
- The concrete implementation class name and namespace
- The implementation's OWN constructor dependencies (you need these for Step 4)

If there are MULTIPLE implementations, determine which one the controller is
meant to use (check other projects/hosts for how they register it - the WPF
host or WebApi host may already register it correctly, and you can mirror that).

---

## Step 2: Find the controller's DI setup

Locate where the controller registers services. Look for:
- `builder.Services.AddGrpc(...)`
- `builder.Services.AddSingleton/AddScoped/AddTransient(...)`
- The registration of `TestControllerGrpcService` itself

Likely files:
- `TestControllerGrpc/Program.cs`
- `TestControllerGrpc/Services/ControllerGrpcServerHost.cs` (referenced in prior
  stack traces at line ~82 - this configures the gRPC server)
- Any `*Startup.cs` or DI extension method

Report the current registration block so you can add to it in the right place.

---

## Step 3: Add the missing registration

Add the `IAppLogger` registration alongside the other service registrations,
BEFORE `builder.Build()` / before the host is built.

Use the concrete class you found in Step 1:

```csharp
// Register the application logger so TestControllerGrpcService can be activated.
builder.Services.AddSingleton<IAppLogger, AppLogger>();
```

Replace `AppLogger` with the REAL implementation class name from Step 1.

### Choosing the correct lifetime

Determine the lifetime from the implementation's dependencies (Step 1):

| Lifetime | Use when |
|---|---|
| `AddSingleton<IAppLogger, AppLogger>()` | Logger is stateless / shared - DEFAULT, usually correct |
| `AddScoped<IAppLogger, AppLogger>()` | Logger needs per-request scope (e.g. per-call correlation id) or depends on a scoped service like `IHttpContextAccessor` |
| `AddTransient<IAppLogger, AppLogger>()` | A fresh instance is required each activation (rare for a logger) |

IMPORTANT lifetime rule: a service cannot depend on a shorter-lived service.
- If `TestControllerGrpcService` is registered/activated per-call (scoped-like,
  which gRPC services are by default) it CAN consume a singleton or scoped
  `IAppLogger`.
- But if you make `IAppLogger` a singleton and its implementation depends on a
  SCOPED service (like `IHttpContextAccessor` or a scoped DbContext), that will
  throw a DIFFERENT lifetime error at startup. In that case, register it as
  `AddScoped` instead.

So: check the implementation's constructor. If it only needs
singleton/stateless things, use `AddSingleton`. If it needs anything scoped, use
`AddScoped`.

### If IAppLogger wraps ILogger<T>

If `AppLogger` is a thin wrapper over the built-in `ILogger`, it may need
`ILogger<AppLogger>` or `ILoggerFactory` injected - those are already registered
by the framework, so `AddSingleton<IAppLogger, AppLogger>()` will work as long
as the wrapper takes `ILoggerFactory` (singleton) rather than a scoped
dependency.

---

## Step 4: Verify no OTHER dependencies are unregistered

The DI error surfaced `IAppLogger` first, but a constructor can have multiple
unregistered dependencies - the container throws on the first missing one and
stops. After adding `IAppLogger`, check the FULL constructor of
`TestControllerGrpcService`:

1. Open `TestControllerGrpc/Services/TestControllerGrpcService.cs`
2. Read its constructor - list EVERY parameter type
3. For each parameter type, confirm there is a matching registration in the DI
   setup (Step 2) OR it is a framework-provided type (`ILogger<T>`,
   `IConfiguration`, `IHostApplicationLifetime`, etc.)
4. Register any that are missing, same as `IAppLogger`

This prevents a whack-a-mole where you fix `IAppLogger`, rebuild, and hit the
next missing dependency immediately.

Report the full constructor and confirm each parameter resolves.

---

## Step 5: Why this regressed (find it, so it does not recur)

This error appears the moment `TestControllerGrpcService` gained an `IAppLogger`
constructor parameter WITHOUT a matching registration - or the registration was
removed in a refactor. This is a `Feature` branch under active development
(`C:\Projects\Feature\TestAgentSolution\`), so it is almost certainly a recent
change where the constructor and the DI registration got out of sync.

Check:
- Recent changes to `TestControllerGrpcService`'s constructor (did `IAppLogger`
  get added recently?)
- Recent changes to the DI setup (was an `IAppLogger` registration removed?)

Confirm the fix restores the constructor/registration pairing so it does not
regress on the next change.

---

## Deliverables

1. The concrete `IAppLogger` implementation class name + namespace
2. The exact registration line added, with the chosen lifetime and WHY
3. The full `TestControllerGrpcService` constructor, with every dependency
   confirmed to resolve (Step 4)
4. Confirmation the controller builds

---

## Verification After the Fix

1. Rebuild and redeploy the controller (TestControllerGrpc).
2. Restart the controller service.
3. Watch an agent (e.g. jvgr1):
   - Status should flip from `NeverRegistered` to `Registered` within ~15s
   - Heartbeats should start succeeding (`X OK` climbing, not all Failed)
4. Confirm the controller Event Viewer no longer logs the
   "Unable to resolve service for type 'IAppLogger'" error on `Register`.

### PowerShell verification (run on the controller)

```powershell
# Confirm no more IAppLogger DI errors appear after restart
Get-WinEvent -LogName Application -MaxEvents 200 |
    Where-Object { $_.Message -match "Unable to resolve service.*IAppLogger" } |
    Select-Object TimeCreated, @{N='Msg';E={$_.Message.Substring(0,120)}} |
    Format-Table -AutoSize
# Expect: no rows with a TimeCreated AFTER the restart
```

---

## Important: This Is Separate From the "Compressed Flag" Framing Error

Do NOT conflate this with the other known issue. There are TWO distinct problems:

| Problem | RequestPath | Exception | Fix |
|---|---|---|---|
| THIS spec | `/Register` | `Unable to resolve service ... IAppLogger` | Register `IAppLogger` in DI |
| Separate issue | `/PushExecutionEvents` | `Unexpected compressed flag value in message header` | Serialize concurrent stream writes (different spec) |

Fix THIS one first - it is what blocks registration right now, and it is a
one-line change. The `PushExecutionEvents` framing error is a separate concern
that may or may not still appear once agents can register and start streaming;
handle it with its own spec after registration is restored.

---

## Summary of the reasoning

1. Error is `InvalidOperationException: Unable to resolve service for type
   IAppLogger` while activating `TestControllerGrpcService`.
2. That means `IAppLogger` is a constructor dependency that is not registered in
   DI.
3. gRPC activates the service per call; with the dependency missing, EVERY
   `Register` call fails -> whole fleet `NeverRegistered`.
4. Fix: register `IAppLogger` -> its implementation with the correct lifetime.
5. Then verify the rest of the constructor resolves, so no next-missing-dep
   surprise.

Start with Step 1: find the `IAppLogger` implementation and show me the
controller's current DI registration block.
