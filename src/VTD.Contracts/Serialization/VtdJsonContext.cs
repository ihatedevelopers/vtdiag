using System.Text.Json;
using System.Text.Json.Serialization;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.Contracts.Serialization;

/// <summary>
/// Source-generated JSON metadata for every contract type. Use <see cref="VtdJson.Options"/> rather than this context's
/// properties directly. Adding a <see cref="Fact{T}"/> with a new <c>T</c> means registering that <c>T</c> here (the
/// Fact converter resolves it at runtime; a contract test walks every Fact and fails if one is missing).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DictionaryKeyPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowOutOfOrderMetadataProperties = true,
    NumberHandling = JsonNumberHandling.Strict,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    AllowTrailingCommas = false,
    WriteIndented = false,
    Converters = [typeof(CamelCaseEnumConverter)])]

// ---- primitives used inside Fact<T> and over RPC ----
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(UnknownReason))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]

// ---- snapshot and Fact<T> payloads ----
[JsonSerializable(typeof(ScanSnapshot))]
[JsonSerializable(typeof(BootIdentity))]
[JsonSerializable(typeof(PendingRebootInfo))]
[JsonSerializable(typeof(ClockInfo))]
[JsonSerializable(typeof(IReadOnlyList<HotfixInfo>))]
[JsonSerializable(typeof(IReadOnlyList<EnvVarFinding>))]
[JsonSerializable(typeof(CpuInfo))]
[JsonSerializable(typeof(MemoryInfo))]
[JsonSerializable(typeof(IReadOnlyList<GpuInfo>))]
[JsonSerializable(typeof(BoardInfo))]
[JsonSerializable(typeof(FirmwareType))]
[JsonSerializable(typeof(TpmInfo))]
[JsonSerializable(typeof(VbsInfo))]
[JsonSerializable(typeof(BcdFlags))]
[JsonSerializable(typeof(BitLockerStatus))]
[JsonSerializable(typeof(DefenderInfo))]
[JsonSerializable(typeof(IReadOnlyList<AvProduct>))]
[JsonSerializable(typeof(IReadOnlyList<ExploitOverride>))]
[JsonSerializable(typeof(IReadOnlyList<IfeoEntry>))]
[JsonSerializable(typeof(IReadOnlyList<DiskInfo>))]
[JsonSerializable(typeof(PartitionStyle))]
[JsonSerializable(typeof(IReadOnlyList<VolumeInfo>))]
[JsonSerializable(typeof(IReadOnlyList<StorageControllerInfo>))]
[JsonSerializable(typeof(IReadOnlyList<DeviceInfo>))]
[JsonSerializable(typeof(IReadOnlyList<LoadedDriver>))]
[JsonSerializable(typeof(IReadOnlyList<ServiceInfo>))]
[JsonSerializable(typeof(IReadOnlyList<ProcessInfo>))]
[JsonSerializable(typeof(IReadOnlyList<InstalledApp>))]
[JsonSerializable(typeof(IReadOnlyList<StartupItem>))]
[JsonSerializable(typeof(IReadOnlyList<NetAdapter>))]
[JsonSerializable(typeof(ConnectivityInfo))]
[JsonSerializable(typeof(ProxyInfo))]
[JsonSerializable(typeof(Ipv6Info))]
[JsonSerializable(typeof(IReadOnlyList<FirewallRuleInfo>))]
[JsonSerializable(typeof(FirewallProfiles))]
[JsonSerializable(typeof(WlanInfo))]
[JsonSerializable(typeof(IReadOnlyList<PingProbe>))]
[JsonSerializable(typeof(PowerScheme))]
[JsonSerializable(typeof(PowerMode))]
[JsonSerializable(typeof(BatteryInfo))]
[JsonSerializable(typeof(IReadOnlyList<MonitorInfo>))]
[JsonSerializable(typeof(IReadOnlyList<GpuPreference>))]
[JsonSerializable(typeof(IReadOnlyList<AudioEndpoint>))]
[JsonSerializable(typeof(MouseSettings))]
[JsonSerializable(typeof(RiotInstall))]
[JsonSerializable(typeof(VanguardInfo))]
[JsonSerializable(typeof(IReadOnlyList<CompatLayerEntry>))]
[JsonSerializable(typeof(GameConfigDigest))]
[JsonSerializable(typeof(ShooterLogDigest))]
[JsonSerializable(typeof(RiotClientLogDigest))]
[JsonSerializable(typeof(IReadOnlyList<CrashReportMeta>))]
[JsonSerializable(typeof(IReadOnlyList<DetectedCode>))]
[JsonSerializable(typeof(IReadOnlyList<EventHit>))]
[JsonSerializable(typeof(IReadOnlyList<AppCrash>))]
[JsonSerializable(typeof(IReadOnlyList<BugCheck>))]
[JsonSerializable(typeof(IReadOnlyList<MinidumpMeta>))]
[JsonSerializable(typeof(PackFactData))]

// ---- scanning ----
[JsonSerializable(typeof(ScanRequest))]
[JsonSerializable(typeof(ScanProgress))]
[JsonSerializable(typeof(PlayerContext))]
[JsonSerializable(typeof(IReadOnlyList<RawCaptureEntry>))]

// ---- knowledge base files ----
[JsonSerializable(typeof(KbManifest))]
[JsonSerializable(typeof(KbRevocationList))]
[JsonSerializable(typeof(KbVersion))]
[JsonSerializable(typeof(IReadOnlyList<TrustedHelperKey>))]
[JsonSerializable(typeof(RuleDef))]
[JsonSerializable(typeof(IReadOnlyList<RuleDef>))]
[JsonSerializable(typeof(SignalDef))]
[JsonSerializable(typeof(IReadOnlyList<SignalDef>))]
[JsonSerializable(typeof(CollectorDef))]
[JsonSerializable(typeof(IReadOnlyList<CollectorDef>))]
[JsonSerializable(typeof(ConflictDef))]
[JsonSerializable(typeof(IReadOnlyList<ConflictDef>))]
[JsonSerializable(typeof(ErrorCodeDef))]
[JsonSerializable(typeof(IReadOnlyList<ErrorCodeDef>))]
[JsonSerializable(typeof(FixDef))]
[JsonSerializable(typeof(IReadOnlyList<FixDef>))]
[JsonSerializable(typeof(GuideDef))]
[JsonSerializable(typeof(IReadOnlyList<GuideDef>))]
[JsonSerializable(typeof(QuestionDef))]
[JsonSerializable(typeof(IReadOnlyList<QuestionDef>))]
[JsonSerializable(typeof(SymptomDef))]
[JsonSerializable(typeof(IReadOnlyList<SymptomDef>))]
[JsonSerializable(typeof(TableDef))]
[JsonSerializable(typeof(IReadOnlyList<TableDef>))]
[JsonSerializable(typeof(KbIssue))]

// ---- analysis ----
[JsonSerializable(typeof(AnalysisResult))]
[JsonSerializable(typeof(IntakeAnswers))]
[JsonSerializable(typeof(SignalResult))]
[JsonSerializable(typeof(IReadOnlyList<SignalResult>))]

// ---- fixes and journal ----
[JsonSerializable(typeof(FixBinding))]
[JsonSerializable(typeof(FixPreview))]
[JsonSerializable(typeof(ConsentToken))]
[JsonSerializable(typeof(FixRunResult))]
[JsonSerializable(typeof(VerifyResult))]
[JsonSerializable(typeof(UndoResult))]
[JsonSerializable(typeof(DriftFinding))]
[JsonSerializable(typeof(IReadOnlyList<DriftFinding>))]
[JsonSerializable(typeof(AttemptSummary))]
[JsonSerializable(typeof(IReadOnlyList<AttemptSummary>))]
[JsonSerializable(typeof(FixProgress))]
[JsonSerializable(typeof(StateCapture))]
[JsonSerializable(typeof(OpArgSpec))]
[JsonSerializable(typeof(IReadOnlyList<OpSpec>))]
[JsonSerializable(typeof(Proposer))]
[JsonSerializable(typeof(PolicyDecision))]
[JsonSerializable(typeof(JournalRecord))]
[JsonSerializable(typeof(RecoveryReport))]

// ---- session ----
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(SessionEvent))]
[JsonSerializable(typeof(SessionCommand))]
[JsonSerializable(typeof(EngineEvent))]
[JsonSerializable(typeof(RestartToken))]
[JsonSerializable(typeof(RestartKind))]
[JsonSerializable(typeof(RebootReadiness))]
[JsonSerializable(typeof(ResumeRegistration))]
[JsonSerializable(typeof(RestorePointInfo))]

// ---- monitor ----
[JsonSerializable(typeof(MonitorOptions))]
[JsonSerializable(typeof(MonitorTick))]
[JsonSerializable(typeof(MonitorSummary))]

// ---- reporting and assist ----
[JsonSerializable(typeof(RedactionPolicy))]
[JsonSerializable(typeof(RedactionManifest))]
[JsonSerializable(typeof(ReportModel))]
[JsonSerializable(typeof(ReportOptions))]
[JsonSerializable(typeof(ReportBundle))]
[JsonSerializable(typeof(OutcomeCard))]
[JsonSerializable(typeof(FixPlan))]
[JsonSerializable(typeof(SignedPlanEnvelope))]
[JsonSerializable(typeof(PlanValidation))]
[JsonSerializable(typeof(ScanBundleManifest))]
[JsonSerializable(typeof(ScanBundle))]

// ---- IPC ----
[JsonSerializable(typeof(ClientHello))]
[JsonSerializable(typeof(EngineHello))]
[JsonSerializable(typeof(ExportRequest))]
[JsonSerializable(typeof(ClientLogEntry))]
[JsonSerializable(typeof(IReadOnlyList<ClientLogEntry>))]
[JsonSerializable(typeof(ExportResult))]
[JsonSerializable(typeof(RestartPrompt))]
[JsonSerializable(typeof(EngineNotification))]
[JsonSerializable(typeof(EngineDiscovery))]
public sealed partial class VtdJsonContext : JsonSerializerContext
{
}
