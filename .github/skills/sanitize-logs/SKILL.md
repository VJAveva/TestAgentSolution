---
name: sanitize-logs
description: 'Sanitize logs, TRX files and vstest console output before sharing or committing them. Removes GetUserToken lines, JWTs, bearer tokens, passwords and connection-string secrets, then proves none of the original values remain. Use for redact, scrub, sanitize, strip secrets, prepare fixture, attach log, commit TRX.'
argument-hint: 'Path(s) to the log / .trx / folder to sanitize'
---

# Sanitize Logs / TRX

Produces a sanitized copy of each input file and a report listing, per category, how many lines were changed. The report never contains the secret values.

## Rules
- **Never print a secret value** in the chat, the terminal or the report. Show only counts, line numbers and a SHA256 prefix (8 chars) when values need to be told apart.
- Write to a **copy** (`<name>.sanitized<ext>`) unless the user explicitly says to redact in place. When redacting in place, back up first, and remember the backup still contains the secret.
- Keep the line count the same: **replace the value, don't delete the line**, so diagnostic context survives. The exception is GetUserToken lines; see the table.
- Keep the encoding: detect the BOM and write back with the same `UTF8Encoding($hasBom)`. For `.trx`, the result must still parse as XML.
- Use absolute paths with `[IO.File]` methods. `Set-Location` does not change the process cwd that they resolve against.

## Patterns (same as the in-app redactors)
| Category | Pattern | Replace with |
|---|---|---|
| GetUserToken | line contains `(GetUserToken)` or `GetUserToken` followed by a credential | replace everything after the label with `<redacted>` |
| JWT | `eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*` | `<redacted-jwt>` |
| Bearer | `(?i)\b(Bearer\s+)[A-Za-z0-9\-._~+/]{16,}={0,2}` | `$1<redacted>` |
| JSON secret | `(?i)("(?:password\|passwd\|pwd\|secret\|token\|apikey\|api_key\|accesskey\|access_key\|clientsecret\|client_secret\|credential)"\s*:\s*")([^"]*)(")` | `$1<redacted>$3` |
| key=value | `(?i)(\b(?:password\|passwd\|pwd\|secret\|token\|apikey\|api_key\|accesskey\|access_key\|clientsecret\|client_secret\|credential)\b\s*[:=]\s*)([^\s;,'")]+)` | `$1<redacted>` |
| PS flag | `(?i)(-(?:password\|passwd\|pwd\|secret\|token\|apikey\|credential)\s+)(\S+)` | `$1<redacted>` |
| Conn string | `(?i)(\b(?:Password\|Pwd)\s*=\s*)([^;"<]+)` | `$1<redacted>` |
| Positional creds | `(RevertRcloudMachine\.bat\s+\S+\s+\S+\s+)(\S+)(\s+)(\S+)` and `(Args\s+:\s+\S+\s+\S+\s+)(\S+)(\s+)(\S+)` | `$1<redacted>$3<redacted>` |

Sources of truth: `TestControllerGrpc.Core/Services/SecurityRedactor.cs`, `TestControllerGrpc.Core/Fir/FirRedactor.cs`. If either file gains a pattern, add it here too.

## Procedure
1. **Read and assert.** Read each file. If the read throws (for example a live log held by the controller), **abort for that file**. Never continue with `$null` text: zero matches on a failed read is a false all-clear. Copy locked files first and read the copy.
2. **Collect the originals.** Run every pattern and gather the **captured secret values** (not whole lines) into an in-memory set. Ignore values that are already a marker (`<redacted...>`, `***REDACTED***`). Never output this set.
3. **Replace.** Apply the patterns in table order and write the sanitized copy.
4. **Verify by value, not by shape.**
   - For every collected value (length 4 or more), check `sanitized.Contains(value)`. It must be **false** for all of them. If any remain, report its category and SHA256 prefix, then fix and redo.
   - Re-run every pattern on the output. Only marker text may match.
   - Do **not** verify by counting the line shape (`bat \S+ \S+ \S+ \S+`): the marker is itself `\S+`, so before and after counts are equal and prove nothing.
   - Check the line count is unchanged, and that `.trx` still loads with `[xml]`.
5. **Look for anything else.** Search for `(?i)pass(word)?|pwd|secret|token|apikey|sig=|AccountKey=|SharedAccessSignature` and list matching **line numbers** that no pattern handled. Leave them for the user to review; don't guess.

## Report
- Per file: lines changed per category, number of distinct values removed, unhandled suspicious line numbers, and the output path.
- Verification: "N original values checked, 0 remain".
- Reminder: redaction does not invalidate a credential. If a real password or token was in the file, it **must be rotated**, and the original file and any backups still contain it.
