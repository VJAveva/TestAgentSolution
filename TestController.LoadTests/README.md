# TestController.LoadTests

Simulated-fleet **load / soak harness** for the controller (D3 of the scaling plan).
It spins up *N* in-process simulated agents — each a real Kestrel-hosted
`TestAgentService` gRPC server that **registers and heartbeats against a live
controller using the production proto/client** — then probes the controller's
`/api/health/diagnostics` endpoint on a fixed cadence and evaluates the
measurable subset of the SCALING success criteria into a PASS/FAIL gate.

This is **manual / nightly tooling**. It is intentionally **not** part of the
per-PR build or `dotnet test` run.

## Prerequisites

A **running controller** (WPF Controller host or `TestController.WebApi`)
reachable over:

- gRPC `TestControllerService` (Register / Heartbeat / UnRegister), and
- REST `/api/health/diagnostics` (anonymous health endpoint).

The simulated agents advertise `http://<agent-host>:<port>` callbacks, so the
controller must be able to reach the machine running the harness (use `127.0.0.1`
when both run on the same host).

## Run

```pwsh
# 100 agents, 15-minute soak, against a controller on localhost:5000
dotnet run -c Release --project TestController.LoadTests -- `
  --controller http://127.0.0.1:5000 --agents 100 --duration 00:15:00
```

`--controller-api` defaults to the `--controller` URL. See `--help` for the full
option list (heartbeat interval, base port, probe cadence, output rate, p95 gate).

Exit codes: `0` = all measured criteria passed, `1` = one or more failed,
`2` = bad arguments.

## What it asserts

- All *N* agents register and stay visible (no mass drop below 95%).
- Controller stays responsive: diagnostics probe **p95 ≤ `--max-probe-p95`** (2s default).
- **Zero** HTTP 429 (rate-limit) responses and zero probe errors.
- Heartbeats delivered with zero failures.

Browser-side criteria (DOM node count, SignalR msg/s, ThreadPool starvation) are
**out of scope** here — observe those via DevTools / `dotnet-counters` against the
controller, per the scaling plan.

## CI

Runs in `.github/workflows/loadtest-nightly.yml` (nightly cron +
`workflow_dispatch`). The scheduled run targets the `LOADTEST_CONTROLLER_URL`
repo variable; the manual dispatch takes a `controller_url` input. The job
uploads the console report as the `soak-report` artifact.
