# Firewall Change Request: Cross-Subnet TCP for TestAgent Node W25s22Grr1

| Field | Value |
|---|---|
| **Request ID** | NETOPS-REQ (assign your tracker ID) |
| **Date** | 2026-06-29 |
| **Requested by** | (your name / team) |
| **Change type** | Network ACL / firewall rule addition |
| **Affected subnets** | 10.48.220.0/24 ↔ 10.48.190.0/24 |
| **Urgency** | Medium — one new agent node unusable; existing fleet unaffected |
| **Application change required** | None |
| **Downtime required** | None |

---

## 1. Business Justification

A new test-execution agent node (**W25s22Grr1**) has been provisioned on subnet
10.48.220.0/24 to expand the automated test fleet. It cannot join the test
controller because inter-subnet TCP traffic between 10.48.220.0/24 and
10.48.190.0/24 is filtered. Until the listed ports are permitted, this node
cannot run any automated test work and the hardware sits idle.

All existing agents reside on 10.48.190.0/24 (same subnet as the controller) and
are unaffected. This request only opens the specific ports needed for the new
cross-subnet node to function.

---

## 2. Root Cause (confirmed)

Inter-subnet filtering between 10.48.220.0/24 and 10.48.190.0/24 silently drops
TCP. This is confirmed, not assumed:

| Test | Result | What it proves |
|---|---|---|
| ICMP ping both directions | **Succeeds** | Hosts are reachable; routing works |
| TCP to controller:5100 from agent | **Times out** (not refused) | Packet dropped in transit, not by the host |
| TCP to agent:5200 from controller | **Times out** (not refused) | Reverse path also dropped |
| TCP WinRM:5985 across subnets | **Times out** | The boundary drops ALL TCP, not just app ports |
| Same-subnet agent (190.x) → controller:5100 | **Succeeds, registers** | Controller, port, and app are healthy |

The **timeout (not connection-refused)** response is the decisive evidence: a
refused connection would mean the packet reached the host and nothing was
listening (an application problem). A timeout means the packet never arrived —
it was dropped between the subnets (a network ACL). Combined with ICMP working
and same-subnet agents working normally, the only remaining cause is
inter-subnet TCP filtering.

Host-level firewalls have been ruled out:
- Controller (JVGR22): inbound rule allows the port, service listening (PID 30084)
- Agent (W25s22Grr1): inbound rule for 5200 verified present, service listening

---

## 3. Requested Rules (exact)

Three rules, bidirectional. Please add to the ACL governing 220.x ↔ 190.x:

| # | Source | Destination | Port/Proto | Direction | Purpose |
|---|---|---|---|---|---|
| 1 | 10.48.220.0/24 | 10.48.190.248 | 5100/TCP | agent → controller | Agent registration + gRPC heartbeat |
| 2 | 10.48.190.248 | 10.48.220.0/24 | 5200/TCP | controller → agent | Streamed test commands to the agent |
| 3 | 10.48.190.0/24 | 10.48.220.0/24 | 5985/TCP | mgmt → agent | WinRM remote management of the node |

Notes:
- Rules 1 and 2 are the **minimum** required for the agent to function (gRPC is
  bidirectional: agent dials in on 5100, controller streams back on 5200).
- Rule 3 (WinRM) enables remote management/provisioning of the new node from
  existing admin hosts. It is currently blocked by the same boundary.
- Scope can be tightened to the single host **10.48.220.132/32** instead of the
  /24 if your policy prefers host-specific rules. The /24 is requested to cover
  future agents added to that subnet without a new change.

### If your policy prefers host-specific (most restrictive) scope:

| # | Source | Destination | Port/Proto |
|---|---|---|---|
| 1 | 10.48.220.132/32 | 10.48.190.248/32 | 5100/TCP |
| 2 | 10.48.190.248/32 | 10.48.220.132/32 | 5200/TCP |
| 3 | 10.48.190.0/24 | 10.48.220.132/32 | 5985/TCP |

---

## 4. Risk Assessment

| Aspect | Assessment |
|---|---|
| **Scope** | Three specific TCP ports between two internal subnets. No internet exposure. |
| **Data sensitivity** | Internal test automation traffic only. No production or customer data. |
| **Blast radius if misconfigured** | Limited to the test fleet; no production systems on these ports. |
| **Reversibility** | Fully reversible — remove the three rules to restore current state. |
| **Authentication** | gRPC + WinRM both require valid credentials; opening the port does not grant access without auth. |
| **Precedent** | Equivalent traffic already flows freely within 190.x; this extends the same pattern to one cross-subnet node. |

---

## 5. Verification Plan (post-change)

Immediately after the rules are applied, these confirm success:

**From the agent (W25s22Grr1):**
```powershell
Test-NetConnection 10.48.190.248 -Port 5100   # expect TcpTestSucceeded: True
```

**From a 190.x admin host:**
```powershell
Test-NetConnection W25s22Grr1 -Port 5985      # expect TcpTestSucceeded: True
```

**Application-level confirmation:**
- Within ~15 seconds of the rules applying, the agent registers with the controller
- Agent status flips from `NeverRegistered` to `Registered`
- Heartbeats begin succeeding (currently at 105 failed heartbeats)

If any test still fails after the change, the traceroute output (collected in
the evidence script) will show the hop where packets are still being dropped.

---

## 6. Rollback Plan

Remove the three added rules. No application or host configuration changes were
made, so removal fully restores the pre-change state. No service restart needed
on either host.

---

## 7. Contacts

| Role | Who |
|---|---|
| Requester / app owner | (you) |
| Controller host owner | (JVGR22 owner) |
| Agent host owner | (W25s22Grr1 owner) |
| Verification will be run by | (you) |

---

**Attachment:** Pre-submission evidence output from `Verify-CrossSubnetFirewall.ps1`
(run on both hosts) — paste the captured results here before submitting.
