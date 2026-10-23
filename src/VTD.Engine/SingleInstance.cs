namespace VTD.Engine;

/// <summary>
/// One engine at a time: holds a named mutex on its own thread. A <see cref="Mutex"/> belongs to the thread that took it,
/// and the engine awaits (and so changes threads) while it owns it, so releasing it inline would throw
/// <see cref="ApplicationException"/> on the way out. Here the same thread takes it and gives it back on dispose.
/// An abandoned mutex (the previous engine died) counts as acquired; startup recovery handles the rest.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _release;

    private SingleInstance(Thread thread, ManualResetEventSlim release)
    {
        _thread = thread;
        _release = release;
    }

    /// <summary>The mutex, or null when another process holds it.</summary>
    /// <param name="name">Mutex name (<c>Global\VTDiag.Engine</c> or <c>Local\VTDiag.Engine.Limited</c>).</param>
    public static SingleInstance? TryAcquire(string name)
    {
        var release = new ManualResetEventSlim(false);
        using var decided = new ManualResetEventSlim(false);
        var owned = false;
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                owned = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            decided.Set();
            if (owned)
            {
                release.Wait();
                mutex.ReleaseMutex();
            }
        })
        {
            IsBackground = true,
            Name = "VTDiag.SingleInstance",
        };
        thread.Start();
        decided.Wait();
        if (!owned)
        {
            thread.Join();
            release.Dispose();
            return null;
        }

        return new SingleInstance(thread, release);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _release.Set();
        _thread.Join();
        _release.Dispose();
    }
}
