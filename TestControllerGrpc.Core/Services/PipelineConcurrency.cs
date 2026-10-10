namespace TestControllerGrpc.Services;

/// <summary>
/// Process-wide cap on how many pipeline actions are dispatched at once.
/// </summary>
/// <remarks>
/// <para>
/// The old cap was a per-call <see cref="SemaphoreSlim"/> created inside each fan-out loop, so it
/// bounded one group and nothing more: two concurrent 50-node stages put 100 actions in flight.
/// </para>
/// <para>
/// The permit is taken around a single ACTION, deliberately NOT around a group's fan-out loop.
/// A global gate on the fan-out would deadlock on nested parallel groups - every permit would be
/// held by an outer child that is itself waiting for its own children to get one. An action is a
/// leaf and never waits on another permit, so this ordering cannot deadlock.
/// </para>
/// </remarks>
public static class PipelineConcurrency
{
    /// <summary>Unchanged from the constant this replaced, so default behaviour is identical.</summary>
    public const int DefaultMaxConcurrentActions = 50;

    public const string ConfigKey = "Controller:MaxConcurrentActions";

    private static SemaphoreSlim _gate = new(DefaultMaxConcurrentActions, DefaultMaxConcurrentActions);
    private static int _max = DefaultMaxConcurrentActions;

    public static int MaxConcurrentActions => Volatile.Read(ref _max);

    /// <summary>
    /// Host opt-in, called once at startup before any run. A value below 1 clamps to 1 - a cap of 0
    /// would wedge every pipeline forever.
    /// </summary>
    public static void Configure(int max)
    {
        var clamped = Math.Max(1, max);
        if (clamped == MaxConcurrentActions) return;

        Interlocked.Exchange(ref _max, clamped);
        Interlocked.Exchange(ref _gate, new SemaphoreSlim(clamped, clamped));
    }

    /// <summary>Waits for a dispatch slot. Dispose the result to return it.</summary>
    public static async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        // Captured, not re-read on release: a Configure() between wait and release would otherwise
        // return the permit to a different semaphore and permanently inflate the new one.
        var gate = Volatile.Read(ref _gate);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Permit(gate);
    }

    private sealed class Permit(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _held = gate;

        public void Dispose() => Interlocked.Exchange(ref _held, null)?.Release();
    }
}
