namespace VTD.Contracts;

/// <summary>
/// How a value must be treated by <c>IRedactor</c> (design_B.md section 12, design_A.md section 14). Every string
/// property in a type that is exported (snapshot, analysis, report, plan) carries a <see cref="SensitivityAttribute"/>,
/// either on the property or on its declaring type; a contract test enforces this so a new field cannot leak by accident.
/// </summary>
public enum Sensitivity
{
    /// <summary>Technical value that is safe to share: driver file names, versions, event ids, invariant enum-like names.</summary>
    None = 0,

    /// <summary>Identifies the person or the machine (computer name, user name, SID, serials). Dropped at share level.</summary>
    Identifier = 1,

    /// <summary>A PnP/device instance id. Share level keeps the enumerator and VEN/DEV part and replaces the
    /// instance-specific tail with a salted hash, so a helper's <c>.vtdplan</c> can still bind to it on the player's PC.</summary>
    DeviceId = 2,

    /// <summary>An entity id whose treatment depends on <see cref="EntityKind"/> (device ids as <see cref="DeviceId"/>,
    /// file paths as <see cref="Path"/>, service and process names unchanged).</summary>
    EntityId = 3,

    /// <summary>A file system path. Profile paths become <c>%USERPROFILE%\…</c>; other user-specific segments are removed.</summary>
    Path = 4,

    /// <summary>Replaced by a salted hash at share level (SSIDs, audio endpoint names).</summary>
    Hashed = 5,

    /// <summary>MAC address: keep the OUI (vendor), salted hash for the rest.</summary>
    MacAddress = 6,

    /// <summary>IP address: reduced to a class (<c>private/24</c>, <c>cgnat</c>, <c>public</c>); server IPs to region/ASN.</summary>
    IpAddress = 7,

    /// <summary>Free text that may contain anything (event parameters, captured log groups, helper notes).
    /// Passes the regex sweep; a sweep hit at export time blocks the export.</summary>
    FreeText = 8,
}

/// <summary>
/// Declares the <see cref="Sensitivity"/> of a property, or the default for all string properties of a type (inherited by
/// derived records, for example every <c>SessionEvent</c>). Lookup order for the redactor: property attribute, then the
/// declaring type's attribute with <c>inherit: true</c>.
/// </summary>
/// <param name="level">The sensitivity level.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class SensitivityAttribute(Sensitivity level) : Attribute
{
    /// <summary>The sensitivity level.</summary>
    public Sensitivity Level { get; } = level;
}

/// <summary>Redaction level of a snapshot or export.</summary>
public enum RedactionLevel
{
    /// <summary>Raw, unredacted. Only ever stored locally under <c>%ProgramData%\VTDiag\snapshots</c>; never exported.</summary>
    None = 0,

    /// <summary>Local level: secrets removed (never collected), identifiers kept only where a fix needs them.</summary>
    Local = 1,

    /// <summary>Share level: everything that leaves the PC (report, <c>.vtdscan</c>, "Copy for Reddit/AI").</summary>
    Share = 2,
}
