using System.IO.Pipes;
using System.Security.Principal;
using StreamJsonRpc;
using VTD.Contracts;
using VTD.Contracts.Ipc;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Engine.Ipc;
using VTD.Reporting;
using VTD.Session;
using VTD.Testing.Fakes;

namespace VTD.Engine.Tests;

/// <summary>An <see cref="EngineCore"/> over the fakes (fixture scan, fake fix runner, in-memory state) with the real
/// session machine, runtime, redactor, exporter and plan validator.</summary>
public sealed class TestEngine : IAsyncDisposable
{
    private TestEngine(EngineCore core, FakeClock clock, FakeFixRunner runner, TestReboot reboot, string logFolder)
    {
        LogFolder = logFolder;
        Core = core;
        Clock = clock;
        FixRunner = runner;
        Reboot = reboot;
    }

    public EngineCore Core { get; }

    /// <summary>Folder of this engine's rolling log (deleted on dispose).</summary>
    public string LogFolder { get; }

    public FakeClock Clock { get; }

    public FakeFixRunner FixRunner { get; }

    public TestReboot Reboot { get; }

    public SequentialIdGenerator Ids => (SequentialIdGenerator)Core.Runtime.Services.Ids;

    public static async Task<TestEngine> StartAsync(PrivilegeMode privilege = PrivilegeMode.Elevated, string playerSid = "S-1-5-21-1-2-3-1001")
    {
        var clock = new FakeClock();
        var ids = new SequentialIdGenerator(clock);
        var kb = FakeKnowledgeBase.Sample();
        var runner = new FakeFixRunner(kb, clock, ids);
        var ledger = new RestartPromptLedger(clock);
        var reboot = new TestReboot(ledger, clock);
        var player = FakePlayer.Default with { Sid = playerSid };
        var services = new SessionServices
        {
            Kb = kb,
            Scanner = FakeScanService.FromFixture(Fixtures.RealtekStutter),
            Analyzer = new FakeAnalyzer(),
            FixRunner = privilege == PrivilegeMode.Elevated ? runner : null,
            Reboot = privilege == PrivilegeMode.Elevated ? reboot : null,
            Recovery = new FakeRecovery(clock),
            Clock = clock,
            Ids = ids,
            Player = () => player,
            Privilege = privilege,
        };
        var runtime = new SessionRuntime(services, new NotificationHub(clock), ledger);
        var store = new InMemoryStateStore<SessionState>();
        var (state, _) = SessionController.LoadOrCreate(store, () => SessionMachine.NewSession(ids.NewId(), clock.GetUtcNow(), privilege, kb.Version.Version, player.Sid));
        runner.SessionId = state.SessionId;
        var controller = new SessionController(state, store, runtime, clock, new SessionControllerOptions { InlineEffects = true });
        runtime.Attach(controller);
        var redactor = new Redactor([.. Enumerable.Range(1, 32).Select(i => (byte)i)], [CanaryTokens.UserName, CanaryTokens.ComputerName]);
        var logFolder = Path.Combine(Path.GetTempPath(), "vtd-engine-tests-" + Guid.NewGuid().ToString("N"));
        var shareText = new VTD.Contracts.Reporting.RedactionPolicy { Level = RedactionLevel.Share };
        var log = new VTD.Engine.Diagnostics.DiagLog(logFolder, clock, text => redactor.RedactText(text, shareText));
        var core = new EngineCore(new EngineCoreOptions
        {
            Log = log,
            DebugBundles = new VTD.Engine.Diagnostics.DebugBundleBuilder(redactor, redactor.ContainsKnownIdentifier, ids, clock),
            Controller = controller,
            Runtime = runtime,
            Redactor = redactor,
            Exporter = new ShareExporter(redactor, new ReportBuilder(kb, redactor), new ScanBundleCodec(redactor), ids, clock),
            PlanValidator = new PlanValidator(redactor),
            Player = player,
            EngineVersion = "0.1.0-test",
            AppVersion = "0.1.0-test",
            ProcessId = Environment.ProcessId,
        });
        await SessionStartup.RunAsync(controller, runtime, null, CancellationToken.None);
        return new TestEngine(core, clock, runner, reboot, logFolder);
    }

    public EngineConnection Connect(EventOrigin origin) => new(Core, Identity(origin));

    public static ClientIdentity Identity(EventOrigin origin) => new()
    {
        ProcessId = Environment.ProcessId,
        ImagePath = Environment.ProcessPath,
        UserSid = "S-1-5-21-1-2-3-1001",
        UserIsAdmin = true,
        Elevated = false,
        Signature = new SignatureCheck(SignatureState.Unsigned, null),
        Origin = origin,
    };

    public async ValueTask DisposeAsync()
    {
        await Core.DisposeAsync();
        try
        {
            Directory.Delete(LogFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp folder: best effort.
        }
    }

    public static ClientHello Hello(ClientKind kind = ClientKind.Ui, string? label = null)
        => new() { ProtocolVersion = EngineProtocol.Version, AppVersion = "0.1.0", Language = Languages.English, Kind = kind, Label = label };
}

/// <summary>A reboot coordinator that records instead of registering or restarting, with the real ledger gate.</summary>
public sealed class TestReboot(RestartPromptLedger ledger, TimeProvider clock) : IRebootCoordinator
{
    public BootIdentity Boot { get; set; } = new() { BootId = 7, LastBootUpTimeUtc = FakeClock.DefaultStart.AddHours(-1), Kind = BootKind.Cold };

    public bool Registered { get; private set; }

    public List<RestartToken> Restarts { get; } = [];

    public Fact<BootIdentity> CurrentBoot() => Fact.Known(Boot, "test");

    public Task<RebootReadiness> CheckReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(new RebootReadiness
    {
        OnAcPower = Tri.True,
        WindowsUpdateRebootPending = Tri.False,
        WindowsUpdateInstalling = Tri.False,
        FastStartupEnabled = Tri.True,
        TaskSchedulerAvailable = Tri.True,
        InteractiveUserIsAdmin = Tri.True,
        FirmwareRestartSupported = Tri.True,
        PlannedMechanism = ResumeMechanism.ScheduledTasks,
    });

    public Task<ResumeRegistration> RegisterResumeAsync(string sessionId, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        Registered = true;
        return Task.FromResult(new ResumeRegistration { Mechanism = ResumeMechanism.ScheduledTasks, Verified = true });
    }

    public Task UnregisterResumeAsync(CancellationToken cancellationToken)
    {
        Registered = false;
        return Task.CompletedTask;
    }

    public Task RestartAsync(RestartToken token, CancellationToken cancellationToken)
    {
        if (!ledger.TryConsume(token))
        {
            throw new InvalidOperationException("restart.token_invalid");
        }

        _ = clock;
        Restarts.Add(token);
        return Task.CompletedTask;
    }
}

/// <summary>A real named-pipe server over a <see cref="TestEngine"/>, with the production policy and identification; this
/// test process's image stands in for one VTDiag image (every other check still runs).</summary>
public sealed class PipeFixture : IAsyncDisposable
{
    private PipeFixture(PipeServer server, string name, ClientPolicy policy)
    {
        Server = server;
        Name = name;
        Policy = policy;
    }

    public PipeServer Server { get; }

    public string Name { get; }

    public ClientPolicy Policy { get; }

    public List<ClientIdentity> Decisions { get; } = [];

    public static string OwnImage => PipeClientVerifier.ImagePathOf(Environment.ProcessId) ?? throw new InvalidOperationException("own image unknown");

    public static string OwnSid
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User!.Value;
        }
    }

    public static PipeFixture Start(EngineCore core, EventOrigin asOrigin, string? playerSid = null, string? installDirectory = null)
    {
        var name = EngineProtocol.PipeNamePrefix + "test." + Guid.NewGuid().ToString("N");
        var policy = new ClientPolicy(installDirectory ?? Path.GetDirectoryName(OwnImage)!, playerSid ?? OwnSid, new SignatureCheck(SignatureState.Unsigned, null));
        policy.Images[Path.GetFileName(OwnImage)] = asOrigin;
        var server = new PipeServer(name, PipeServer.CreateSecurity(playerSid ?? OwnSid, limitedEngine: true), pipe => PipeServer.IdentifyClient(pipe, policy), id => new RpcSurface(new EngineConnection(core, id)));
        var fixture = new PipeFixture(server, name, policy);
        server.ClientDecided += (_, d) =>
        {
            lock (fixture.Decisions)
            {
                fixture.Decisions.Add(d);
            }
        };
        server.Start();
        return fixture;
    }

    public async Task<(IEngineRpc Proxy, JsonRpc Rpc, NamedPipeClientStream Pipe)> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, ct);
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new SystemTextJsonFormatter { JsonSerializerOptions = VtdJson.CreateRpcOptions() }));
        var proxy = rpc.Attach<IEngineRpc>();
        rpc.StartListening();
        return (proxy, rpc, pipe);
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
