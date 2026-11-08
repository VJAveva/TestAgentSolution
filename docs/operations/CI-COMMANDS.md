# CI / Local Test Commands

## Build

```powershell
# Full solution build (quiet output)
dotnet build TestAgentSolution.sln -v q

# WebClient TypeScript check
cd TestController.WebClient && npx tsc --noEmit
```

## Test — .NET

```powershell
# All gRPC/Core tests (agent, controller, models, services, security)
dotnet test TestControllerGrpc.Tests\TestControllerGrpc.Tests.csproj --no-build

# All WebApi integration tests (endpoints, contracts, hardening)
dotnet test TestController.WebApi.Tests\TestController.WebApi.Tests.csproj --no-build

# Run by category
dotnet test --filter "Category=Safety" --no-build
dotnet test --filter "Category=Security" --no-build
dotnet test --filter "Category=Contract" --no-build
```

## Test — WebClient (Vitest)

```powershell
cd TestController.WebClient

# Run all tests
npm test

# Run in watch mode
npm run test:watch

# Run specific file
npx vitest run src/lib/agentStatus.test.ts
npx vitest run src/hooks/useAgentTelemetry.test.ts
```

## Coverage

```powershell
# .NET coverage (all projects)
dotnet test TestAgentSolution.sln --collect:"XPlat Code Coverage" --settings coverlet.runsettings

# WebClient coverage
cd TestController.WebClient && npx vitest run --coverage
```

## Validation Sequence (pre-commit)

```powershell
# 1. Build everything
dotnet build TestAgentSolution.sln -v q

# 2. Run .NET tests
dotnet test TestControllerGrpc.Tests\TestControllerGrpc.Tests.csproj --no-build -v q
dotnet test TestController.WebApi.Tests\TestController.WebApi.Tests.csproj --no-build -v q

# 3. WebClient type check + tests
cd TestController.WebClient
npx tsc --noEmit
npm test
```

## Test Categories

| Category | Purpose |
|----------|---------|
| `Safety` | Execution safeguards, stream protection, polling guards |
| `Security` | Credential redaction, command policy, auth checks |
| `Contract` | API response shapes, proto consistency, route conventions |
| `Integration` | Multi-component interaction simulations |
| `Regression` | Bug-fix verification tests |
