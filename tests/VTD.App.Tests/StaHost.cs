using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using VTD.App.Localization;
using VTD.Contracts;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VTD.App.Tests;

/// <summary>
/// One dedicated STA thread with a running dispatcher and the app's <see cref="System.Windows.Application"/> (so theme
/// resources and DataTemplates resolve). Every UI test runs its body there, one at a time.
/// </summary>
internal static class StaHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<Dispatcher> UiDispatcher = new(Start);

    /// <summary>Runs an async body on the UI thread and waits for it.</summary>
    /// <param name="body">Body.</param>
    public static async Task RunAsync(Func<Task> body)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await UiDispatcher.Value.InvokeAsync(async () =>
            {
                try
                {
                    Loc.Instance.Language = Languages.English;
                    await body().ConfigureAwait(true);
                    done.SetResult();
                }
#pragma warning disable CA1031 // forwarded to the test
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    done.SetException(ex);
                }
            });
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var app = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "VTD.App.Tests UI",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return dispatcher!;
    }
}
