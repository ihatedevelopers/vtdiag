using VTD.Contracts;
using VTD.Contracts.Ipc;
using VTD.Contracts.Journaling;
using VTD.Contracts.Sessions;
using VTD.Session;
using VTD.Testing.Fakes;

namespace VTD.Session.Tests.Sim;

/// <summary>One engine process over the simulated PC (the disk and the world outlive it).</summary>
public sealed class SimEngine
{
    private SimEngine(SimWorld world, int generation, CrashingStore store, SessionRuntime runtime, SessionController controller)
    {
        World = world;
        Generation = generation;
        Store = store;
        Runtime = runtime;
        Controller = controller;
    }

    public SimWorld World { get; }

    public int Generation { get; }

    public CrashingStore Store { get; }

    public SessionRuntime Runtime { get; }

    public SessionController Controller { get; }

    public SessionState State => Controller.Current;

    public ConsentRequestedNotification? LastConsentRequest { get; private set; }



    /// <summary>Starts a process: load (or create) the session, then the start-up order (resume, recovery, expiry).</summary>
    public static async Task<(SimEngine Engine, StartupOutcome Outcome)> StartAsync(SimWorld world, InMemoryStateStore<SessionState> disk, FakeClock clock, IIdGenerator ids, string? resumeSessionId, SaveCounter counter, Dictionary<string, int> resumeWork)
    {
        var generation = world.NewProcess();
        var store = new CrashingStore(disk, world, generation, counter);
        var kb = FakeKnowledgeBase.Sample();
        var ledger = new RestartPromptLedger(clock);
        var services = new SessionServices
        {
            Kb = kb,
            Scanner = FakeScanService.FromFixture(Fixtures.RealtekStutter),
            Analyzer = new FakeAnalyzer(),
            FixRunner = new SimFixRunner(world, generation, kb, clock, ids),
            Reboot = new SimReboot(world, generation, ledger),
            Recovery = new SimRecovery(world, generation, clock),
            Monitor = new FakeMonitorService(clock, ids) { TickCount = 2 },
            Clock = clock,
            Ids = ids,
            Player = () => FakePlayer.Default,
            Privilege = PrivilegeMode.Elevated,
        };
        var runtime = new SessionRuntime(services, new NotificationHub(clock), ledger);
        var (state, _) = SessionController.LoadOrCreate(store, () => SessionMachine.NewSession(ids.NewId(), clock.GetUtcNow(), PrivilegeMode.Elevated, kb.Version.Version, FakePlayer.Default.Sid));
        var controller = new SessionController(state, store, runtime, clock, new SessionControllerOptions { InlineEffects = true });
        runtime.Attach(controller);
        var engine = new SimEngine(world, generation, store, runtime, controller);
        controller.Changed += (_, s) =>
        {
            world.Seen.Add("phase:" + s.Phase.ToString());
            if (s.NeedsAttentionReasonKey is { } reason && s.Phase == Phase.NeedsAttention)
            {
                world.Seen.Add("reason:" + reason);
            }

            if (s.LastErrorCode is { } error)
            {
                world.Seen.Add("error:" + error);
            }

            if (s.Phase == Phase.Resuming)
            {
                // The budget is per restart request (a click), keyed by the request time; each resume run has its own
                // counter value (several persisted states of one run share it).
                var key = s.SessionId + "@" + (s.Restart?.RequestedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "-");
                var run = key + "#" + s.Resume.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (resumeWork.TryAdd(run, 0))
                {
                    resumeWork[key] = resumeWork.GetValueOrDefault(key) + 1;
                }
            }
        };
        runtime.Hub.Raised += (_, n) =>
        {
            if (n is ConsentRequestedNotification c)
            {
                engine.LastConsentRequest = c;
            }
        };
        var outcome = await SessionStartup.RunAsync(controller, runtime, resumeSessionId, CancellationToken.None);
        await controller.WhenIdleAsync();
        return (engine, outcome);
    }

    public async Task<SessionState> SendAsync(SessionCommand command)
    {
        try
        {
            return await Controller.HandleAsync(command, CancellationToken.None);
        }
        finally
        {
            // A refusal can still have effects (an expired session is closed, then the command is refused).
            await Controller.WhenIdleAsync();
        }
    }
}
