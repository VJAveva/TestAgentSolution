---
name: update-architecture-diagrams
description: 'Reconcile docs/ARCHITECTURE-DIAGRAMS.md (Mermaid C4, gRPC surface, host internals, runtime flows, deployment, RBAC) with the current code: find type, project, RPC, endpoint, port and file names that no longer exist and fix them. Use for update architecture diagrams, stale diagram, docs drift, rename in docs.'
---

# Update Architecture Diagrams

Target: `docs/ARCHITECTURE-DIAGRAMS.md` (sections 1-11, mostly ```mermaid blocks plus the "Where to look in code" table). The scope is **names**: fix renamed or removed identifiers. Don't redesign diagrams or add components unless the user asks.

## Encoding first
The file already contains mojibake (e.g. a damaged dash in the title). Check for a BOM and count non-ASCII bytes **before** editing:
`@([IO.File]::ReadAllBytes($p) | ? { $_ -gt 127 }).Count`.
Edit only the lines you change, never round-trip the whole file through a tool that re-encodes, and check the non-ASCII count is unchanged afterwards (unless you deliberately fixed one).

## Procedure
1. **Extract identifiers** from every Mermaid block and the code table:
   - C# type names: PascalCase tokens ending in Service, Manager, Controller, Hub, Executor, Dispatcher, Store, Registry, Runner, Provider, VM, ViewModel, Window, Worker, Interceptor, and the like.
   - Project names (`TestController*`, `TestAgent*`).
   - gRPC services and RPCs (match against `TestControllerGrpc.Core/Protos/*.proto`).
   - REST routes (`/api/...`) and SignalR hubs (`/hubs/...`).
   - Ports (5100, 5200, 81), file and config names (`WatchList.xml`, `appsettings.json`, `orchestrator.db`), paths.
2. **Look each one up in code** (grep, excluding `docs/`, `bin/`, `obj/`, `publish/`, `node_modules/`):
   - Types: `class|interface|record|enum <Name>\b`.
   - RPCs: `rpc <Name>` in the .proto files.
   - Routes: `[Route(`, `[Http*("`, and `Map(Get|Post|...)` in Endpoints.
3. **Classify** each identifier:
   - **OK**: found.
   - **Renamed**: not found, but one clear successor exists (same folder or role, similar name, `git log -S<OldName> --oneline` shows the rename). Confirm from the diff, not by name similarity alone.
   - **Removed**: no successor. Propose removing the node or edge rather than guessing a replacement.
   - **Ambiguous**: several candidates. Ask the user.
4. **Also check facts the names imply**: ports in `appsettings*.json` / `fleet-inventory.json`, which host serves which controller (`ProxiedControllerExclusionProvider` for proxied ones), and the proto service list. Report mismatches; fix only names unless the user approves more.
5. **Edit**: change only the mismatched tokens. Keep the Mermaid syntax valid: node ids without spaces, labels quoted if they contain `()[]{}`, and no `--` inside labels where it breaks parsing.
6. **Validate**: every changed block still renders. Check bracket and quote balance; if the integrated browser or a preview is available, render the file. Re-run step 2 on the changed identifiers: all OK.

## Report
`| Section | Old name | New name / action | Evidence (file:line or commit) |`. Then: identifiers checked (count), changed, flagged ambiguous, flagged removed, and fact mismatches left for the user.
