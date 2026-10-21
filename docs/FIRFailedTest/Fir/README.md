# FIR test fixtures (sanitized)

Source: OAK_main_20261005.3 / Set1, agent jvgr1, run 2026-10-10.

| File | Test | Outcome | Use |
|---|---|---|---|
| S1_04_SmokeTestDeploy.log | S1_04_SmokeTestDeploy | Failed | root Row 6 (platform startup), 32 failures, masked Row 32 |
| S1_13_TPTest3.log | S1_13_TPTest3 | Failed | masked root Row 23 (Deployed 0 of 1), cascade Rows 29-32 |
| S1_21_GalaxyStatus.log | S1_21_GalaxyStatus | Passed | must yield no FIR; 64 noise lines |
| WASSet1.fixture.xlsx | sheets for the three tests only | - | Excel row N = log "Row No: N" |

Sanitized: every `(GetUserToken) grpcuser: ...` value -> `<redacted>`; every JWT -> `<redacted-jwt>`.
The redaction markers must survive: FIR must still report SecretsFound (2 and 4) by matching the
GetUserToken line shape, not the secret value.

Not included: .trx files (not provided). The vstest .log carries the same debug trace as the TRX
DebugTrace/StdOut, so FirLogParser should accept plain text from either source.
