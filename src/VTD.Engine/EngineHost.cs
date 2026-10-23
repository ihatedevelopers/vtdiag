using System.Security.Principal;
using VTD.Contracts;
using VTD.Contracts.Ipc;
using VTD.Contracts.Sessions;
using VTD.Engine.Ipc;
using VTD.Engine.Platform;
using VTD.Session;

namespace VTD.Engine;

/// <summary>Parsed engine command line (everything except <c>--cli</c>).</summary>
public sealed record EngineArguments
{
    /// <summary><c>--limited</c>: run unelevated as the player (UAC declined).</summary>
    public bool Limited { get; init; }

    /// <summary><c>--resume &lt;sessionId&gt;</c> from the logon task.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary><c>--pipe &lt;name&gt;</c>: the pipe name the launching UI chose (must start with the protocol prefix).</summary>
    public string? PipeName { get; init; }

    /// <summary><c>--kb &lt;folder&gt;</c>: bundled KB folder (default: <c>kb</c> next to the engine).</summary>
    public string? KbPath { get; init; }

    /// <summary><c>--uninstall</c>: remove the resume tasks / RunOnce entry and the discovery file (the uninstaller).</summary>
    public bool Uninstall { get; init; }

    /// <summary>Copy this portable folder to Program Files and exit (<see cref="SelfInstall"/>).</summary>
    public bool Install { get; init; }

    /// <summary><c>--no-network</c>: no clock-skew or ping probes.</summary>
    public bool NoNetwork { get; init; }

    /// <summary>Parses; null with an error code when the arguments are invalid.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="error">Error code.</param>
    public static EngineArguments? Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        var result = new EngineArguments();
        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i])
            {
                case "--limited":
                    result = result with { Limited = true };
                    break;
                case "--resume":
                    var id = Next();
                    if (!Ulid.IsValid(id))
                    {
                        error = "args.resume_id";
                        return null;
                    }

                    result = result with { ResumeSessionId = id };
                    break;
                case "--pipe":
                    var pipe = Next();
                    if (pipe is null || !pipe.StartsWith(EngineProtocol.PipeNamePrefix, StringComparison.Ordinal) || pipe.Length > 96
                        || !pipe.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
                    {
                        error = "args.pipe";
                        return null;
                    }

                    result = result with { PipeName = pipe };
                    break;
                case "--kb":
                    var kb = Next();
                    if (kb is null)
                    {
                        error = "args.kb";
                        return null;
                    }

                    result = result with { KbPath = kb };
                    break;
                case "--uninstall":
                    result = result with { Uninstall = true };
                    break;
                case "--install":
                    result = result with { Install = true };
                    break;
                case "--no-network":
                    result = result with { NoNetwork = true };
                    break;
                default:
                    error = "args.unknown";
                    return null;
            }
        }

        return result;
    }
}

/// <summary>
/// The engine process (DESIGN.md section 2): refuses to run elevated unless its own image is under
/// <c>%ProgramFiles%</c>; one elevated engine at a time (<c>Global\VTDiag.Engine</c>); start-up order (resume first,
/// then recovery) BEFORE the pipe accepts anything; the pipe; the discovery file; idle exit after
/// <see cref="EngineProtocol.IdleExit"/> with no client and nothing running.
/// </summary>
public static class EngineHost
{
    /// <summary>Exit code: refused to run (elevated outside Program Files).</summary>
    public const int ExitRefused = 3;

    /// <summary>True when this process token is elevated (Administrators enabled).</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>True when <paramref name="imagePath"/> is inside <c>%ProgramFiles%</c> and no path component is a reparse point.</summary>
    /// <param name="imagePath">Image.</param>
    public static bool IsUnderProgramFiles(string? imagePath)
    {
        if (imagePath is null)
        {
            return false;
        }

        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(imagePath);
        return programFiles.Length > 3
               && full.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)
               && !EngineStorage.HasReparsePoint(Path.GetDirectoryName(full)!);
    }

    /// <summary>The bundled KB folder: <c>--kb</c>, else <c>kb</c> next to the engine, else (unelevated dev runs only) the repository's.</summary>
    /// <param name="explicitPath">The <c>--kb</c> argument.</param>
    /// <param name="elevated">Elevated.</param>
    public static string? FindKb(string? explicitPath, bool elevated)
    {
        if (explicitPath is not null)
        {
            return elevated ? null : Path.GetFullPath(explicitPath);
        }

        var local = Path.Combine(AppContext.BaseDirectory, "kb");
        if (File.Exists(Path.Combine(local, "kb.manifest.json")))
        {
            return local;
        }

        if (elevated)
        {
            return null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "kb");
            if (File.Exists(Path.Combine(dir.FullName, "VTDiag.sln")) && File.Exists(Path.Combine(candidate, "kb.manifest.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Runs the engine server until idle.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="log">Log (stderr).</param>
    public static async Task<int> RunAsync(EngineArguments args, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(log);
        var elevated = IsElevated();
        if (args.Install)
        {
            // The one elevated job allowed outside Program Files: copy the folder there, nothing else.
            return await SelfInstall.RunAsync(elevated, log).ConfigureAwait(false);
        }

        if (elevated && !IsUnderProgramFiles(Environment.ProcessPath))
        {
            await log.WriteLineAsync("engine.not_in_program_files: VTDiag.Engine refuses to run elevated outside Program Files.").ConfigureAwait(false);
            return ExitRefused;
        }

        if (args.Uninstall)
        {
            return await UninstallAsync(elevated, log).ConfigureAwait(false);
        }

        var privilege = elevated && !args.Limited ? PrivilegeMode.Elevated : PrivilegeMode.Limited;
        var kb = FindKb(args.KbPath, elevated);
        if (kb is null)
        {
            await log.WriteLineAsync("engine.kb_missing").ConfigureAwait(false);
            return 1;
        }

        // An abandoned mutex (the previous engine died) counts as acquired; recovery below handles it.
        using var instance = SingleInstance.TryAcquire(privilege == PrivilegeMode.Elevated ? @"Global\VTDiag.Engine" : @"Local\VTDiag.Engine.Limited");
        if (instance is null)
        {
            await log.WriteLineAsync("engine.already_running").ConfigureAwait(false);
            return 0;
        }

        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };
            await using var core = await EngineComposition.BuildAsync(new EngineStartOptions { Privilege = privilege, KbPath = kb, AllowNetwork = !args.NoNetwork }, stop.Token).ConfigureAwait(false);
            core.Controller.EffectFaulted += (_, ex) => log.WriteLine("effect.faulted " + ex.GetType().Name);
            InstallLastChanceLogging(core.Log);

            // Recovery (and the resume counter) BEFORE any RPC is accepted.
            var outcome = await SessionStartup.RunAsync(core.Controller, core.Runtime, args.ResumeSessionId, stop.Token).ConfigureAwait(false);
            if (outcome == StartupOutcome.ExitStaleResume)
            {
                await log.WriteLineAsync("resume.stale_task_removed").ConfigureAwait(false);
                return 0;
            }

            LogRecovery(core, outcome);
            var storage = privilege == PrivilegeMode.Elevated ? EngineStorage.ForElevated(core.Player.Sid) : EngineStorage.ForLimited(core.Player.LocalAppData);
            var pipeName = args.PipeName ?? EngineProtocol.PipeNamePrefix + core.Runtime.Services.Ids.NewId();
            var policy = new ClientPolicy(AppContext.BaseDirectory, core.Player.Sid, PipeClientVerifier.VerifySignature(Environment.ProcessPath!));
            await using var server = new PipeServer(
                pipeName,
                PipeServer.CreateSecurity(core.Player.Sid, limitedEngine: privilege == PrivilegeMode.Limited),
                pipe => PipeServer.IdentifyClient(pipe, policy),
                identity => new RpcSurface(new EngineConnection(core, identity)));
            server.ClientDecided += (_, c) => log.WriteLine(c.Origin == EventOrigin.Unspecified ? "client.refused " + c.RefusalCode : "client.accepted " + c.Origin);
            server.Start();
            storage.WriteDiscovery(new EngineDiscovery
            {
                ProtocolVersion = EngineProtocol.Version,
                PipeName = pipeName,
                ProcessId = Environment.ProcessId,
                StartedUtc = core.Runtime.Services.Clock.GetUtcNow(),
                Privilege = privilege,
            });
            await log.WriteLineAsync("engine.ready " + pipeName).ConfigureAwait(false);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false);
                    var idle = core.Runtime.Services.Clock.GetUtcNow() - core.LastActivityUtc;
                    if (core.Connections == 0 && !core.Controller.Busy && idle > EngineProtocol.IdleExit)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C.
            }
            finally
            {
                storage.DeleteDiscovery();
            }

            return 0;
        }
    }

    /// <summary>Unhandled exceptions (with stack traces) go to the redacted log before the process dies.</summary>
    /// <param name="log">Log.</param>
    public static void InstallLastChanceLogging(Diagnostics.DiagLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                log.Error("engine.unhandled", ex, "terminating", e.IsTerminating ? "true" : "false");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("engine.unobserved_task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Logs what start-up recovery found (journal recovery, a resumed session).</summary>
    /// <param name="core">Engine.</param>
    /// <param name="outcome">Start-up outcome.</param>
    public static void LogRecovery(EngineCore core, StartupOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(core);
        var recovery = core.Runtime.LastRecovery;
        core.Log.Info("engine.startup", outcome.ToString(), "recovery", recovery is null ? "none" : VtdJsonSummary(recovery), "phase", core.Controller.Current.Phase.ToString());
    }

    private static string VtdJsonSummary(object recovery) => VTD.Contracts.Serialization.VtdJson.Serialize(recovery);

    private static async Task<int> UninstallAsync(bool elevated, TextWriter log)
    {
        if (!elevated)
        {
            await log.WriteLineAsync("uninstall.needs_admin").ConfigureAwait(false);
            return ExitRefused;
        }

        var sid = InteractiveUser.SessionUserSid() ?? InteractiveUser.ProcessUserSid();
        var player = InteractiveUser.ForSid(sid, isAdmin: true);
        var clock = TimeProvider.System;
        var coordinator = new RebootCoordinator(new BootReader(clock, new VTD.Platform.Events.EventLogSource()), new NoProbe(), new RestartPromptLedger(clock), () => player, AppContext.BaseDirectory, InteractiveUser.ProcessUserSid());
        await coordinator.UnregisterResumeAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            File.Delete(Path.Combine(EngineStorage.ElevatedRoot(), EngineProtocol.DiscoveryFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to remove.
        }

        await log.WriteLineAsync("uninstall.resume_removed").ConfigureAwait(false);
        return 0;
    }

    private sealed class NoProbe : Contracts.Scanning.IFactProbe
    {
        public IReadOnlyCollection<string> SupportedIds => [];

        public Task<Contracts.Analysis.SignalResult> ProbeAsync(string probeId, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken)
            => Task.FromResult(new Contracts.Analysis.SignalResult { Id = probeId, State = Tri.Unknown, UnknownReason = UnknownReason.NotCollected });
    }
}
