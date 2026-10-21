using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>
/// <c>file.quarantine</c>: MOVES an allowlisted file into <c>%ProgramData%\VTDiag\quarantine\&lt;id&gt;\</c> with a manifest
/// (S13: never delete). The quarantine folder is derived from the original path and content hash, so <see cref="Intended"/>
/// is pure and the same file always lands in the same place. Undo moves it back if nothing took its place.
/// </summary>
public sealed class FileQuarantineOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.FileQuarantine;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("path", OpArgType.FilePath)];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["exists", "sha256", "quarantinePath"];

    /// <summary>Where a file with this path and content goes.</summary>
    /// <param name="root">Quarantine root.</param>
    /// <param name="fullPath">Original full path.</param>
    /// <param name="sha256">Content hash.</param>
    public static string QuarantinePathFor(string root, string fullPath, string sha256)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        var id = CanonicalHash.OfText(fullPath.ToUpperInvariant() + "|" + sha256)[..24];
        return Path.Combine(root, id, Path.GetFileName(fullPath));
    }

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        if (before.Get("exists") != "true")
        {
            return before;
        }

        var q = QuarantinePathFor(Ports.Files.QuarantineRoot, FullPath(args), before.Get("sha256"));
        return Captures.Of(Op, ("exists", "false"), ("sha256", before.Get("sha256")), ("quarantinePath", q));
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
        => before?.Get("exists") == "true" ? null : "target.file_missing";

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
    {
        var full = ArgValidator.ExpandPath(args.Get("path"), Ports.Files.Folders);
        return ValueTask.FromResult(full is null ? ProtectedTargetPolicy.Deny("args") : Policy.CheckQuarantine(full));
    }

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var full = FullPath(args);
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["exists"] = "false", ["sha256"] = string.Empty, ["quarantinePath"] = string.Empty };
        if (Ports.Files.Probe(full).Exists)
        {
            map["exists"] = "true";
            map["sha256"] = Ports.Files.Sha256(full);
            return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
        }

        // Not at its place: look in the quarantine for any copy we made of this path (probe after apply / recovery).
        if (FindQuarantined(full) is { } found)
        {
            map["sha256"] = found.Sha;
            map["quarantinePath"] = found.Path;
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var full = FullPath(args);
        var sha = before.Get("sha256");
        var dest = QuarantinePathFor(Ports.Files.QuarantineRoot, full, sha);
        var facts = Ports.Files.Probe(full);
        Ports.Files.SafeMove(full, dest, sha);
        Ports.Files.WriteManifest(Path.GetDirectoryName(dest)!, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["originalPath"] = full,
            ["fileName"] = Path.GetFileName(full),
            ["sha256"] = sha,
            ["size"] = facts.Size.ToString(CultureInfo.InvariantCulture),
            ["lastWriteUtc"] = facts.LastWriteUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            ["quarantinedUtc"] = Ctx.Clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["sessionId"] = Ctx.Session.SessionId,
        });
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        var full = FullPath(args);
        if (before.Get("exists") != "true")
        {
            return ValueTask.FromResult(false);
        }

        if (Ports.Files.Probe(full).Exists)
        {
            throw new PortRefusedException("undo.target_exists");
        }

        var sha = before.Get("sha256");
        var source = QuarantinePathFor(Ports.Files.QuarantineRoot, full, sha);
        if (!Ports.Files.Probe(source).Exists)
        {
            throw new PortRefusedException("undo.quarantine_missing");
        }

        Ports.Files.SafeMove(source, full, sha);
        return ValueTask.FromResult(false);
    }

    private string FullPath(OpArgs args)
        => ArgValidator.ExpandPath(args.Get("path"), Ports.Files.Folders) ?? throw new ArgumentException("args.invalid:path");

    private (string Path, string Sha)? FindQuarantined(string full)
    {
        if (!Ports.Files.DirectoryExists(Ports.Files.QuarantineRoot))
        {
            return null;
        }

        // A quarantined copy is <root>\<id>\<file name> where <id> derives from the original path and the content hash,
        // so a candidate is ours only when its own hash recomputes the same folder.
        foreach (var dir in Ports.Files.ListDirectories(Ports.Files.QuarantineRoot))
        {
            var candidate = Path.Combine(dir, Path.GetFileName(full));
            if (!Ports.Files.Probe(candidate).Exists)
            {
                continue;
            }

            var sha = Ports.Files.Sha256(candidate);
            if (string.Equals(QuarantinePathFor(Ports.Files.QuarantineRoot, full, sha), candidate, StringComparison.OrdinalIgnoreCase))
            {
                return (candidate, sha);
            }
        }

        return null;
    }
}
