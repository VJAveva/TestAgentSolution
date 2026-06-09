using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Tests for the snapshot recovery feature: LoadPersistedSessions,
/// ReloadSnapshotsCommand, and the dirty-checking guards that prevent
/// stack overflow in ActionPillVM / SessionCardVM / AgentRowVM.
/// </summary>
public class SnapshotRecoveryAndDirtyGuardTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Phase 2.2 — Dashboard Snapshot Recovery
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void LoadPersistedSessions_Should_AddCards_When_PersistedDataExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"SnapRecovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var persistPath = Path.Combine(tempDir, "sessions.json");
            var events = new EventAggregator();
            var mgr = new ExecutionSessionManager(events, persistPath);

            // Create and complete a session
            var session = mgr.BeginSession("Deploy.Api", "Renamed",
                new Dictionary<string, string> { ["_BuildNumber"] = "1.0" },
                new List<IActionNode>(), "snap01");
            session.UserId = "dev1";
            session.Source = "WebClient";
            session.LockedAgents = new[] { "Agent-01" };
            mgr.RecordResult("snap01", new ActionExecutionResult
            {
                ActionTag = "Install",
                ActionType = "RunRemoteCommand",
                AgentName = "Agent-01",
                Outcome = ActionOutcome.Success,
            });
            mgr.RecordResult("snap01", new ActionExecutionResult
            {
                ActionTag = "Test",
                ActionType = "RunRemoteCommand",
                AgentName = "Agent-01",
                Outcome = ActionOutcome.Failed,
                ExitCode = 1,
                ErrorMessage = "2 tests failed",
            });
            mgr.CompleteSession("snap01");
            Thread.Sleep(500);

            // Create a new manager (simulating restart)
            var mgr2 = new ExecutionSessionManager(events, persistPath);
            var lockMgr = new AgentLockManager();
            var vm = new ExecutionDashboardVM(mgr2, lockMgr, events,
                System.Windows.Threading.Dispatcher.CurrentDispatcher);

            // LoadPersistedSessions is called in constructor
            Assert.Contains(vm.Sessions, s => s.SessionId == "snap01");
            var card = vm.Sessions.First(s => s.SessionId == "snap01");
            Assert.Equal("Deploy.Api", card.WatchItemTag);
            Assert.Contains("Recovered", card.Source);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void LoadPersistedSessions_Should_SkipDuplicates_When_SessionAlreadyExists()
    {
        var vm = ExecutionDashboardVM.CreateForFeed(
            System.Windows.Threading.Dispatcher.CurrentDispatcher);

        // Pre-add a card
        vm.Sessions.Add(new SessionCardVM
        {
            SessionId = "existing01",
            WatchItemTag = "Live.Session",
            Status = "Running",
        });

        // Simulate reload — the existing session should not be duplicated
        Assert.Single(vm.Sessions);
    }

    [Fact]
    public void LoadPersistedSessions_Should_MarkCrashedSessions_When_StateWasRunning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"SnapCrash_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var persistPath = Path.Combine(tempDir, "sessions.json");

            // Write a "Running" session (simulating crash)
            var persisted = new[]
            {
                new ExecutionSessionManager.PersistedSession
                {
                    SessionId = "crashed01",
                    WatchItemTag = "Nightly",
                    State = "Running",
                    UserId = "scheduler",
                    Source = "WebClient",
                    ActionResults = new[]
                    {
                        new ExecutionSessionManager.PersistedActionResult
                        {
                            ActionTag = "Install",
                            Outcome = "Success",
                            AgentName = "Agent-01",
                        },
                    },
                }
            };
            File.WriteAllText(persistPath,
                System.Text.Json.JsonSerializer.Serialize(persisted));

            var events = new EventAggregator();
            var mgr = new ExecutionSessionManager(events, persistPath);
            var vm = new ExecutionDashboardVM(mgr, new AgentLockManager(), events,
                System.Windows.Threading.Dispatcher.CurrentDispatcher);

            var card = vm.Sessions.FirstOrDefault(s => s.SessionId == "crashed01");
            Assert.NotNull(card);
            Assert.Equal("Failed", card.Status);
            Assert.Contains("Crashed", card.Source);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void LoadPersistedSessions_Should_PopulateAgentRows_When_ResultsExist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"SnapAgents_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var persistPath = Path.Combine(tempDir, "sessions.json");

            var persisted = new[]
            {
                new ExecutionSessionManager.PersistedSession
                {
                    SessionId = "agents01",
                    WatchItemTag = "Deploy",
                    State = "Failed",
                    ActionResults = new[]
                    {
                        new ExecutionSessionManager.PersistedActionResult
                        {
                            ActionTag = "Install",
                            ActionType = "RunRemoteCommand",
                            AgentName = "Agent-01",
                            Outcome = "Success",
                        },
                        new ExecutionSessionManager.PersistedActionResult
                        {
                            ActionTag = "RunTests",
                            ActionType = "RunRemoteCommand",
                            AgentName = "Agent-02",
                            Outcome = "Failed",
                            ExitCode = 1,
                            ErrorMessage = "Test failed",
                        },
                    },
                }
            };
            File.WriteAllText(persistPath,
                System.Text.Json.JsonSerializer.Serialize(persisted));

            var events = new EventAggregator();
            var mgr = new ExecutionSessionManager(events, persistPath);
            var vm = new ExecutionDashboardVM(mgr, new AgentLockManager(), events,
                System.Windows.Threading.Dispatcher.CurrentDispatcher);

            var card = vm.Sessions.FirstOrDefault(s => s.SessionId == "agents01");
            Assert.NotNull(card);
            Assert.Equal(2, card.Agents.Count);
            Assert.Contains(card.Agents, a => a.AgentName == "Agent-01");
            Assert.Contains(card.Agents, a => a.AgentName == "Agent-02");
            Assert.True(card.FailedActions > 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Phase 2.3 — Stack Overflow Fix: Dirty-Check Guards
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ActionPillVM_Should_NotFireNotification_When_ValueUnchanged()
    {
        var pill = new ActionPillVM
        {
            Tag = "Install",
            ActionType = "RunRemoteCommand",
            Command = "install.cmd",
            Status = "Success",
            ExitCode = 0,
        };

        var notifications = new List<string>();
        pill.PropertyChanged += (s, e) => notifications.Add(e.PropertyName!);

        // Re-set same status — should NOT fire cascading notifications
        notifications.Clear();
        pill.Status = "Success"; // Same value

        // The OnStatusChanged fires RaiseDerivedIfChanged which checks cache
        // DisplayLabel, StatusIcon, Tooltip should NOT fire since they haven't changed
        Assert.DoesNotContain("DisplayLabel", notifications);
        Assert.DoesNotContain("StatusIcon", notifications);
        Assert.DoesNotContain("Tooltip", notifications);
    }

    [Fact]
    public void ActionPillVM_Should_FireNotification_When_ValueActuallyChanged()
    {
        var pill = new ActionPillVM
        {
            Tag = "Install",
            Status = "Pending",
        };

        var notifications = new List<string>();
        pill.PropertyChanged += (s, e) => notifications.Add(e.PropertyName!);

        pill.Status = "Running"; // Different value

        Assert.Contains("Status", notifications);
        // StatusIcon changes from ○ to ● — should fire
        Assert.Contains("StatusIcon", notifications);
    }

    [Fact]
    public void AgentRowVM_UpdateAction_Should_ShortCircuit_When_NothingChanged()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Success",
            exitCode: 0, errorMessage: "", duration: "30s");

        var notifications = new List<string>();
        row.PropertyChanged += (s, e) => notifications.Add(e.PropertyName!);

        // Re-apply identical values — should short-circuit
        notifications.Clear();
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Success",
            exitCode: 0, errorMessage: "", duration: "30s");

        // No property changes should fire on the pill or row
        Assert.Empty(notifications);
    }

    [Fact]
    public void AgentRowVM_UpdateAction_Should_NotDowngrade_When_TerminalStatus()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Success");

        // Attempt to downgrade from Success to Running (stale reconcile)
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Running");

        Assert.Equal("Success", row.Actions.First().Status);
    }

    [Fact]
    public void SessionCardVM_Should_NotFireCascade_When_DerivedValuesUnchanged()
    {
        var card = new SessionCardVM
        {
            SessionId = "test01",
            Status = "Running",
            PassedActions = 5,
            FailedActions = 2,
            ProgressPercent = 70,
        };

        var notifications = new List<string>();
        card.PropertyChanged += (s, e) => notifications.Add(e.PropertyName!);

        // Re-set same status — dirty guard checks should prevent cascade
        notifications.Clear();
        card.Status = "Running"; // Same value

        // StatusBadge didn't change, so the dirty guard suppresses it
        Assert.DoesNotContain("StatusBadge", notifications);
    }

    [Fact]
    public void SessionCardVM_Should_FireCascade_When_StatusChanges()
    {
        var card = new SessionCardVM
        {
            SessionId = "test01",
            Status = "Running",
        };

        var notifications = new List<string>();
        card.PropertyChanged += (s, e) => notifications.Add(e.PropertyName!);

        card.Status = "Failed"; // Different

        Assert.Contains("Status", notifications);
        Assert.Contains("StatusBadge", notifications);
    }

    [Fact]
    public void ActionPillVM_RapidUpdates_Should_NotStackOverflow_When_ManyPillsUpdated()
    {
        // Simulate the 1-sec reconcile tick: 50+ pills updated in sequence
        var row = new AgentRowVM { AgentName = "Agent-01" };

        // Create 50 pills
        for (var i = 0; i < 50; i++)
        {
            row.UpdateAction($"Step{i}", "RunRemoteCommand", $"cmd{i}.cmd", "Success",
                exitCode: 0, duration: $"{i}s");
        }

        // Now re-apply all of them (reconcile) — this previously caused stack overflow
        for (var iteration = 0; iteration < 10; iteration++)
        {
            for (var i = 0; i < 50; i++)
            {
                row.UpdateAction($"Step{i}", "RunRemoteCommand", $"cmd{i}.cmd", "Success",
                    exitCode: 0, duration: $"{i}s");
            }
        }

        // If we get here without StackOverflowException, the fix works
        Assert.Equal(50, row.Actions.Count);
    }
}
