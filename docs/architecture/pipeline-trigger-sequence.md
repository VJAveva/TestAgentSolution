# Pipeline Trigger Sequence

How a WatchList file drop becomes an agent command and how results flow back.

Traced from:

- `TestControllerGrpc/Services/FileWatcherManager.cs` — `OnTriggered`
- `TestControllerGrpc.Core/Services/PipelineExecutorBase.cs` — `ExecuteEventTrackedAsync`, `ExecuteNodeTrackedAsync`, `ExecuteActionTrackedAsync`, `ExecuteInitialize`
- `TestControllerGrpc/Services/ActionPipelineExecutor.cs` — `ExecuteActionAsync` (smart retry)
- `TestControllerGrpc/Services/AgentGrpcDispatcher.cs` — `ExecuteRemoteCommandAsync`
- `TestControllerGrpc.Core/Services/RemoteCommandStreamRunner.cs` — `StreamAsync`

## Sequence

```mermaid
sequenceDiagram
	autonumber
	participant FS as FileSystemWatcher
	participant FW as FileWatcherManager
	participant LR as ILockRegistry
	participant PR as ParameterResolver
	participant EX as PipelineExecutorBase
	participant SM as ExecutionSessionManager
	participant APE as ActionPipelineExecutor
	participant D as AgentGrpcDispatcher
	participant SR as RemoteCommandStreamRunner
	participant A as TestAgent (gRPC)
	participant UI as Controller UI / Dashboard

	FS->>FW: file drop matches WatchItem.Filter
	FW->>SM: HasActiveExecution(wi.Tag)
	alt already running
		FW-->>FS: ignore trigger
	else free
		FW->>LR: TryAcquire(wi.Tag, owner, LockKind.Trigger)
		alt Conflict
			LR-->>FW: AcquireResult.Conflict
			FW-->>FS: ignore trigger
		else Success
			LR-->>FW: Lock.Token
			FW->>PR: LoadTriggerFile(ctx, fullPath)
			PR-->>FW: ctx.Parameters
			FW->>UI: TriggerFired / TriggerParametersLoaded
			FW->>EX: ExecuteEventTrackedAsync(tag, evt, ctx, ct)

			EX->>EX: DeepCloneNode(children) snapshot
			EX->>SM: BeginSession(tag, evt.Type, params, snapshot)
			SM-->>EX: ExecutionSession
			EX->>EX: CaptureLockToken + LockRenewalTimer

			loop each IActionNode (Sequential / Parallel)
				EX->>EX: ExecuteNodeTrackedAsync(node)

				alt InitializeConfig
					EX->>PR: Resolve(ParameterFile) + TryLoadInitializeSource
					PR-->>EX: null | failure
					opt failure
						EX->>EX: ctx.FatalError set, abort run
					end
				else ActionConfig
					EX->>SM: BeginAction(result "Running")
					SM->>UI: NodeProgress / SignalR
					EX->>PR: FindUnresolvedTokens(action, ctx)
					PR-->>EX: unresolved tokens
					alt tokens unresolved
						EX->>EX: Outcome = Failed (no dispatch)
					else resolved
						EX->>APE: ExecuteActionAsync(action, ctx, ct)
						APE->>PR: ResolveAction(action, ctx)
						PR-->>APE: resolved ActionConfig

						loop smart retry (MaxRetries, backoff)
							alt RunRemoteCommand
								APE->>D: ExecuteRemoteCommandAsync(action, ctx, ct)
								D->>PR: ResolveAction(action, ctx)
								D->>A: GetState / WaitForAgentFree
								A-->>D: AgentState
								D->>SR: StreamAsync(client, agent, resolved, linkedCt)
								SR->>A: RunCommandStreamed(request)
								loop streamed events
									A-->>SR: stdout / stderr / progress
									SR->>D: outputReceived(agent, line, kind)
									D->>UI: OutputReceived / StatusChanged
								end
								A-->>SR: final exit code
								SR-->>D: RemoteCommandStreamResult
								D-->>APE: ActionResult(Success, ExitCode, Error)
							else RunCommand (local)
								APE->>D: ExecuteLocalCommandAsync(action, ctx, ct)
								D-->>APE: ActionResult
							else SendMail
								APE->>APE: ExecuteSendMail(resolved, ctx)
							end
							opt failure and ShouldRetry
								APE->>APE: delay (fixed/exponential), next attempt
							end
						end

						APE-->>EX: bool success
						opt all attempts failed
							APE->>UI: OnNodeFailed(node, exitCode, error)
						end
					end
					EX->>SM: RecordResult(sessionId, ActionExecutionResult)
					SM->>UI: NodeProgress(Success/Failed) + session summary
				end
			end

			EX->>SM: CompleteSession(sessionId)
			EX->>LR: TryRelease(tag, lockToken)
			EX->>UI: LogEntry("Session", summary)
		end
	end
```

## Notes

- **Lock ownership is split.** The trigger path acquires the pipeline lock; the executor releases it in `finally` using the token threaded through `PipelineExecutionContext.LockToken`. `LockRenewalTimer` keeps the expiry sweeper from reaping it mid-run. A stale token never releases a newer acquisition's lock.
- **`ParameterResolver` is consulted three times** on the dispatch path: `ExecuteInitialize`, the `FindUnresolvedTokens` pre-flight in `ExecuteActionTrackedAsync`, and `ResolveAction` in both `ActionPipelineExecutor` and `AgentGrpcDispatcher`.
- **An unresolved token is a hard stop, not a dispatch.** Sending `[_Agent2]` literally to a shell fails far from the real cause, so the pre-flight fails the action first. An `Initialize` load failure sets `ctx.FatalError`, which overrides `FailAndContinue`.
- **Results return on two channels:** live streamed lines via `OutputReceived` / `StatusChanged`, and terminal state via `ExecutionSessionManager.BeginAction` / `RecordResult` — the latter carries the `SessionId` the dashboard routes on.
- **Two independent retry layers.** `ActionPipelineExecutor` owns smart retry (`MaxRetries`, fixed/exponential backoff, `RetryOnExitCodes`). `AgentGrpcDispatcher` separately owns the Polly resilience pipeline, busy-recovery with `ForceReady`, and the reboot circuit-breaker bypass.
- **Snapshot isolation:** `DeepCloneNode` clones the action tree at session start so a WatchList hot-reload cannot mutate in-flight nodes.
