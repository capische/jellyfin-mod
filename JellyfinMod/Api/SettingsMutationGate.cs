namespace JellyfinMod.Api;

/// <summary>
/// Serializes the administrator's revisioned settings changes in this process (whole-review chunk 3a P2 3, 3b P2 3). Each
/// change reads its record's revision only after taking the gate and saves before releasing it, so of two changes made
/// from the same revision one is saved and the other meets the new revision and answers 409, instead of both saving the
/// same next revision and the later silently overwriting the earlier. Background work never takes it.
/// </summary>
internal static class SettingsMutationGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Waits for the gate; dispose the result to release it.</summary>
    public static async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease();
    }

    private sealed class Lease : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
