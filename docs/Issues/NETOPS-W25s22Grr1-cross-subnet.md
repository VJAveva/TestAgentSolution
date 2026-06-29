# NETOPS-REQ: Agent W25s22Grr1 cannot register with Controller JVGR22 (cross-subnet TCP blocked)

**Date:** 2026-06-29
**Reported via:** TestAgentSolution agent diagnostics
**Severity:** Medium — new agent node unusable; all other agents unaffected
**Type:** Network ACL / firewall change (no application change required)

## Summary
Agent host **W25s22Grr1** (10.48.220.132) cannot reach the Controller **JVGR22**
(10.48.190.248) on TCP 5100, and the Controller cannot reach back on TCP 5200.
ICMP works both ways; TCP times out (packets dropped). Root cause is inter-subnet
filtering between **10.48.220.0/24** and **10.48.190.0/24**. This is not an app or
host-firewall issue — the Controller's inbound rule is open to Any, the service is
listening, and other agents on 10.48.190.x register normally.

## Evidence
| Host | IPv4 | Subnet | Result |
|------|------|--------|--------|
| Controller JVGR22 | 10.48.190.248 | 190.x | Listening on 5100, FW Allow Any, PID 30084 |
| Agent JVGR1 (works) | 10.48.190.213 | 190.x (same) | TCP 5100 OK, registers |
| Agent W25s22Grr1 (fails) | 10.48.220.132 | 220.x (different) | TCP 5100 timeout; reverse TCP 5200 timeout; ICMP ping OK |

- W25s22Grr1 -> JVGR22:5100 = timeout ("did not properly respond" = dropped, not refused)
- JVGR22 -> W25s22Grr1:5200 = timeout
- Ping succeeds both directions; DNS resolves JVGR22 -> 10.48.190.248
- 105 failed heartbeats, NeverRegistered

## Requested change (bidirectional)
1. Allow **10.48.220.0/24 -> 10.48.190.248 : 5100/TCP**  (agent -> controller gRPC)
2. Allow **10.48.190.248 -> 10.48.220.0/24 : 5200/TCP**  (controller -> agent streamed commands)
3. Allow **10.48.190.0/24 -> 10.48.220.0/24 : 5985/TCP**  (WinRM management/remoting)

Note: ICMP is already permitted across both subnets; only TCP ports are filtered.
WinRM (5985) is currently blocked too, confirming the boundary drops all TCP, not just
the app ports. Opening all three in one change unblocks management + agent traffic.

## Verification after change
From W25s22Grr1:
```powershell
Test-NetConnection 10.48.190.248 -Port 5100   # expect TcpTestSucceeded: True
```
From a 190.x admin host:
```powershell
Test-NetConnection W25s22Grr1 -Port 5985      # expect TcpTestSucceeded: True
```
Agent registers within ~15s; status flips to Registered with OK heartbeats.
