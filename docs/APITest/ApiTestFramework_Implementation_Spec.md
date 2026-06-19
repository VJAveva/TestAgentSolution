# API Workflow Test Framework — Implementation Specification

| Field | Value |
|---|---|
| **Project** | TestController.ApiTests |
| **Purpose** | End-to-end functional tests for the TestController API (trigger → execute → results) |
| **Framework** | xUnit + WebApplicationFactory + FluentAssertions |
| **Runs in** | Two modes: InMemory (fast, CI) and Live (real fleet) |
| **Target** | .NET 10 |

---

## Instructions for Copilot

You are implementing a complete API test framework inside the TestAgentSolution.
Work through the phases in order. For each file, I provide the full intent and
the code skeleton. Your job:

1. Create each file as specified
2. Wherever you see `===== PLUG IN =====`, replace the placeholder with the
   REAL value from this codebase — actual route strings, DTO property names,
   the real agent dispatcher interface, real WatchItem tags and agent names.
   Inspect the actual WebApi controllers and Core services to find these.
3. After creating all files, ensure the project compiles
4. Get ONE test green first (the happy-path workflow), then expand
5. Be opinionated — if my skeleton conflicts with the real codebase, adapt it
   and tell me what you changed and why

Do NOT invent endpoints or DTOs. Read the real `TestController.WebApi`
controllers and `TestControllerGrpc.Core` services to discover the true
contract, then align the test code to it.

---

## Background: The Application Under Test

TestAgentSolution is a distributed test execution platform:

- **TestControllerGrpc.Core** — shared services (session manager, agent registry, dispatcher)
- **TestController.WebApi** — ASP.NET Core REST API (the system under test)
- **TestController.Api** — MVC + SignalR hub
- **TestControllerGrpc** — WPF Controller UI
- **TestAgentGrpc** — agent running on each test VM (gRPC, port 5200)

The core workflow we are testing:

```
1. Client POSTs a trigger request to the WebApi
2. WebApi creates an ExecutionSession (ConcurrentDictionary-backed)
3. Controller dispatches commands to agents over gRPC
4. Agents execute, report progress/results back
5. Session aggregates results (TRX files)
6. Client polls session status, then fetches results
```

Key services to locate in the real code:

- `IAgentGrpcDispatcher` (or similar) — the abstraction the controller uses to
  talk to agents. **This is the seam we replace with a fake in InMemory mode.**
- `ExecutionSessionManager` — tracks active sessions
- `AgentRegistry` — registered agents
- The WebApi controllers — discover the real routes here

---

## The Two-Mode Strategy (most important concept)

The framework runs the SAME tests two ways:

| Mode | Host | Agents | Use | Speed |
|---|---|---|---|---|
| **InMemory** | `WebApplicationFactory` hosts WebApi in-process | Faked via `FakeAgentDispatcher` | Local dev, every-build CI | ms |
| **Live** | Real HTTP to running controller | Real agents execute | Nightly, pre-release | minutes |

Switch via the `API_TEST_MODE` environment variable. No code changes between modes.

This matters because:
- InMemory tests validate **workflow logic** fast, with no infrastructure
- Live tests validate the **full real stack** against actual agents
- Writing tests once and running both ways doubles their value

---

## Phase 0: Project Setup

### File: `TestController.ApiTests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageReference Include="FluentAssertions" Version="6.12.1" />
    <PackageReference Include="Xunit.SkippableFact" Version="1.4.13" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <!-- ===== PLUG IN: path to your real WebApi project ===== -->
    <ProjectReference Include="..\TestController.WebApi\TestController.WebApi.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.test.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

### Prerequisite: make `Program` visible

The WebApi's `Program` type must be public for `WebApplicationFactory<Program>`.
If the WebApi uses top-level statements, add at the END of its `Program.cs`:

```csharp
public partial class Program { }
```

### File: `appsettings.test.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning"
    }
  }
}
```

---

## Phase 1: Infrastructure

### File: `Infrastructure/TestConfig.cs`

Reads configuration from environment variables so the same tests run locally
and in CI without code changes.

```csharp
namespace TestController.ApiTests.Infrastructure;

public enum TestMode
{
    InMemory,
    Live
}

public static class TestConfig
{
    public static TestMode Mode =>
        string.Equals(
            Environment.GetEnvironmentVariable("API_TEST_MODE"),
            "Live",
            StringComparison.OrdinalIgnoreCase)
            ? TestMode.Live
            : TestMode.InMemory;

    public static string LiveBaseUrl =>
        Environment.GetEnvironmentVariable("API_TEST_URL")
        // ===== PLUG IN: your real controller URL =====
        ?? "https://rcloud.dev.wonderware.com";

    public static TimeSpan WorkflowTimeout
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("API_TEST_TIMEOUT_SECONDS");
            return int.TryParse(raw, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(120);
        }
    }

    public static TimeSpan PollInterval => TimeSpan.FromSeconds(1);
}
```

### File: `Infrastructure/Contracts.cs`

DTOs the tests use. Defined separately from production DTOs so a property
rename forces a conscious test update (validates the contract as a consumer
sees it).

```csharp
namespace TestController.ApiTests.Infrastructure;

// ===== PLUG IN: align ALL property names with your real JSON contract =====
// Inspect your WebApi controllers / swagger to find the true shapes.

public sealed record TriggerRequest
{
    public required string WatchItemTag { get; init; }
    public required string BuildNumber { get; init; }
    public string[]? Agents { get; init; }
    public string? TestSet { get; init; }
}

public sealed record SessionResponse
{
    public required string SessionId { get; init; }
    public required string State { get; init; }
}

public sealed record SessionStatus
{
    public required string SessionId { get; init; }
    public required string State { get; init; }
    public int TotalActions { get; init; }
    public int CompletedActions { get; init; }
    public double ProgressPercent { get; init; }
    public AgentProgress[]? Agents { get; init; }
}

public sealed record AgentProgress
{
    public required string AgentName { get; init; }
    public required string State { get; init; }
    public int CompletedActions { get; init; }
    public int TotalActions { get; init; }
}

public sealed record ResultsSummary
{
    public required string SessionId { get; init; }
    public int TotalActions { get; init; }
    public int CompletedActions { get; init; }
    public int PassedTests { get; init; }
    public int FailedTests { get; init; }
    public int SkippedTests { get; init; }
}

public static class SessionStates
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static readonly string[] Terminal =
        { Completed, Failed, Cancelled };

    public static bool IsTerminal(string state) =>
        Terminal.Contains(state, StringComparer.OrdinalIgnoreCase);
}
```

### File: `Infrastructure/ApiClient.cs`

Typed wrapper over HttpClient. ALL endpoint routes live HERE — when a route
changes, you fix one file, not fifty tests.

```csharp
using System.Net.Http.Json;

namespace TestController.ApiTests.Infrastructure;

public sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(HttpClient http) => _http = http;

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            // ===== PLUG IN: your health route =====
            var resp = await _http.GetAsync("/api/health");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<SessionResponse> TriggerPipelineAsync(TriggerRequest request)
    {
        // ===== PLUG IN: your trigger route =====
        var resp = await _http.PostAsJsonAsync("/api/execution/trigger", request);
        await EnsureSuccess(resp, "trigger pipeline");
        return (await resp.Content.ReadFromJsonAsync<SessionResponse>())!;
    }

    public async Task<SessionStatus> GetSessionStatusAsync(string sessionId)
    {
        // ===== PLUG IN: your session-status route =====
        return (await _http.GetFromJsonAsync<SessionStatus>(
            $"/api/execution/sessions/{sessionId}"))!;
    }

    public async Task<ResultsSummary> GetResultsAsync(string sessionId)
    {
        // ===== PLUG IN: your results route =====
        return (await _http.GetFromJsonAsync<ResultsSummary>(
            $"/api/results/{sessionId}"))!;
    }

    public async Task CancelSessionAsync(string sessionId)
    {
        // ===== PLUG IN: your cancel route =====
        var resp = await _http.PostAsync(
            $"/api/execution/sessions/{sessionId}/cancel", content: null);
        await EnsureSuccess(resp, "cancel session");
    }

    public async Task<IReadOnlyList<string>> GetRegisteredAgentsAsync()
    {
        // ===== PLUG IN: your agent-registry route + shape =====
        var agents = await _http.GetFromJsonAsync<string[]>("/api/agents");
        return agents ?? Array.Empty<string>();
    }

    private static async Task EnsureSuccess(HttpResponseMessage resp, string action)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"Failed to {action}: {(int)resp.StatusCode} {resp.ReasonPhrase}. " +
            $"Body: {Truncate(body, 500)}");
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
```

### File: `Infrastructure/FakeAgentDispatcher.cs`

The seam that makes InMemory mode work. Replaces the real gRPC agent transport
so workflow logic runs for real while command execution is simulated. Lets you
inject failures to test recovery without breaking real VMs.

```csharp
namespace TestController.ApiTests.Infrastructure;

public sealed class FakeAgentBehavior
{
    public TimeSpan ExecutionDelay { get; set; } = TimeSpan.FromMilliseconds(50);
    public string? FailCommandsContaining { get; set; }
    public bool SimulateUnreachable { get; set; }
    public int PassCount { get; set; } = 10;
    public int FailCount { get; set; } = 0;
    public int SkipCount { get; set; } = 0;
}

// ===== PLUG IN: implement YOUR real dispatcher interface =====
// Find the interface your controller uses to call agents (likely
// IAgentGrpcDispatcher in TestControllerGrpc.Core). Implement it here.
// Adapt the method signatures and types to match the real interface.

/*
using TestControllerGrpc.Core.Services;   // your namespace
using TestControllerGrpc.Core.Models;     // your command/result types
using Grpc.Core;

public sealed class FakeAgentDispatcher : IAgentGrpcDispatcher
{
    private readonly FakeAgentBehavior _behavior;

    public FakeAgentDispatcher(FakeAgentBehavior behavior) => _behavior = behavior;

    public async Task<CommandResult> DispatchAsync(
        string agentName, CommandRequest command, CancellationToken ct)
    {
        if (_behavior.SimulateUnreachable)
            throw new RpcException(new Status(
                StatusCode.Unavailable,
                $"Simulated: agent {agentName} unreachable"));

        await Task.Delay(_behavior.ExecutionDelay, ct);

        if (_behavior.FailCommandsContaining is { } needle
            && command.Command.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return new CommandResult
            {
                ExitCode = 1,
                Error = $"Simulated failure for command containing '{needle}'"
            };
        }

        return new CommandResult { ExitCode = 0, Output = "Simulated success" };
    }

    // ... implement any other interface members ...
}
*/
```

### File: `Infrastructure/ApiTestFixture.cs`

The engine. Builds either an in-memory host or a live HTTP client based on mode.

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace TestController.ApiTests.Infrastructure;

public class ApiTestFixture : IAsyncLifetime
{
    public ApiClient Client { get; private set; } = default!;
    public FakeAgentBehavior AgentBehavior { get; } = new();

    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _http;

    public Task InitializeAsync()
    {
        _http = TestConfig.Mode == TestMode.InMemory
            ? CreateInMemoryClient()
            : CreateLiveClient();
        Client = new ApiClient(_http);
        return Task.CompletedTask;
    }

    private HttpClient CreateInMemoryClient()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    // ===== PLUG IN: swap the real agent transport for the fake =====
                    // services.RemoveAll<IAgentGrpcDispatcher>();
                    // services.AddSingleton(AgentBehavior);
                    // services.AddSingleton<IAgentGrpcDispatcher, FakeAgentDispatcher>();
                    //
                    // Also stub other outbound deps if needed (file shares,
                    // vCloud/PowerCLI, email).
                });
            });
        return _factory.CreateClient();
    }

    private static HttpClient CreateLiveClient() =>
        new() {
            BaseAddress = new Uri(TestConfig.LiveBaseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };

    public Task DisposeAsync()
    {
        _http?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }
}
```

### File: `Infrastructure/WorkflowHelpers.cs`

Poll-until-done helpers (never `Thread.Sleep`) and domain assertions.

```csharp
using FluentAssertions;

namespace TestController.ApiTests.Infrastructure;

public static class WorkflowHelpers
{
    public static async Task<SessionStatus> PollUntilTerminalAsync(
        this ApiClient api, string sessionId,
        TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TestConfig.WorkflowTimeout);
        var interval = pollInterval ?? TestConfig.PollInterval;
        SessionStatus? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = await api.GetSessionStatusAsync(sessionId);
            if (SessionStates.IsTerminal(last.State)) return last;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"Session '{sessionId}' did not reach terminal state within " +
            $"{(timeout ?? TestConfig.WorkflowTimeout).TotalSeconds:0}s. " +
            $"Last state: '{last?.State ?? "unknown"}' " +
            $"({last?.CompletedActions ?? 0}/{last?.TotalActions ?? 0}).");
    }

    public static async Task<SessionStatus> PollUntilProgressAsync(
        this ApiClient api, string sessionId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TestConfig.WorkflowTimeout);
        SessionStatus? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await api.GetSessionStatusAsync(sessionId);
            if (last.ProgressPercent > 0 || SessionStates.IsTerminal(last.State))
                return last;
            await Task.Delay(TestConfig.PollInterval);
        }
        throw new TimeoutException(
            $"Session '{sessionId}' never reported progress > 0%.");
    }
}

public static class WorkflowAssertions
{
    public static void ShouldHaveCompletedSuccessfully(this SessionStatus status)
    {
        status.State.Should().Be(SessionStates.Completed);
        status.CompletedActions.Should().Be(status.TotalActions);
    }

    public static void ShouldHaveFailed(this SessionStatus status) =>
        status.State.Should().Be(SessionStates.Failed);

    public static void ShouldHaveProducedResults(this ResultsSummary results) =>
        results.TotalActions.Should().BeGreaterThan(0);
}
```

---

## Phase 2: Test Data Builders

### File: `Data/TestWatchItems.cs`

```csharp
using TestController.ApiTests.Infrastructure;

namespace TestController.ApiTests.Data;

// ===== PLUG IN: use WatchItem tags and agent names that exist in your env =====
public static class TestWatchItems
{
    public static TriggerRequest SimplePipeline(
        string agent = "jvgr1", string build = "OAK_main_test.1") =>
        new()
        {
            WatchItemTag = "SmokeTest_Single",
            BuildNumber = build,
            Agents = new[] { agent },
            TestSet = "Set1"
        };

    public static TriggerRequest MultiAgentPipeline(
        string[]? agents = null, string build = "OAK_main_test.1") =>
        new()
        {
            WatchItemTag = "SmokeTest_Fleet",
            BuildNumber = build,
            Agents = agents ?? new[] { "jvgr1", "jvkpri", "jvkbak" },
            TestSet = "All"
        };

    public static TriggerRequest FailingInstallPipeline(
        string agent = "jvhist", string build = "OAK_main_test.1") =>
        new()
        {
            WatchItemTag = "SmokeTest_Single",
            BuildNumber = build,
            Agents = new[] { agent },
            TestSet = "Set1"
        };
}
```

---

## Phase 3: Core Workflow Tests

### File: `Workflows/TriggerExecuteResultsTests.cs`

The canonical trigger → execute → results test, plus regression guards for
known bugs (progress stuck at 0%).

```csharp
using FluentAssertions;
using TestController.ApiTests.Data;
using TestController.ApiTests.Infrastructure;
using Xunit;

namespace TestController.ApiTests.Workflows;

public class TriggerExecuteResultsTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture _fixture;
    private readonly ApiClient _api;

    public TriggerExecuteResultsTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
        _api = fixture.Client;
    }

    [Fact]
    public async Task Trigger_Execute_Results_HappyPath()
    {
        var request = TestWatchItems.SimplePipeline(agent: "jvgr1");

        var session = await _api.TriggerPipelineAsync(request);
        session.SessionId.Should().NotBeNullOrEmpty();
        session.State.Should().Be(SessionStates.Running);

        var final = await _api.PollUntilTerminalAsync(session.SessionId);
        final.ShouldHaveCompletedSuccessfully();

        var results = await _api.GetResultsAsync(session.SessionId);
        results.ShouldHaveProducedResults();
        results.CompletedActions.Should().Be(results.TotalActions);
    }

    [Fact]
    public async Task Trigger_ReportsProgress_NotStuckAtZero()
    {
        // Regression guard: "Active Session stuck at 0%" bug
        var request = TestWatchItems.SimplePipeline(agent: "jvgr1");
        var session = await _api.TriggerPipelineAsync(request);

        var observed = await _api.PollUntilProgressAsync(session.SessionId);
        observed.ProgressPercent.Should().BeGreaterThan(0);

        await _api.PollUntilTerminalAsync(session.SessionId);
    }

    [Fact]
    public async Task Trigger_FinalProgress_ReachesHundredPercent()
    {
        var request = TestWatchItems.SimplePipeline(agent: "jvgr1");
        var session = await _api.TriggerPipelineAsync(request);
        var final = await _api.PollUntilTerminalAsync(session.SessionId);

        if (final.State == SessionStates.Completed)
            final.ProgressPercent.Should().BeApproximately(100, 0.01);
    }
}
```

### File: `Workflows/ConcurrentExecutionTests.cs`

```csharp
using FluentAssertions;
using TestController.ApiTests.Data;
using TestController.ApiTests.Infrastructure;
using Xunit;

namespace TestController.ApiTests.Workflows;

public class ConcurrentExecutionTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiClient _api;
    public ConcurrentExecutionTests(ApiTestFixture fixture) => _api = fixture.Client;

    [Fact]
    public async Task TwoSessions_RunConcurrently_BothComplete()
    {
        var requestA = TestWatchItems.SimplePipeline(agent: "jvgr1");
        var requestB = TestWatchItems.SimplePipeline(agent: "jvkpri");

        var sessionA = await _api.TriggerPipelineAsync(requestA);
        var sessionB = await _api.TriggerPipelineAsync(requestB);

        sessionA.SessionId.Should().NotBe(sessionB.SessionId);

        var finalA = _api.PollUntilTerminalAsync(sessionA.SessionId);
        var finalB = _api.PollUntilTerminalAsync(sessionB.SessionId);
        await Task.WhenAll(finalA, finalB);

        SessionStates.IsTerminal((await finalA).State).Should().BeTrue();
        SessionStates.IsTerminal((await finalB).State).Should().BeTrue();
    }

    [Fact]
    public async Task MultiAgentPipeline_AllAgentsReport()
    {
        var request = TestWatchItems.MultiAgentPipeline(
            agents: new[] { "jvgr1", "jvkpri", "jvkbak" });

        var session = await _api.TriggerPipelineAsync(request);
        var final = await _api.PollUntilTerminalAsync(session.SessionId);

        final.Agents.Should().NotBeNull();
        final.Agents!.Should().HaveCount(3);
    }
}

public class CancellationTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiClient _api;
    public CancellationTests(ApiTestFixture fixture) => _api = fixture.Client;

    [Fact]
    public async Task Cancel_RunningSession_ReachesCancelledState()
    {
        var request = TestWatchItems.MultiAgentPipeline();
        var session = await _api.TriggerPipelineAsync(request);

        await _api.CancelSessionAsync(session.SessionId);

        var final = await _api.PollUntilTerminalAsync(session.SessionId);
        final.State.Should().Be(SessionStates.Cancelled);
    }
}
```

### File: `Workflows/FailureRecoveryTests.cs`

Failure injection tests — InMemory mode only (can't force a real agent to fail).

```csharp
using FluentAssertions;
using TestController.ApiTests.Data;
using TestController.ApiTests.Infrastructure;
using Xunit;

namespace TestController.ApiTests.Workflows;

public class FailureRecoveryTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture _fixture;
    private readonly ApiClient _api;

    public FailureRecoveryTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
        _api = fixture.Client;
    }

    [SkippableFact]
    public async Task FailedInstall_SurfacesAsFailure_DoesNotHang()
    {
        Skip.If(TestConfig.Mode == TestMode.Live,
            "Failure injection requires the fake agent (InMemory mode).");

        _fixture.AgentBehavior.FailCommandsContaining = "Install-Build";
        var request = TestWatchItems.FailingInstallPipeline();

        var session = await _api.TriggerPipelineAsync(request);
        var final = await _api.PollUntilTerminalAsync(
            session.SessionId, timeout: TimeSpan.FromSeconds(30));

        SessionStates.IsTerminal(final.State).Should().BeTrue();

        var results = await _api.GetResultsAsync(session.SessionId);
        results.FailedTests.Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task UnreachableAgent_DoesNotHangForever()
    {
        Skip.If(TestConfig.Mode == TestMode.Live,
            "Unreachable simulation requires the fake agent (InMemory mode).");

        _fixture.AgentBehavior.SimulateUnreachable = true;
        var request = TestWatchItems.SimplePipeline(agent: "jvhist");

        var session = await _api.TriggerPipelineAsync(request);
        var final = await _api.PollUntilTerminalAsync(
            session.SessionId, timeout: TimeSpan.FromSeconds(30));

        SessionStates.IsTerminal(final.State).Should().BeTrue();
    }
}
```

---

## Phase 4: Contract Tests

### File: `Contracts/HealthAndRegistryTests.cs`

```csharp
using FluentAssertions;
using TestController.ApiTests.Infrastructure;
using Xunit;

namespace TestController.ApiTests.Contracts;

public class HealthAndRegistryTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiClient _api;
    public HealthAndRegistryTests(ApiTestFixture fixture) => _api = fixture.Client;

    [Fact]
    public async Task Health_Endpoint_IsReachable()
    {
        var healthy = await _api.IsHealthyAsync();
        healthy.Should().BeTrue();
    }

    [Fact]
    public async Task AgentRegistry_ReturnsAgents()
    {
        var agents = await _api.GetRegisteredAgentsAsync();
        agents.Should().NotBeNull();
    }
}
```

---

## Phase 5: Verify and Run

### Build and run in-memory (default)

```bash
dotnet restore
dotnet build
dotnet test
```

### Run a single test first

```bash
dotnet test --filter "FullyQualifiedName~Trigger_Execute_Results_HappyPath"
```

### Run against the live fleet

```bash
API_TEST_MODE=Live API_TEST_URL=https://rcloud.dev.wonderware.com dotnet test
```

---

## Phase 6: CI Integration

### Every build (fast, in-memory) — azure-pipelines.yml

```yaml
- task: DotNetCoreCLI@2
  displayName: 'API Workflow Tests (in-memory)'
  inputs:
    command: test
    projects: '**/TestController.ApiTests.csproj'
    arguments: '--logger trx --results-directory $(Agent.TempDirectory)'
  # No env vars = in-memory mode = no infrastructure needed
```

### Nightly (real fleet)

```yaml
- task: DotNetCoreCLI@2
  displayName: 'API Workflow Tests (LIVE)'
  inputs:
    command: test
    projects: '**/TestController.ApiTests.csproj'
  env:
    API_TEST_MODE: Live
    API_TEST_URL: https://rcloud.dev.wonderware.com
    API_TEST_TIMEOUT_SECONDS: 300
```

---

## The Plug-In Checklist (do these in order)

| # | What | Where | Effort |
|---|---|---|---|
| 1 | Make `Program` public | WebApi `Program.cs` | 2 min |
| 2 | Fix project reference path | `.csproj` | 2 min |
| 3 | Match DTO property names to real JSON | `Contracts.cs` | 15 min |
| 4 | Match route strings to real controllers | `ApiClient.cs` | 15 min |
| 5 | Use real WatchItem tags + agent names | `TestWatchItems.cs` | 10 min |
| 6 | Implement `FakeAgentDispatcher` against real interface | `FakeAgentDispatcher.cs` | 30 min |
| 7 | Wire the service swap in the fixture | `ApiTestFixture.cs` | 15 min |

After 1-5, the happy-path test should run in-memory (with a real or stub
dispatcher). After 6-7, failure-injection tests work too.

---

## Design Principles (why it's built this way)

| Principle | Where | Why |
|---|---|---|
| One mode switch, two contexts | `TestConfig` | Same tests validate logic (fast) and reality (live) |
| All routes in one client | `ApiClient` | Endpoint change = one edit, not fifty |
| Poll-with-timeout, never Sleep | `WorkflowHelpers` | Fast when done, non-flaky when slow |
| Builders for test data | `TestWatchItems` | Readable tests, reusable shapes |
| Fake agent with injectable failures | `FakeAgentDispatcher` | Test failure recovery without breaking real VMs |
| Independent tests, own session each | All workflow tests | No shared state, no order dependence |
| Contract DTOs separate from production | `Contracts.cs` | A rename forces a conscious test update |

---

## Recommended Build Order

1. **Phase 0-1** — project + infrastructure compiles
2. **One green test** — `Trigger_Execute_Results_HappyPath` in-memory. Proves the wiring.
3. **Fake agent** — enables failure tests (highest-value coverage)
4. **Concurrency + cancellation** — exercises ConcurrentDictionary paths
5. **Live mode** — run the same tests against the real controller
6. **CI** — in-memory every build, live nightly

Get one green test before building out the rest. It proves the approach and
becomes the template for every other workflow.

---

## Regression Guards Built In

These tests directly guard against bugs already seen in this system:

| Test | Guards against |
|---|---|
| `Trigger_ReportsProgress_NotStuckAtZero` | "Active Session stuck at 0%" defect |
| `FailedInstall_SurfacesAsFailure_DoesNotHang` | Pipeline hangs on install failure |
| `UnreachableAgent_DoesNotHangForever` | 2-hour gRPC unreachable + circuit breaker |

Once green and in CI, these bugs cannot silently regress — a regression breaks
the build immediately.

---

**End of specification.**
