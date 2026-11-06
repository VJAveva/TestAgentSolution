# Prompt — WatchList XML editor intelligence + edit by instruction

> Builds on `Intelligent_Authoring_Copilot_Prompt.md` (Oct). Paste the code-audit ground rules first.
> One part at a time; the agent stops after each part. **Investigate first.**

---

## Part 0 — Investigate (no code changes)

```
Investigate the WatchList XML editor and authoring features in the WPF controller app. Do not change code.
Report, with file:line:
1. Which XML editor control is used, what it does today (highlighting, folding, validation, completion),
   and how XML edits sync with the tree view and the WatchList model (and whether XML comments survive).
2. What of Intelligent_Authoring Part A already exists (autocomplete, recipes, apply-to-pool, live
   validation) and where.
3. The single sources of truth to reuse: WatchListValidator, PreflightRunner rules, ParameterResolver /
   TokenDisplay, AgentRegistry, template library, PipelineBuilderService (Preview/Validate), RBAC.
4. Whether any LLM client already exists in the repo (e.g. the impact-mapping reranker) that an assistant
   could use, and how it is configured.
5. Gaps against Parts 1-2 below. Propose the order. Then stop.
```

## Part 1 — Live intelligence in the XML editor (deterministic, no AI)

```
Add live intelligence to the XML editor. Everything must come from the SAME validator / pre-flight /
resolver code the tree view and pre-flight use - no second rule set.

1. Completion
   - Element and attribute names from the WatchList model.
   - AgentName: live registry agents with status (online / busy / reboot / offline).
   - TemplateID: templates in the library. Type: the action-type list.
   - ParameterFile: browse under Parameters\. Profile: profiles defined in THAT file.
   - Typing "[_": tokens from the loaded parameter file + GlobalVariables, with resolved value and source
     layer (Global / Profile / Pipeline / GlobalVars / Trigger); secrets masked.
2. Live diagnostics (squiggles + a problems list, debounced)
   - XML not well-formed (line/column).
   - Unresolved [_Token], naming the node that uses it.
   - Bracketed text that is not a token ([PASS], [DEMO]) - warning, never an unresolved token.
   - Initialize: file missing / invalid JSON (line) / empty or unknown Profile.
   - Duplicate Tags in a group; Ref to a missing template; PollInterval >= Timeout.
   - Credential-looking values in Parameters or Command (must use SecretEnv) - error.
   - Trigger Path+Filter colliding with another WatchItem; Path folder missing.
3. Hover: token -> value + layer; Ref -> template outline; AgentName -> status; Initialize -> loaded file,
   profile, and "last good copy" warning if the file is currently invalid.
4. Quick fixes (one click, through the model, undoable):
   "Set Profile to <x>", "Replace [PASS] with PASS:", "Move credentials to SecretEnv",
   "Create missing template", "Open parameter file at the Global section", "Remove steps that use <token>".
5. Colours: tokens resolved = normal, unresolved = error colour, secret = masked; DynamicResource brushes only.
6. Save is blocked on errors; XML and tree stay in sync both ways; XML comments are preserved.

Tests (use this week's real AUT1 cases): [PASS]/[FAIL] text is a warning not a failure; empty Profile flagged
with the file's profile list; unreadable file gives one error, not 11 tokens; unresolved token names its node;
credential in Parameters is an error with the SecretEnv quick fix; quick fixes undo in one step. Red-proof each.
Respect the Ui ratchets.
```

## Part 2 — Edit the WatchList by instruction

```
Add an "Ask to change" box to the editor. The user types what they want; the editor proposes a change.

1. Two tiers behind one interface (IWatchListAssistant):
   a) Deterministic intents first - work with no AI:
      make install-only (remove steps whose tokens the config does not supply, e.g. Prepare-Agent),
      add/remove a step (restart, settle, mail), apply a step to a pool / profile, change timeouts,
      skip / unskip a node, clone a pipeline for another release (re-point Initialize + trigger, unique
      Tag <=36 chars, safe charset), add a token to the parameter file.
   b) Free-text tier only if Part 0 found an LLM client and it is enabled in config; otherwise hidden.
2. Output is ALWAYS a ChangeSet against the WatchList model (never raw XML):
   { plan[], assumptions[], changes[ {op add|update|remove, nodeId, fields} ] }.
   Unknown agents / templates / tokens are listed as problems, never silently added.
3. Apply to a COPY -> run validator + pre-flight -> show a per-change XML diff -> accept/reject each change
   -> Accept applies as ONE undoable edit. Never auto-save, never run or trigger anything.
4. Context sent to the assistant: the current WatchItem, token NAMES (no values), agents with status,
   template IDs, the rules from Part 1. Never secrets; never write Password/SecretEnv values.
5. RBAC: same permission as editing the WatchList; audit entry "assistant change accepted by <user>".

Tests: "make this install-only" on the SP2026R2 Single Node pipeline removes both Prepare-Agent steps and
leaves no unresolved tokens; "clone for SP2026" produces a valid pipeline with a unique tag and trigger;
unknown agent -> problem listed, change not applied; ChangeSet applied to a copy until Accept; one Undo
reverts all; secrets never appear in the request or the diff. Red-proof each.
```

---

Stop after each part and report: what changed, test counts (controller and WebApi suites), what you found
but did not fix.
