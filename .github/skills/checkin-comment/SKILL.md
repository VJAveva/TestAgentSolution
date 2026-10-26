---
name: checkin-comment
description: 'Write a five-line plain-language check-in (commit) comment summarising the STAGED git changes. Use for commit message, check-in comment, summarise staged changes.'
---

# Check-in Comment

## Procedure
1. Run `git diff --cached --stat` and `git diff --cached`.
   - If nothing is staged, say so and stop. Do **not** summarise unstaged work and do not stage anything.
   - If the diff is large, read the stat first, then the hunks of the files that matter most.
2. Work out **what changed for a user or operator, and why**, not a list of files. Group related changes together.
3. Write **exactly five lines**:
   1. A summary of 72 characters or fewer: plain sentence, no trailing period, no prefix tags.
   2. to 5. What changed and why, one idea per line, in plain words a tester would understand. Name a feature or screen rather than a class, unless the class *is* the change.
4. Rules:
   - Mention a test or guard only if one was added.
   - Mention deploy-relevant facts if present: a config key that has to be added by hand, a rollout order, or a WatchList edit.
   - No secrets, hostnames with credentials, or ticket IDs that you invented.
   - No emojis and no marketing words ("robust", "seamless").
   - Don't say "fixed bug". Say what was broken.
5. Output the five lines in a single code block. Do **not** run `git commit` unless asked.
