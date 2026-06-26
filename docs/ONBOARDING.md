# Onboarding: First Run on the TestController WebApi

Audience: a new team with **no prior access** to the platform. Goal: go from zero to a
first successful API call using only this document and the published API reference.

> The interactive API reference is served by the WebApi itself at **`/scalar`**.
> The raw OpenAPI document is at **`/openapi/v1.json`** — point Postman, `curl`, or a
> codegen tool at it.

---

## 1. What you're talking to

| Surface | Default endpoint | Purpose |
|---|---|---|
| WebApi (REST + SignalR) | `http://<webapi-host>/` | Everything the WebClient uses |
| API reference UI | `http://<webapi-host>/scalar` | Browse + try endpoints |
| OpenAPI spec | `http://<webapi-host>/openapi/v1.json` | Tooling / codegen |
| Liveness probe | `/healthz/live` | "process is up" |
| Readiness probe | `/healthz/ready` | "agents reachable" |

In co-located deployments the WebApi is fronted by IIS on port 80; standalone dev runs
on the Kestrel port in `appsettings.json`.

## 2. Authentication

The platform runs in one of two modes (see `docs/rbac/05_Default_Mode_Design.md`):

- **Default mode** — RBAC disabled. Requests are accepted without a token; you are
  treated as the default client identity. Good for evaluation.
- **Secured mode** — RBAC enabled. You must send a bearer token.

To get a token in Secured mode:

```bash
curl -X POST http://<webapi-host>/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{ "username": "<you>", "password": "<password>" }'
# → { "token": "..." }
```

Then send it on every call:

```bash
curl http://<webapi-host>/api/agents/fleet \
  -H "Authorization: Bearer <token>"
```

In the `/scalar` UI, click **Authentication**, choose **Bearer**, and paste the token —
it is then attached to every "Send" automatically.

> No account yet? An administrator creates one via `POST /api/users` (requires
> `User_Create`). For a quick read-only look, `POST /api/auth/guest` returns a
> short-lived guest session.

## 3. Your first call

1. Open `http://<webapi-host>/scalar`.
2. Authenticate (Section 2) — skip this in Default mode.
3. Run **`GET /api/agents/fleet`**. A `200` with the agent roster means you're in.
4. Run **`GET /healthz/ready`**. `Healthy` confirms the agents are reachable.

## 4. The endpoints you'll use most

| Endpoint | What it does | Permission (Secured mode) |
|---|---|---|
| `GET /api/agents/fleet` | Current agent roster + telemetry | read |
| `GET /api/watchlist` | The configured WatchItems | read |
| `POST /api/execution/trigger/{id}` | Run a single WatchItem | `Pipeline_Trigger` |
| `POST /api/execution/cancel-all` | Cancel everything in flight | `Pipeline_CancelAll` |
| `GET /api/results` | Build/test results | read |

The full, authoritative list is whatever `/scalar` shows for your deployed version —
this table is a starting map, not the source of truth.

## 5. When something doesn't work

| Symptom | Likely cause | Where to look |
|---|---|---|
| `401 Unauthorized` | Missing/expired token in Secured mode | re-run `/api/auth/login` |
| `403 Forbidden` | Authenticated but lack the permission | ask an admin to assign your role |
| `409 Conflict` on trigger | Agents busy (no run queue yet) | retry, or see Implementation Plan P5-1 |
| `503` on `/healthz/ready` | An agent is unreachable | `docs/TROUBLESHOOTING.md` |

Operational procedures (deploy, rollback, restart, log locations) live in
`docs/RUNBOOK.md`. Architecture and the mode model live under `docs/rbac/` and
`docs/architecture/`.
