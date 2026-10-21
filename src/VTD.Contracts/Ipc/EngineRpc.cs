using System.Text.Json.Serialization;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.Contracts.Ipc;

/// <summary>Engine protocol constants.</summary>
public static class EngineProtocol
{
    /// <summary>Protocol version; Hello fails on mismatch.</summary>
    public const int Version = 1;

    /// <summary>Pipe name prefix; the engine appends a per-launch nonce: <c>\\.\pipe\VTDiag.Engine.&lt;nonce&gt;</c>.
    /// DACL: interactive user SID + Administrators + SYSTEM; PIPE_REJECT_REMOTE_CLIENTS; FILE_FLAG_FIRST_PIPE_INSTANCE.</summary>
    public const string PipeNamePrefix = "VTDiag.Engine.";

    /// <summary>UI heartbeat interval.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(2);

    /// <summary>An idle engine with no UI exits after this.</summary>
    public static TimeSpan IdleExit { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Discovery file name under <c>%ProgramData%\VTDiag\</c> (written by the engine at start, deleted at exit;
    /// ACL: SYSTEM/Administrators write, interactive user read). Lets a UI started by the resume task, the CLI and the MCP
    /// host find an engine they did not start; <c>FIRST_PIPE_INSTANCE</c> prevents pipe-name squatting.</summary>
    public const string DiscoveryFileName = "engine.json";

    /// <summary>Longest a CLI/MCP client may wait for a consent decision in <see cref="IEngineRpc.WaitForOutcomeAsync"/>.</summary>
    public const int MaxWaitSeconds = 600;
}

/// <summary>Contents of <see cref="EngineProtocol.DiscoveryFileName"/>.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record EngineDiscovery
{
    /// <summary>Protocol version.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Full pipe name (<c>VTDiag.Engine.&lt;nonce&gt;</c>).</summary>
    public required string PipeName { get; init; }

    /// <summary>Engine PID. Clients check GetNamedPipeServerProcessId equals it, and that its image is under %ProgramFiles%.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Engine start (UTC).</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Elevated or limited.</summary>
    public required PrivilegeMode Privilege { get; init; }
}

/// <summary>Kind of client, as claimed. The engine trusts only what it verifies from the pipe (PID → token SID, image path
/// under %ProgramFiles%, Authenticode signer).</summary>
public enum ClientKind
{
    /// <summary>VTD.App.</summary>
    Ui = 0,

    /// <summary>The CLI.</summary>
    Cli = 1,

    /// <summary>The MCP host (M3).</summary>
    Mcp = 2,
}

/// <summary>Client hello.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ClientHello
{
    /// <summary>Protocol version.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Client version.</summary>
    public required string AppVersion { get; init; }

    /// <summary>UI language.</summary>
    public required string Language { get; init; }

    /// <summary>Claimed kind (informational only).</summary>
    public ClientKind Kind { get; init; } = ClientKind.Ui;

    /// <summary>Self-declared label for <see cref="Fixes.Proposer.Label"/> (for example <c>Claude Code (MCP)</c>); unverified.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public string? Label { get; init; }
}

/// <summary>Engine hello.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record EngineHello
{
    /// <summary>Protocol version.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Engine version.</summary>
    public required string EngineVersion { get; init; }

    /// <summary>KB version.</summary>
    public required string KbVersion { get; init; }

    /// <summary>Elevated or limited.</summary>
    public required PrivilegeMode Privilege { get; init; }

    /// <summary>Engine PID (the UI verifies GetNamedPipeServerProcessId equals the process it started).</summary>
    public required int EngineProcessId { get; init; }

    /// <summary>True only for the verified VTD.App client: consent and restart tokens are accepted from it alone.</summary>
    public required bool ConsentAllowed { get; init; }

    /// <summary>Recovery that ran before this connection.</summary>
    public RecoveryReport? Recovery { get; init; }

    /// <summary>Current session, if any.</summary>
    public SessionState? Session { get; init; }

    /// <summary>Capability flags (for example <c>fixes</c>, <c>monitor</c>, <c>plans</c>).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

/// <summary>What to export.</summary>
public enum ExportKind
{
    /// <summary>HTML report.</summary>
    ReportHtml = 0,

    /// <summary>Markdown summary (Copy for Reddit).</summary>
    ReportMarkdown = 1,

    /// <summary>JSON report.</summary>
    ReportJson = 2,

    /// <summary><c>.vtdscan</c> bundle.</summary>
    ScanBundle = 3,

    /// <summary>Copy for AI.</summary>
    AiDigest = 4,

    /// <summary>
    /// <c>.vtddebug</c>: one redacted zip (rolling logs, last scan snapshot, analysis, journal and session state, environment
    /// summary, manifest) a player sends so both a VTDiag bug and the PC problem can be understood. A sweep hit in any
    /// part blocks the export and names the section.
    /// </summary>
    DebugBundle = 5,
}

/// <summary>
/// Export request. Every export is Share-level: the redaction level is not the client's choice. The engine builds the
/// <see cref="RedactionPolicy"/> itself.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record ExportRequest
{
    /// <summary>Kind.</summary>
    public required ExportKind Kind { get; init; }

    /// <summary>Language.</summary>
    public string Language { get; init; } = Languages.English;

    /// <summary>Snapshot sections the player unticked in the preview (or that a sweep hit suggested excluding).</summary>
    public IReadOnlyList<string> ExcludedSections { get; init; } = [];

    /// <summary>Keep the game server IP ("include for ISP ticket"). Honoured from the verified UI only.</summary>
    public bool IncludeServerIp { get; init; }

    /// <summary>Also save under <c>%ProgramData%\VTDiag\reports</c> (readable by the player).</summary>
    public bool SaveToReportsFolder { get; init; }
}

/// <summary>Log level of a <see cref="ClientLogEntry"/>.</summary>
public enum ClientLogLevel
{
    /// <summary>Verbose detail (kept only while debug mode is on).</summary>
    Debug = 0,

    /// <summary>Normal event.</summary>
    Info = 1,

    /// <summary>Something unexpected that the app handled.</summary>
    Warning = 2,

    /// <summary>A failure.</summary>
    Error = 3,
}

/// <summary>
/// One log entry from the UI. The UI cannot write files, so it hands its events to the engine, which redacts them at write
/// time and rolls them into the same size-capped JSON-lines log as its own. <see cref="Message"/> and
/// <see cref="Detail"/> are scrubbed (no user names, tokens, serials, machine name) before they touch the disk.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record ClientLogEntry
{
    /// <summary>Client time (audit only).</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>Level.</summary>
    public required ClientLogLevel Level { get; init; }

    /// <summary>Event name, for example <c>app.start</c>, <c>ui.navigate</c>, <c>app.unhandled</c>.</summary>
    public required string Event { get; init; }

    /// <summary>Short message.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public string? Message { get; init; }

    /// <summary>Extra detail (an exception with its stack trace).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public string? Detail { get; init; }
}

/// <summary>Export status.</summary>
public enum ExportStatus
{
    /// <summary>Done.</summary>
    Ok = 0,

    /// <summary>The regex sweep found something: nothing was exported.</summary>
    BlockedByRedaction = 1,

    /// <summary>Failed.</summary>
    Failed = 2,
}

/// <summary>Export result. The UI writes <see cref="TextContent"/>/<see cref="Base64Content"/> through a SaveFileDialog stream or the clipboard.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ExportResult
{
    /// <summary>Status.</summary>
    public required ExportStatus Status { get; init; }

    /// <summary>Suggested file name (<c>VTDiag-Report-&lt;yyyymmdd&gt;-&lt;shortid&gt;.html</c>).</summary>
    public string? FileName { get; init; }

    /// <summary>Text content (HTML, Markdown, JSON, digest).</summary>
    public string? TextContent { get; init; }

    /// <summary>Binary content (zip), base64.</summary>
    public string? Base64Content { get; init; }

    /// <summary>SHA-256.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Redaction manifest.</summary>
    public RedactionManifest? Redaction { get; init; }

    /// <summary>Sweep hits when blocked.</summary>
    public IReadOnlyList<SweepHit> SweepHits { get; init; } = [];

    /// <summary>Saved path.</summary>
    [Sensitivity(Sensitivity.Path)]
    public string? SavedPath { get; init; }
}

/// <summary>Returned by <see cref="IEngineRpc.PrepareRestartAsync"/>: the nonce the click must echo, and the readiness gates.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RestartPrompt
{
    /// <summary>Single-use nonce.</summary>
    public required string Nonce { get; init; }

    /// <summary>Kind.</summary>
    public required RestartKind Kind { get; init; }

    /// <summary>Readiness.</summary>
    public required RebootReadiness Readiness { get; init; }

    /// <summary>The restart button may be enabled (AC power, no running apply...).</summary>
    public required bool Allowed { get; init; }

    /// <summary>Why not.</summary>
    public IReadOnlyList<string> BlockReasonKeys { get; init; } = [];

    /// <summary>Nonce expiry.</summary>
    public required DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>Engine → client notification. JSON discriminator: <c>type</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ScanProgressNotification), "scanProgress")]
[JsonDerivedType(typeof(FixProgressNotification), "fixProgress")]
[JsonDerivedType(typeof(SessionChangedNotification), "sessionChanged")]
[JsonDerivedType(typeof(MonitorTickNotification), "monitorTick")]
[JsonDerivedType(typeof(DriftNotification), "drift")]
[JsonDerivedType(typeof(DeadmanNotification), "deadman")]
[JsonDerivedType(typeof(ConsentRequestedNotification), "consentRequested")]
public abstract record EngineNotification
{
    /// <summary>Sequence.</summary>
    public required long Seq { get; init; }

    /// <summary>When.</summary>
    public required DateTimeOffset Utc { get; init; }
}

/// <summary>Scan progress.</summary>
public sealed record ScanProgressNotification : EngineNotification
{
    /// <summary>Progress.</summary>
    public required ScanProgress Progress { get; init; }
}

/// <summary>Fix progress.</summary>
public sealed record FixProgressNotification : EngineNotification
{
    /// <summary>Progress.</summary>
    public required FixProgress Progress { get; init; }
}

/// <summary>Session changed (phase, revision).</summary>
public sealed record SessionChangedNotification : EngineNotification
{
    /// <summary>New state.</summary>
    public required SessionState Session { get; init; }
}

/// <summary>Monitor tick (UI/tray).</summary>
public sealed record MonitorTickNotification : EngineNotification
{
    /// <summary>Tick.</summary>
    public required MonitorTick Tick { get; init; }
}

/// <summary>Drift found on rescan.</summary>
public sealed record DriftNotification : EngineNotification
{
    /// <summary>Findings.</summary>
    public required IReadOnlyList<DriftFinding> Findings { get; init; }
}

/// <summary>Deadman countdown tick.</summary>
public sealed record DeadmanNotification : EngineNotification
{
    /// <summary>Attempt.</summary>
    [Sensitivity(Sensitivity.None)]
    public required string AttemptId { get; init; }

    /// <summary>Seconds left before auto-revert.</summary>
    public required int SecondsLeft { get; init; }
}

/// <summary>What a consent request is for.</summary>
public enum ConsentKind
{
    /// <summary>Apply a fix (<see cref="FixSelected"/> from any client).</summary>
    Fix = 0,

    /// <summary>The second "this cannot be undone" click of a one-way fix.</summary>
    SecondConsent = 1,

    /// <summary>Undo an attempt (<see cref="UndoProposed"/> from the CLI/MCP).</summary>
    Undo = 2,
}

/// <summary>Sent to the UI when a decision only the player can make is waiting (the UI brings its window forward).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ConsentRequestedNotification : EngineNotification
{
    /// <summary>Kind.</summary>
    public required ConsentKind Kind { get; init; }

    /// <summary>Preview awaiting consent (Fix, SecondConsent).</summary>
    public string? PreviewId { get; init; }

    /// <summary>Attempt (SecondConsent, Undo).</summary>
    public string? AttemptId { get; init; }

    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Who asked.</summary>
    public required Proposer ProposedBy { get; init; }

    /// <summary>For <see cref="ConsentKind.SecondConsent"/>: the nonce the second click's token must echo
    /// (<see cref="ConsentToken.SecondConsentNonce"/>). Useless to any other client: <see cref="ConsentGiven"/> is UI-only.</summary>
    public string? SecondConsentNonce { get; init; }
}

/// <summary>
/// The App ⇄ Engine contract over the named pipe with StreamJsonRpc (<c>SystemTextJsonFormatter</c> with
/// <see cref="Serialization.VtdJson.CreateRpcOptions"/>). All user actions are <see cref="SessionCommand"/>s. Implemented
/// by A4 (server) and faked by <c>FakeEngineRpc</c> for A6.
/// </summary>
/// <remarks>
/// The CLI (<c>VTDiag.Engine.exe --cli scan|analyze|fix preview|fix apply|undo|export --json</c>) and the M3 MCP host are
/// pure clients of this interface over the same pipe: they never host <c>IFixRunner</c> or primitives in-process, so the
/// engine always sees their pipe identity. For non-UI clients the engine returns Share-redacted data (snapshot, analysis,
/// previews without nonce), enforces <see cref="CommandPolicy"/>, and turns writes into consent requests for the UI.
/// </remarks>
public interface IEngineRpc
{
    /// <summary>Raised for progress, phase changes, monitor ticks, drift, deadman ticks and consent requests.</summary>
    event EventHandler<EngineNotification>? Notification;

    /// <summary>Handshake: version check, privilege, recovery report, current session.</summary>
    /// <param name="hello">Client hello.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<EngineHello> HelloAsync(ClientHello hello, CancellationToken cancellationToken);

    /// <summary>Heartbeat (every <see cref="EngineProtocol.HeartbeatInterval"/>).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>Current session.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<SessionState> GetSessionAsync(CancellationToken cancellationToken);

    /// <summary>Sends a user action. Throws (RPC error <c>command.not_allowed</c>) when <see cref="CommandPolicy"/> refuses
    /// the verified origin.</summary>
    /// <param name="command">Command.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<SessionState> SendAsync(SessionCommand command, CancellationToken cancellationToken);

    /// <summary>Latest snapshot of the session (Local level for the UI's fact browser, Share level for CLI/MCP).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<ScanSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Latest analysis of the session, if any.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<AnalysisResult?> GetAnalysisAsync(CancellationToken cancellationToken);

    /// <summary>
    /// For CLI/MCP proposals: waits until the player decides on the preview (consent → applied result; skip/decline →
    /// null) or the timeout passes (null).
    /// </summary>
    /// <param name="previewId">Preview id returned through the session after <see cref="FixSelected"/>.</param>
    /// <param name="timeoutSeconds">At most <see cref="EngineProtocol.MaxWaitSeconds"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<FixRunResult?> WaitForOutcomeAsync(string previewId, int timeoutSeconds, CancellationToken cancellationToken);

    /// <summary>
    /// The preview the engine computed for <see cref="FixSelected"/> (<see cref="SessionState.CurrentPreviewId"/> or
    /// <see cref="ConsentRequestedNotification.PreviewId"/>): the exact preview the consent token must bind to, so the
    /// consent dialog shows what will run. Null when unknown, expired or already used. The UI gets the consent nonce;
    /// CLI/MCP clients get it without (<see cref="FixPreview.ConsentNonce"/> null).
    /// </summary>
    /// <param name="previewId">Preview id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<FixPreview?> GetPreviewAsync(string previewId, CancellationToken cancellationToken);

    /// <summary>A monitor summary of this session (<see cref="MonitorCompleted.SummaryId"/>,
    /// <see cref="MonitorRunInfo.SummaryId"/>); null when unknown. Share-redacted for CLI/MCP.</summary>
    /// <param name="summaryId">Summary id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<MonitorSummary?> GetMonitorSummaryAsync(string summaryId, CancellationToken cancellationToken);

    /// <summary>Previews a fix without changing the session phase (CLI <c>fix preview</c>, Helper checks).</summary>
    /// <param name="fixId">Fix id.</param>
    /// <param name="binding">Binding.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<FixPreview> PreviewFixAsync(string fixId, FixBinding binding, CancellationToken cancellationToken);

    /// <summary>Undo center rows.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken);

    /// <summary>Gets the restart nonce and readiness for the restart screen.</summary>
    /// <param name="kind">Kind.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<RestartPrompt> PrepareRestartAsync(RestartKind kind, CancellationToken cancellationToken);

    /// <summary>Exports a report or bundle (share-level redaction; blocked on a sweep hit).</summary>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken);

    /// <summary>Hands UI log entries to the engine (redacted at write time; at most 200 entries per call).</summary>
    /// <param name="entries">Entries.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task SubmitClientLogAsync(IReadOnlyList<ClientLogEntry> entries, CancellationToken cancellationToken);

    /// <summary>
    /// Raises (true) or lowers (false) the log verbosity while the player reproduces a problem. Honoured from the verified UI
    /// and the CLI; returns the mode now in effect.
    /// </summary>
    /// <param name="enabled">Debug mode on or off.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<bool> SetDebugModeAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Validates a <c>.vtdplan</c> (a plain <see cref="FixPlan"/> or a <see cref="SignedPlanEnvelope"/>) against a fresh scan.</summary>
    /// <param name="planText">File content.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PlanValidation> ImportPlanAsync(string planText, CancellationToken cancellationToken);
}

/// <summary>Exit codes of <c>VTDiag.Engine.exe --cli …</c>.</summary>
public enum CliExitCode
{
    /// <summary>Success.</summary>
    Ok = 0,

    /// <summary>Unexpected error.</summary>
    Error = 1,

    /// <summary>Bad arguments.</summary>
    Usage = 2,

    /// <summary>Needs the elevated engine.</summary>
    NotElevated = 3,

    /// <summary>A write was requested: consent must be given in the VTDiag window (the CLI can never consent).</summary>
    ConsentRequired = 4,

    /// <summary>Blocked by a guard, policy or redaction.</summary>
    Blocked = 5,

    /// <summary>The session needs the player's attention.</summary>
    NeedsAttention = 6,
}
