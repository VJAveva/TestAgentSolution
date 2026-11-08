# Copilot Debugging Task: gRPC "Unexpected compressed flag value in message header"

## Your Role

You are debugging a production gRPC framing error in the TestAgentSolution
codebase. I have already done extensive diagnosis and RULED OUT several causes.
Your job is to find the remaining root cause **in the application code** and fix
it. Work methodically. Do NOT re-investigate the things already ruled out below.
Read the actual code before proposing fixes — do not guess.

---

## The Error

Repeatedly, on the controller, in the Windows Event Viewer:

```
Category: Grpc.AspNetCore.Server.ServerCallHandler  EventId: 6
RequestPath: /testagent.TestControllerService/PushExecutionEvents
Error when executing service method 'PushExecutionEvents'.

System.IO.InvalidDataException: Unexpected compressed flag value in message header.
   at Grpc.AspNetCore.Server.Internal.PipeExtensions.TryReadMessage(...)
   at Grpc.AspNetCore.Server.Internal.PipeExtensions.ReadStreamMessageAsync[T](...)
   at ...HttpContextStreamReader`1...
   at Grpc.Core.AsyncStreamReaderExtensions.ReadAllAsyncCore[T](...)
   at TestControllerGrpc.Services.TestControllerGrpcService.PushExecutionEvents(
        IAsyncStreamReader`1 requestStream, ServerCallContext context)
        in ...\TestControllerGrpc\Services\TestControllerGrpcService.cs:line 82
```

There is also a mirror-image error on the controller's CLIENT side:

```
Category: TestController.WebApi.Services.AgentEventRelayService
Error subscribing to agent warmgr events
Grpc.Core.RpcException: Status(StatusCode="Internal",
  Detail="Error reading next message. InvalidDataException: Unexpected
  compressed flag value in message header.")
   at Grpc.Net.Client.Internal.StreamExtensions.ReadCompressedFlag(Byte flag)
   at ...AgentEventRelayService.SubscribeToAgentAsync(AgentEntry agent, ...)
        in ...\TestController.WebApi\Services\AgentEventRelayService.cs:line 73
```

And downstream (secondary, NOT the root cause — these are the connection
tearing down AFTER the framing error):

```
Agent at ipv6:[::ffff:10.48.190.254]:65211 disconnected unexpectedly.
System.IO.IOException: The client reset the request stream.
```

---

## What "Unexpected compressed flag value" Means (context for your analysis)

Every gRPC message frame is prefixed with a 5-byte header:

```
[1 byte: compression flag (must be 0x00 or 0x01)][4 bytes: big-endian length][payload]
```

The reader read a first byte that was NEITHER 0 nor 1. This means the 5-byte
framing is MISALIGNED — the reader is looking at a byte that is not actually the
start of a frame. This is a **wire desynchronization**: the writer and reader
disagree about where one message ends and the next begins. It is a
transport-framing problem, NOT a protobuf/deserialization problem (it fails in
`TryReadMessage`, BEFORE any message deserialization).

---

## ALREADY RULED OUT — do NOT re-investigate these

| Hypothesis | Why it's ruled out |
|---|---|
| Network middlebox / cross-subnet path corruption | Same-subnet agents (10.48.190.x) and agent `warmgr` fail with the identical error. Their traffic never crosses any transit hop. Not the network. |
| Proto/contract version skew | Error occurs at `TryReadMessage` (framing), BEFORE deserialization. Proto skew would fail during deserialization with a different error. |
| h2c / TLS mismatch | Agent config confirmed correct: `ControllerAddress: http://jvgr22:5100`, `AgentEndpoint: http://...:5200`, Kestrel `Protocols: Http2`, `WarnOnPlaintextHttp2: true`. Plaintext h2c on both ends. |
| gRPC DLL version mismatch | The `Grpc.*` and `Google.Protobuf.dll` versions were compared on controller and agent — they MATCH. |

---

## KEY CLUE — this must drive your investigation

**Every single error is on `PushExecutionEvents`. NONE on `Register` or
`Heartbeat`.**

- `Register` and `Heartbeat` are UNARY calls (one request, one response). They
  never fail.
- `PushExecutionEvents` is a CLIENT-STREAMING call (agent opens one long-lived
  stream and writes many `ExecutionEvent` messages over time). It ALWAYS fails.

This pattern is decisive. Wire desynchronization on a streaming call — but never
on unary calls — points at **how the stream is being written**. The most common
cause of exactly this symptom in gRPC is:

> **Concurrent writes to a single client stream.** gRPC client streams are NOT
> thread-safe for concurrent writes. If two threads call `WriteAsync` on the
> same stream/`IClientStreamWriter` without serialization, their writes
> interleave, the 5-byte framing is corrupted, and the reader sees an invalid
> compression flag.

This is the LEADING hypothesis. It also explains why the error is intermittent
and fleet-wide (it's timing/race dependent).

---

## Your Investigation — do these in order

### STEP 1: Find and analyze the agent's PushExecutionEvents WRITER (highest priority)

Locate the code in the AGENT (`TestAgentGrpc`) that opens the
`PushExecutionEvents` stream and writes `ExecutionEvent` messages to it.

Search for:
- The call that opens the stream (e.g. `client.PushExecutionEvents(...)`)
- All `.WriteAsync(...)` / `.RequestStream.WriteAsync(...)` calls on that stream
- The `IClientStreamWriter` or call object it writes to

**Determine: can more than one thread reach that `WriteAsync` on the SAME stream
at the same time?** Look specifically for:
- A shared stream/call object stored in a field and written from multiple places
- Event handlers, callbacks, timers, or `Task.Run` / parallel loops that each
  push events to the same stream
- Parallel test-action execution where each action reports events concurrently
- ANY `WriteAsync` that is not protected by a single-writer mechanism
  (`SemaphoreSlim`, `lock` around an async-safe path, or a producer/consumer
  `Channel<T>` with ONE draining writer)

Report exactly what you find:
- The file and method that owns the stream
- Every code path that writes to it
- Whether those paths can execute concurrently
- Whether any synchronization currently exists

### STEP 2: Read the controller's READER at the exact failure point

Open `TestControllerGrpc\Services\TestControllerGrpcService.cs` around **line
82** (the `PushExecutionEvents` handler / the `ReadAllAsync` loop). Confirm:
- How it reads the stream (`requestStream.ReadAllAsync()` or `MoveNext` loop)
- That the reader itself isn't doing anything unusual (it shouldn't be — the
  corruption originates on the writer side, but confirm)

Also open `TestController.WebApi\Services\AgentEventRelayService.cs` around
**line 73** — this is the controller acting as a CLIENT subscribing to agent
events. Note: the agent is ALSO a gRPC server streaming events back. So there
may be a SECOND streaming path (controller-subscribes-to-agent) with the SAME
concurrent-write bug on the AGENT's server side. Check whether the agent writes
that response stream from multiple threads too.

### STEP 3: Check for interceptors / compression configuration

In the controller's gRPC registration (look for `AddGrpc(...)`, likely in
`ControllerGrpcServerHost.cs` or `Program.cs`) and the agent's gRPC setup, check
for:
- `ResponseCompressionAlgorithm` / `CompressionProviders` set on one side but
  not the other
- Any custom `Interceptors` that read/write or wrap the request/response stream
- Any custom message/serializer configuration

A compression setting mismatch OR an interceptor that mishandles the stream can
also desync framing. Confirm both ends agree (or that compression is off on
both).

### STEP 4: Check stream lifecycle / error handling on the writer

In the agent's writer (from Step 1), check:
- Is the stream reused across sessions/runs, or freshly created each time?
- On a mid-stream error, does the code keep writing to a half-broken stream?
- Is `CompleteAsync()` called correctly exactly once when done?
- Could two overlapping sessions share one stream instance?

---

## The Fix (apply once you've confirmed the cause)

### If STEP 1 confirms concurrent writes (most likely)

Serialize ALL writes to the stream through a single writer. The clean pattern is
a `System.Threading.Channels.Channel<ExecutionEvent>`: every producer writes to
the channel (thread-safe), and ONE dedicated task drains the channel and is the
ONLY thing that ever calls `WriteAsync` on the gRPC stream.

Implement it roughly like this (adapt to the real class/types):

```csharp
// Single unbounded (or bounded) channel of events to send
private readonly Channel<ExecutionEvent> _outbound =
    Channel.CreateUnbounded<ExecutionEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,     // exactly one drainer
        SingleWriter = false     // many producers allowed
    });

// PRODUCERS (any thread) — never touch the gRPC stream directly:
public void EnqueueEvent(ExecutionEvent e) => _outbound.Writer.TryWrite(e);

// SINGLE WRITER LOOP — the ONLY code that calls stream.WriteAsync:
private async Task PumpAsync(
    IClientStreamWriter<ExecutionEvent> stream, CancellationToken ct)
{
    try
    {
        while (await _outbound.Reader.WaitToReadAsync(ct))
        {
            while (_outbound.Reader.TryRead(out var e))
            {
                await stream.WriteAsync(e);   // serialized: one writer only
            }
        }
        await stream.CompleteAsync();
    }
    catch (Exception ex)
    {
        // log; do NOT keep writing to a broken stream
    }
}
```

Key guarantees this provides:
- Only ONE thread ever calls `WriteAsync` -> framing can never desync
- Producers are decoupled and thread-safe via the channel
- Backpressure is possible (use a bounded channel with `DropOldest` if you want
  to match the existing EventBroadcaster backpressure pattern)

If there is a simpler existing single-writer construct, a `SemaphoreSlim(1,1)`
awaited around every `WriteAsync` also works, but the channel approach is
cleaner and matches the codebase's existing Channel-based EventBroadcaster
pattern.

### If STEP 3 finds a compression/interceptor mismatch

Align both ends: either disable gRPC compression on both, or ensure both use the
same provider. Remove or fix any interceptor that touches raw stream bytes.

### Also: fix the error MASKING so future issues are visible

Separately, the controller handler currently only catches `IOException` and
`OperationCanceledException`; other exceptions surface to the agent as a generic
"Exception was thrown by handler." Add a broad catch that LOGS the real
exception server-side before rethrowing, so the true error is always in the
controller log:

```csharp
catch (Exception ex)
{
    _logger.LogError(ex,
        "PushExecutionEvents failed for {Peer}", context.Peer);
    throw;
}
```

Also confirm whether `EnableDetailedErrors` should be enabled in a controlled
way for diagnostics (note: it exposes server stack traces to clients — use only
temporarily, then revert).

---

## Deliverables I want from you

1. **Root cause**: the exact file/method and code path that desyncs the stream,
   with the concurrent-write paths named (or the interceptor/compression issue
   if that's what you find instead).
2. **The fix**: concrete code changes (single-writer serialization or config
   alignment), applied to the real classes.
3. **Reader-side hardening**: add the logging catch so this can't be masked
   again.
4. **Verification plan**: how to prove the fix works (e.g., run parallel
   execution / multiple agents pushing events concurrently and confirm zero
   "compressed flag" errors over a sustained run).

---

## Constraints

- Do NOT reintroduce the ruled-out theories.
- Read the ACTUAL code at the cited lines before proposing changes.
- Preserve existing behavior except for the framing fix and the logging.
- Follow the codebase conventions (it already uses Channel-based broadcasting
  with DropOldest backpressure elsewhere — be consistent with that).
- Client streams must have exactly ONE writer. That invariant is the core of the
  fix.

---

## Summary of the reasoning chain (so you understand WHY)

1. Framing desync error (`compressed flag`) = writer/reader disagree on message
   boundaries.
2. It happens ONLY on the streaming call (`PushExecutionEvents`), never on unary
   calls.
3. Network, proto, h2c, and DLL versions are all ruled out.
4. The remaining cause is in application code, in the streaming write path.
5. gRPC client streams are not thread-safe for concurrent writes; concurrent
   `WriteAsync` corrupts framing exactly this way.
6. Therefore: find the concurrent writes to the stream and serialize them
   through a single writer.

Start with STEP 1. Show me the agent's PushExecutionEvents writer code and your
analysis of whether concurrent writes are possible.
