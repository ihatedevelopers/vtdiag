using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Sessions;
using VTD.Fixes.Runner;
using VTD.Testing.Fakes;

namespace VTD.Fixes.Tests.Fakes;

/// <summary>A complete runner on a fake PC: clock, ids, KB, fake system, probe, journal, restore points.</summary>
public sealed class Rig : IDisposable
{
    /// <summary>Session id used by the rig.</summary>
    public const string Session = "01JB6R7X3K9M2N4P5Q6R7S8TB0";

    private Rig(IReadOnlyList<FixDef> fixes, IJournal? journal, FakeSystem? system)
    {
        Clock = new FakeClock();
        Ids = new SequentialIdGenerator(Clock);
        System = system ?? FakeSystem.Typical(Clock);
        Context = new FixSessionContext(Session, "0.2.0-test") { Player = FakePlayer.Default, IsElevated = true, BootId = 42 };
        Probe = FakeFactProbe.ReadyToFix();
        Journal = journal ?? new InMemoryJournal();
        RestorePoints = new FakeRestorePointService(Clock);
        Kb = new FakeKnowledgeBase { Fixes = fixes };
        Registry = new OperationRegistry(System.Ports, Context, Clock, Probe);
        Runner = new FixRunner(Kb, Registry, Journal, Probe, RestorePoints, Context, Clock, Ids);
    }

    /// <summary>Clock.</summary>
    public FakeClock Clock { get; }

    /// <summary>Ids.</summary>
    public SequentialIdGenerator Ids { get; }

    /// <summary>The fake PC.</summary>
    public FakeSystem System { get; }

    /// <summary>Session context.</summary>
    public FixSessionContext Context { get; }

    /// <summary>Live probe.</summary>
    public FakeFactProbe Probe { get; }

    /// <summary>Journal.</summary>
    public IJournal Journal { get; }

    /// <summary>Restore points.</summary>
    public FakeRestorePointService RestorePoints { get; }

    /// <summary>KB.</summary>
    public FakeKnowledgeBase Kb { get; }

    /// <summary>Primitives.</summary>
    public OperationRegistry Registry { get; }

    /// <summary>Runner.</summary>
    public FixRunner Runner { get; }

    /// <summary>The UI as proposer.</summary>
    public static Proposer Ui { get; } = new() { Origin = EventOrigin.Ui };

    /// <summary>Creates a rig.</summary>
    /// <param name="fixes">KB fixes.</param>
    public static Rig Create(params FixDef[] fixes) => new(fixes, null, null);

    /// <summary>Creates a rig with a given journal and/or system.</summary>
    /// <param name="journal">Journal.</param>
    /// <param name="system">System.</param>
    /// <param name="fixes">KB fixes.</param>
    public static Rig With(IJournal? journal, FakeSystem? system, params FixDef[] fixes) => new(fixes, journal, system);

    /// <summary>A second runner over the same journal and PC (an engine restart).</summary>
    public FixRunner Restart() => new(Kb, Registry, Journal, Probe, RestorePoints, Context, Clock, Ids);

    /// <summary>The token the UI mints for a preview.</summary>
    /// <param name="p">Preview.</param>
    /// <param name="second">Second-consent nonce.</param>
    public ConsentToken Token(FixPreview p, string? second = null) => new()
    {
        TokenId = Ids.NewId(),
        SessionId = Session,
        PreviewId = p.PreviewId,
        FixId = p.FixId,
        BindingHash = p.BindingHash,
        PreviewHash = p.PreviewHash,
        Nonce = p.ConsentNonce!,
        IssuedUtc = Clock.GetUtcNow(),
        SecondConsentNonce = second,
    };

    /// <summary>Preview then apply with consent (and the second click for one-way fixes).</summary>
    /// <param name="fixId">Fix.</param>
    /// <param name="binding">Binding.</param>
    public async Task<(FixPreview Preview, FixRunResult Result)> ApplyAsync(string fixId, FixBinding? binding = null)
    {
        var p = await Runner.PreviewAsync(fixId, binding ?? FixBinding.Empty, Ui, CancellationToken.None);
        Assert.True(p.CanApply, fixId + " blocked: " + string.Join(", ", p.BlockReasonKeys));
        var r = await Runner.ApplyAsync(Token(p), null, CancellationToken.None);
        if (r.Status == AttemptStatus.AwaitingSecondConsent)
        {
            r = await Runner.ApplyAsync(Token(p, r.SecondConsentNonce), null, CancellationToken.None);
        }

        return (p, r);
    }

    /// <summary>All journal records.</summary>
    public async Task<IReadOnlyList<JournalRecord>> RecordsAsync()
    {
        var list = new List<JournalRecord>();
        await foreach (var r in Journal.ReadAllAsync(CancellationToken.None))
        {
            list.Add(r);
        }

        return list;
    }

    /// <summary>A binding of one name.</summary>
    /// <param name="name">Binding name.</param>
    /// <param name="kind">Kind.</param>
    /// <param name="ids">Entity ids.</param>
    public static FixBinding Bind(string name, EntityKind kind, params string[] ids)
        => new() { Entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal) { [name] = [.. ids.Select(i => new EntityRef { Kind = kind, Id = i, DisplayName = i })] } };

    /// <inheritdoc />
    public void Dispose() => Runner.Dispose();
}

/// <summary>Fix definitions for runner tests (mirrors of the KB shapes).</summary>
public static class TestFixes
{
    /// <summary>A one-step registry fix (HAGS off: reboot required).</summary>
    public static FixDef HagsOff { get; } = new()
    {
        Id = "fix.display.hags_off",
        TitleKey = "t",
        ExplainKey = "e",
        UndoExplainKey = "u",
        Risk = RiskLevel.Low,
        Reversibility = Reversibility.Exact,
        Reboot = RebootNeed.Required,
        Steps = [new StepDef { Op = OpIds.RegistrySetValue, Args = Map(("path", @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"), ("name", "HwSchMode"), ("kind", "DWord"), ("data", "1")) }],
        Verify = [new VerifyDef { Probe = OpIds.RegistrySetValue, Args = Map(("path", @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"), ("name", "HwSchMode")), Expect = Map(("data", "1")) }],
    };

    /// <summary>Three registry steps (mouse acceleration off).</summary>
    public static FixDef MouseAccelOff { get; } = new()
    {
        Id = "fix.input.mouse_accel_off",
        TitleKey = "t",
        ExplainKey = "e",
        UndoExplainKey = "u",
        Risk = RiskLevel.Low,
        Reversibility = Reversibility.Exact,
        Admin = false,
        Steps =
        [
            new StepDef { Op = OpIds.RegistrySetValue, Args = Map(("path", @"HKCU\Control Panel\Mouse"), ("name", "MouseSpeed"), ("kind", "String"), ("data", "0")) },
            new StepDef { Op = OpIds.RegistrySetValue, Args = Map(("path", @"HKCU\Control Panel\Mouse"), ("name", "MouseThreshold1"), ("kind", "String"), ("data", "0")) },
            new StepDef { Op = OpIds.RegistrySetValue, Args = Map(("path", @"HKCU\Control Panel\Mouse"), ("name", "MouseThreshold2"), ("kind", "String"), ("data", "0")) },
        ],
        Verify = [new VerifyDef { Probe = OpIds.RegistrySetValue, Args = Map(("path", @"HKCU\Control Panel\Mouse"), ("name", "MouseSpeed")), Expect = Map(("data", "0")) }],
        Drift = new DriftDef { Watch = true, ReasonKey = "drift.setting_reverted" },
    };

    /// <summary>Disable conflicting services (pick all; start type then stop).</summary>
    public static FixDef DisableConflictService { get; } = new()
    {
        Id = "fix.svc.disable_conflict_service",
        TitleKey = "t",
        ExplainKey = "e",
        UndoExplainKey = "u",
        Risk = RiskLevel.Low,
        Reversibility = Reversibility.Exact,
        Template = true,
        Bind = new Dictionary<string, BindDef>(StringComparer.Ordinal) { ["svc"] = new() { Kind = EntityKind.Service, FromSignal = "conflict.*.service_present", Pick = BindPick.All } },
        Steps =
        [
            new StepDef { Op = OpIds.ServiceSetStartType, Args = Map(("name", "$svc.id"), ("startType", "Disabled")) },
            new StepDef { Op = OpIds.ServiceStop, Args = Map(("name", "$svc.id"), ("timeoutSec", "20")), OnFail = StepFailure.Continue },
        ],
        Verify = [new VerifyDef { Probe = OpIds.ServiceSetStartType, Args = Map(("name", "$svc.id")), Expect = Map(("startType", "Disabled")) }],
        Drift = new DriftDef { Watch = true },
    };

    /// <summary>Disable a secondary adapter (deadman).</summary>
    public static FixDef DisableSecondAdapter { get; } = new()
    {
        Id = "fix.net.disable_second_adapter",
        TitleKey = "t",
        ExplainKey = "e",
        UndoExplainKey = "u",
        Risk = RiskLevel.Low,
        Reversibility = Reversibility.Exact,
        Bind = new Dictionary<string, BindDef>(StringComparer.Ordinal) { ["adapter"] = new() { Kind = EntityKind.Adapter, FromSignal = "net.secondary_adapter_up" } },
        Guards =
        [
            new GuardDef { Probe = GuardIds.AdapterNotDefaultRoute, Args = Map(("instanceId", "$adapter.id")), FailKey = "guard.adapter_is_active" },
            new GuardDef { Probe = GuardIds.OtherAdapterOnline, Args = Map(("exceptInstanceId", "$adapter.id")), FailKey = "guard.no_other_connection" },
        ],
        Steps = [new StepDef { Op = OpIds.DeviceDisable, Args = Map(("instanceId", "$adapter.id")) }],
        Verify = [new VerifyDef { Probe = OpIds.DeviceDisable, Args = Map(("instanceId", "$adapter.id")), Expect = Map(("enabled", "false")) }],
        Deadman = new DeadmanDef { Seconds = 60, Check = GuardIds.InternetReachable },
    };

    /// <summary>Winsock reset (one-way, medium, reboot, deadman).</summary>
    public static FixDef WinsockReset { get; } = new()
    {
        Id = "fix.net.winsock_reset",
        TitleKey = "t",
        ExplainKey = "e",
        UndoExplainKey = "u",
        Risk = RiskLevel.Medium,
        Reversibility = Reversibility.OneWay,
        Reboot = RebootNeed.Required,
        Steps = [new StepDef { Op = OpIds.OneWayWinsockReset }],
        Deadman = new DeadmanDef { Seconds = 60 },
    };

    /// <summary>Close overlays (session scope, continue on failure).</summary>
    public static FixDef CloseOverlays { get; } = new()
    {
        Id = "fix.proc.close_overlays",
        TitleKey = "t",
        ExplainKey = "e",
        UndoExplainKey = "u",
        Risk = RiskLevel.Low,
        Reversibility = Reversibility.SessionOnly,
        Admin = false,
        Scope = FixScope.Session,
        Bind = new Dictionary<string, BindDef>(StringComparer.Ordinal) { ["proc"] = new() { Kind = EntityKind.Process, FromSignal = "proc.overlay_running", Pick = BindPick.All } },
        Steps = [new StepDef { Op = OpIds.ProcessCloseGraceful, Args = Map(("imageName", "$proc.id")), OnFail = StepFailure.Continue }],
        Verify = [new VerifyDef { Probe = OpIds.ProcessCloseGraceful, Args = Map(("imageName", "$proc.id")), Expect = Map(("running", "false")) }],
    };

    /// <summary>A string map.</summary>
    /// <param name="pairs">Pairs.</param>
    public static IReadOnlyDictionary<string, string> Map(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}
