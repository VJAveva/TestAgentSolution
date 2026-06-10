# Conventions — TestAgentSolution

## Naming

| Element | Convention | Evidence |
|---------|-----------|----------|
| Classes | PascalCase, descriptive nouns | `ActionPipelineExecutor`, `AgentLockManager`, `WatchListXmlParserService` |
| Interfaces | `I` prefix | `IActionPipelineExecutor`, `IAppLogger`, `IVocabularyMonitor`, `IAgentGrpcDispatcher` (per `TestControllerGrpc.Core/Services/`) |
| Files | One type per file, filename = type name | `AppLogger.cs`, `WatchListConfig.cs`, `AgentLockManager.cs` |
| Test classes | `{ClassUnderTest}Tests` | `WatchListValidatorTests`, `AgentLockManagerTests`, `ActionPipelineExecutorTests` |
| Async methods | `Async` suffix | `ExecuteEventAsync`, `ExecuteGroupTrackedAsync`, `RetryFailedAsync` |
| React components | PascalCase function exports | `AppShell`, `WatchListTree`, `LiveLogger` |
| React hooks | `use` prefix, camelCase | `useAgents`, `useExecution`, `useSignalR` |
| Zustand stores | `use{Domain}Store` | `useAgentStore`, `useExecutionStore`, `watchlistStore` |

## File Structure

- **One type per file** — enforced throughout .NET projects
- **File-scoped namespaces** (`namespace X;`) — no block-scoped
- **Folder = namespace segment:** `Services/` → `.Services`, `Models/` → `.Models`, `ViewModels/` → `.ViewModels`
- **Partial classes for large VMs:** `MainViewModel.cs` + `MainViewModel.Execution.cs` + `MainViewModel.Agents.cs` etc. (12+ partials)
- **React:** `src/components/{domain}/` for components, `src/hooks/` for hooks, `src/stores/` for state, `src/lib/` for utilities, `src/types/` for TypeScript types

## Async Conventions

- **ConfigureAwait:** Used sparingly — only in library code doing SignalR reconnect (`IExecutionFeed.cs`). Not used in ViewModel or WebApi code (WPF sync context and ASP.NET Core don't need it).
- **CancellationToken:** Threaded through all pipeline methods as `ct` parameter. Per `IActionPipelineExecutor`, every public method accepts `CancellationToken ct`.
- **Async naming:** Consistent `Async` suffix on all Task-returning methods.
- **No `.Result` / `.Wait()`:** Fire-and-forget is explicit (`_ = hostTask`) or uses `Task.WhenAny()`.
- **`AllowConcurrentExecutions`:** RelayCommand attribute used for execution commands that may overlap sessions.

## Error Handling

- **No `Result<T>` pattern.** The codebase uses exceptions for fatal errors and event-based propagation for pipeline failures.
- **Pipeline errors:** `NodeFailed` event with exit code + error message. `FailAndContinue` flag per ActionGroup/Action controls whether failure halts execution.
- **Custom exception types:** None observed — uses `InvalidOperationException`, `OperationCanceledException`, standard BCL types.
- **Retry policy:** Per-action `MaxRetries` + `RetryDelaySeconds` + `RetryBackoff` (Exponential/Fixed) + `RetryOnExitCodes` (semicolon-delimited). Implemented in `ActionPipelineExecutor`, not Polly (Polly is a dependency in WebApi for HTTP resilience, not pipeline retry).
- **Global crash handling:** `CrashDumpHelper.InstallGlobalHandlers()` at process start in both hosts. Unhandled exceptions → crash log file + event log.
- **WebApi:** Global exception middleware returns 500 JSON. Config validation fails startup in production (`ConfigValidator`).

## Logging

- **Library:** Custom `AppLogger` implementing `IAppLogger` (not Serilog)
- **Pattern:** `logger.Info("Category", "message")` / `logger.Error("Category", "msg", ex)` / `logger.Warn(...)`
- **Also:** `ILogger<T>` from Microsoft.Extensions.Logging for framework services
- **Structured fields:** `category`, `correlationId`, `elapsedMs` — but message itself is string interpolation, NOT template-based
- **Log levels per scenario:**
  - `Info` — operation start/end, state transitions, configuration loaded
  - `Debug` — not heavily used (AppLogger doesn't expose a Debug method; use `ILogger.LogDebug`)
  - `Warning` — recoverable issues (port fallback, CORS open, tray exit during execution)
  - `Error` — handler failures with exception attached
  - `Critical` — startup config validation failure (via `ILogger.LogCritical`)
- **Security:** `SecurityRedactor.Redact()` applied to all messages before file write (passwords, tokens)

## WPF MVVM

- **Framework:** CommunityToolkit.Mvvm 8.2.2
- **Base class:** `ObservableObject` (source-generated `INotifyPropertyChanged`)
- **Properties:** `[ObservableProperty]` attribute — generates backing field + notification
- **Commands:** `[RelayCommand]` attribute with optional `CanExecute = nameof(...)` and `AllowConcurrentExecutions`
- **View-to-VM binding:** `DataContext` set in code-behind or XAML via `xmlns:vm` + static locator (`App.Services`)
- **Navigation:** Tab panels in MainWindow + standalone popup windows (`Window.ShowDialog()` / `.Show()`)
- **Collections:** `ObservableCollection<T>` and custom `RangeObservableCollection<T>`
- **Dispatcher:** `Application.Current.Dispatcher.InvokeAsync()` for UI-thread marshaling from background events
- **Theming:** DynamicResource bindings with three ResourceDictionary themes (Dark/Light/HighContrast)

## React

- **Hooks-only:** No class components. All logic in custom hooks (`useAgents`, `useExecution`, etc.)
- **State management:** Zustand (`create<T>()`) — one store per domain. No Redux, no Context for global state.
- **API calls:** `axios` in hooks (GET/POST/DELETE). Also `apiFetch<T>()` wrapper in `lib/api.ts` with correlation IDs.
- **Component layout:** `export default function ComponentName()` — one component per file, PascalCase filename
- **Props:** TypeScript interfaces in `src/types/`, prop types inlined or imported
- **CSS:** Tailwind utility classes inline. No CSS modules, no styled-components.
- **Real-time:** `useSignalR` hook manages singleton HubConnection with event subscriptions that update Zustand stores
- **Testing:** Vitest + @testing-library/react + jsdom. `vi.mock()` for deps, `renderHook()` for hook tests.
- **No router:** Single-page with tab state in `AppShell`

## Testing

- **xUnit fixtures:**
  - Integration: `IClassFixture<TestWebAppFactory>` (shared `WebApplicationFactory<Program>`)
  - Unit: Plain test classes, no base class
  - Cleanup: `IDisposable` for temp directories
- **Vitest patterns:** `describe`/`it`/`expect`, `vi.useFakeTimers()`, `vi.mock()`, `renderHook()` + `act()`
- **Test naming:** `Method_Should_Expected_When_State` (dominant). Some simpler: `Method_State_Expected`.
- **Test data builders:** Static `TestFixtures` class with factory methods (`CreateLock(...)`, `CreateExecutionContext(...)`, `CreateTempDir(...)`)
- **Assertions:** xUnit `Assert.*`. No FluentAssertions observed.
- **Frontend assertions:** `@testing-library/jest-dom` matchers via Vitest globals

## Dependency Injection

- **Style:** Constructor injection exclusively. No property injection observed.
- **Service lifetimes:** Almost everything is `Singleton`. No `Scoped` services in either host (no per-request scope pattern). Only `AddScoped<IHubFilter>` for SignalR hub filter.
- **Registration pattern:**
  - Direct `services.AddSingleton<Interface, Implementation>()` calls in `App.xaml.cs` (WPF) and `Program.cs` (WebApi)
  - Extension method `AddControllerApi()` groups shared API registrations (per `TestController.Api/ControllerApiExtensions.cs`)
  - Extension method `AddMultiIdentitySecurity()` groups auth registrations
  - `builder.Services.Configure<T>(section)` for options pattern
- **Hosted services:** `AddHostedService<T>()` or `AddHostedService(sp => sp.GetRequiredService<T>())` for singletons that also implement `IHostedService`
- **Static fallback:** `App.Services` (WPF) provides service location where DI can't reach (window construction)
