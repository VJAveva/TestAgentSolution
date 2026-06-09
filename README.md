# TestAgentSolution

A **distributed test execution orchestration platform** that dispatches and monitors automated test pipelines across remote agent nodes via gRPC, with a React browser client for real-time visibility.

---

## Architecture Overview

```
  Browser (React SPA)  ←─── SignalR + REST ───→  Controller Host
                                                       │
                                                   gRPC (Protobuf)
                                                       │
                           ┌───────────────────────────┼───────────────┐
                           │                           │               │
                     TestAgent-1               TestAgent-2        TestAgent-N
                     (gRPC :5200)              (gRPC :5200)       (gRPC :5200)
```

- **Controller** manages WatchList pipelines, schedules execution sessions, and aggregates results.
- **Agents** execute commands, run tests, and stream logs back to the controller.
- **WebClient** provides real-time monitoring dashboards via SignalR.

Two deployment modes:
- **Mode A – WPF Desktop Controller** (`TestControllerGrpc`) for interactive use.
- **Mode B – Standalone WebApi** (`TestController.WebApi`) for headless/IIS deployment.

---

## Projects

| Project | Description |
|---------|-------------|
| `TestControllerGrpc` | WPF controller application (Mode A) |
| `TestControllerGrpc.Core` | Shared domain: execution engine, lock manager, event system |
| `TestController.Api` | Shared API controllers, SignalR hubs, middleware |
| `TestController.WebApi` | ASP.NET Core host for standalone deployment (Mode B) |
| `TestController.WebClient` | React + Vite + TypeScript SPA |
| `TestController.Dashboard` | WPF dashboard companion (Mode A) |
| `TestAgentGrpc` | Remote agent service (gRPC server) |
| `TestAgentDisplay` | Agent-local WPF monitoring window |
| `TestControllerGrpc.Tests` | Unit/integration tests for controller logic |
| `TestController.WebApi.Tests` | WebApi endpoint tests |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or later)
- [Node.js 20+](https://nodejs.org/) (for WebClient build)
- Windows 10/11 or Windows Server 2019+ (WPF projects require Windows)

---

## Build

### Full Solution

```bash
dotnet build TestAgentSolution.sln
```

### WebClient Only

```bash
cd TestController.WebClient
npm install
npm run build
```

### Clean Build

```bat
CleanBuild.bat
```

---

## Run

### Standalone WebApi (Mode B)

```bash
cd TestController.WebApi
dotnet run
```

The API is available at `https://localhost:5001` (or as configured in `appsettings.json`).  
OpenAPI docs are served at `/openapi/v1.json`.

### WPF Controller (Mode A)

Open `TestControllerGrpc` project in Visual Studio and run (F5).

### Agent Node

```bash
cd TestAgentGrpc
dotnet run
```

Agents connect back to the controller at the address configured in `appsettings.json`.

---

## Tests

```bash
dotnet test TestAgentSolution.sln
```

With coverage:

```powershell
.\Run-Tests-Coverage.ps1
```

---

## Deployment

See [`deploy/README.md`](deploy/README.md) for production deployment scripts and procedures.

---

## Documentation

| Document | Path |
|----------|------|
| Architecture & Design | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) |
| Architecture Diagrams | [`docs/ARCHITECTURE-DIAGRAMS.md`](docs/ARCHITECTURE-DIAGRAMS.md) |
| Ops Runbook | [`docs/RUNBOOK.md`](docs/RUNBOOK.md) |
| CI Commands | [`docs/CI-COMMANDS.md`](docs/CI-COMMANDS.md) |
| Security Framework | [`docs/MultiIdentity_Security_Framework_Requirements.md`](docs/MultiIdentity_Security_Framework_Requirements.md) |
| Hardening Plans | [`docs/hardening-plan/`](docs/hardening-plan/) |
| Deployment Guide | [`deploy/README.md`](deploy/README.md) |

---

## Key Technologies

- **.NET 10** — ASP.NET Core, gRPC, WPF
- **React 18** — Vite, TypeScript, Tailwind CSS, Zustand, TanStack Virtual
- **SignalR** — Real-time event streaming to browser
- **Protobuf/gRPC** — Controller ↔ Agent communication
- **Polly/Resilience** — Retry, circuit-breaker, timeout policies
