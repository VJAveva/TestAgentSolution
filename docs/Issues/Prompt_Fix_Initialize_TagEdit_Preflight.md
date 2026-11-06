# Prompt — Investigate and fix: Initialize parameter file, node Tag editing, pre-flight clarity

> Paste the ground rules from the code-audit skill first. Then this prompt. One part at a time.
> Rule: **investigate and report before changing anything.** Each part stops after the report until I say "fix".

---

## Part 1 — Investigate (no code changes)

```
Investigate three problems reported on controller AUT1 (WPF controller app), then STOP and report.
Do not change code in this part.

A. Initialize node / pipeline parameter file
   Symptom: from the controller app there is no way to load, edit or refresh the pipeline JSON
   (Parameters\<Release>\<Release>-<UseCase>-pipeline-config.json) used by an Initialize node.
   Changing ParameterFile or editing the JSON on disk does not show up reliably; an empty Profile
   ("profile ''") silently drops all profile values.
   Find: what UI exists today for the Initialize node (check docs/ and Prompt_Initialize_ParameterEditor
   history), how ParameterFile and Profile are edited and saved, how ParameterFileMonitor reloads, and
   whether "keep last good values on bad JSON" can hide an edit. Reproduce each with a test or exact steps.

B. Editing node Tags does not work
   Symptom: changing the Tag of a node (Action, ActionGroup, Initialize) in a pipeline, and of nodes inside
   Library templates, does not save or does not take effect.
   Find: the edit path (view -> view model -> model -> serializer -> file) for WatchItem nodes AND template
   nodes; where the change is lost (binding mode, clone vs original node, NodeId vs Tag lookup, template
   instance vs template definition, dirty flag, save path). Separately: is WatchItem.Tag rename blocked on
   purpose? (Tag is an identity key: RBAC PipelineAssignments, locks, audit ResourceId, history.)

C. Pre-flight messages that misled the user today
   1. "[PASS]" / "[FAIL]" / "[DEMO]" text inside commands reported as unresolved tokens.
   2. Unreadable parameter file reported as ~11 unresolved tokens instead of one root cause.
   3. Empty Profile reported only as "profile ''".
   4. Unresolved tokens listed without the node that uses them.
   5. No indication of WHICH parameter file + profile was loaded, or that a last-good copy was used.

Report for each item: root cause with file:line, a failing test that reproduces it, proposed minimal fix,
risk, and anything that is by design. Then stop.
```

## Part 2 — Fix A: Initialize parameter file in the controller app

```
Fix A per the Part 1 report. Minimal, testable, WPF first.
1. Initialize node editor: Browse for ParameterFile (default folder Parameters\); Profile becomes a dropdown
   filled from the chosen file's Profiles (no free text, no empty value when the file has profiles).
2. "Open file" (opens the JSON in the default editor) and "Reload" (re-reads now) buttons.
3. Validate on Browse/Reload/Save: file exists, valid JSON (report line/column), Profile exists in the file.
   Show the result inline on the node; Save of the WatchList is blocked on an invalid Initialize.
4. When ParameterFileMonitor falls back to the last good copy, surface it: node badge + pre-flight warning
   "using last good copy from <time>; current file invalid at line N".
5. Do not write the JSON from the app in this part (read/validate/reload only); editing JSON in-app is a
   separate decision.
Tests: browse sets path; profile list comes from file; empty/unknown profile blocked; invalid JSON shows
line; last-good fallback is visible; red-proof each. Respect Ui ratchets (DynamicResource,
AutomationProperties.Name). Build/test with -c Release.
```

## Part 3 — Fix B: node Tag editing (pipelines and templates)

```
Fix B per the Part 1 report.
1. Action / ActionGroup / Initialize Tag edits save and take effect, in pipelines AND in Library templates
   (template edit updates the template definition, and every Ref shows it on reload).
2. Tags stay unique within their parent group; duplicate -> inline error, Save blocked.
3. WatchItem (pipeline) Tag: if rename is blocked by design, show a clear message ("pipeline name is its ID;
   renaming would orphan permissions and history") instead of silently ignoring the edit. If you recommend
   supporting rename, write a short ADR first (migrate PipelineAssignments, locks, audit ResourceId,
   pipelines{} key in configs) - do not implement it in this part.
4. Generated/expanded nodes keep unique NodeIds; edits never change NodeId.
Tests: tag edit persists through save + reload for a pipeline node and a template node; duplicate blocked;
WatchItem rename shows the message; red-proof each.
```

## Part 4 — Fix C: pre-flight clarity

```
Fix C per the Part 1 report.
1. Tokens are only [_Name] (underscore prefix). Any other bracketed text ([PASS], [DEMO]) is NOT a token:
   ignore it, or at most a warning "bracketed text is not a token". Check the resolver and validator agree.
2. Parameter file missing/invalid -> ONE failure naming the file and the JSON line; suppress the unresolved
   token list that follows from it.
3. Empty or unknown Profile -> its own failure: "Initialize Profile is empty; file defines: Single, Warm".
4. Each unresolved token lists the node(s) that use it: "_SetupFolder - used by 'Run Prepare-Agent on aut2'".
5. Parameters tab header shows the loaded file path, the profile, and "last good copy" when applicable.
Tests for each, using today's AUT1 cases; red-proof each. Same output in WPF and the web pre-flight.
```

---

Stop after each part and report: what changed, tests before/after (controller and WebApi suites),
anything found but not fixed.
