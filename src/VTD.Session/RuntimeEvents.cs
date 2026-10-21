using VTD.Contracts.Sessions;

namespace VTD.Session;

// Engine-internal effect results. They are EngineEvents (so CommandPolicy can never accept them and SendAsync, which
// takes a SessionCommand, can never carry them), they are never serialized, and only the runtime raises them while
// executing an effect. They are not part of the IPC contract.

/// <summary>The resume registration finished (<see cref="RegisterResumeEffect"/>).</summary>
public sealed record ResumeRegistered : EngineEvent
{
    /// <summary>What was registered and read back.</summary>
    public required ResumeRegistration Registration { get; init; }

    /// <summary>The click-minted token of the "Restart now" path, handed back so the machine can emit the restart.</summary>
    public RestartToken? ThenRestart { get; init; }
}

/// <summary>The resume tasks and RunOnce entry were removed (<see cref="UnregisterResumeEffect"/>).</summary>
public sealed record ResumeUnregistered : EngineEvent;

/// <summary>The restart call failed (the PC did not restart).</summary>
public sealed record RestartFailed : EngineEvent
{
    /// <summary>Machine error code.</summary>
    public required string ErrorCode { get; init; }
}

/// <summary>The deadman deadline of an attempt passed on the engine clock.</summary>
public sealed record DeadmanExpired : EngineEvent
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }
}

/// <summary>The restore point service made (or failed to make) the session's restore point.</summary>
public sealed record RestorePointRecorded : EngineEvent
{
    /// <summary>Info.</summary>
    public required RestorePointInfo Info { get; init; }
}
