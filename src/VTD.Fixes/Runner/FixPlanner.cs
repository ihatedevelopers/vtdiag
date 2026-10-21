using System.Globalization;
using System.Text.RegularExpressions;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Fixes.Ops;
using VTD.Fixes.Policy;

namespace VTD.Fixes.Runner;

/// <summary>One resolved step: a primitive with concrete arguments (no <c>$bind</c> references left).</summary>
/// <param name="Index">Resolved index (0-based, in apply order).</param>
/// <param name="Def">The KB step.</param>
/// <param name="Op">The primitive, or null when the KB names an unknown one.</param>
/// <param name="Args">Resolved arguments.</param>
/// <param name="Entities">Bound entities this step acts on.</param>
public sealed record PlannedStep(int Index, StepDef Def, OperationBase? Op, IReadOnlyDictionary<string, string> Args, IReadOnlyList<EntityRef> Entities)
{
    /// <summary>Arguments as <see cref="OpArgs"/>.</summary>
    public OpArgs OpArgs => new() { Values = Args };
}

/// <summary>One resolved guard.</summary>
/// <param name="Probe">Probe id.</param>
/// <param name="Args">Resolved arguments.</param>
/// <param name="Must">Required value.</param>
/// <param name="Severity">Severity.</param>
/// <param name="FailKey">Message key.</param>
public sealed record PlannedGuard(string Probe, IReadOnlyDictionary<string, string> Args, Tri Must, GuardSeverity Severity, string FailKey);

/// <summary>One resolved verification check.</summary>
/// <param name="Def">The KB check.</param>
/// <param name="Args">Resolved arguments.</param>
/// <param name="Op">The primitive whose probe reads the state, or null for a live probe id.</param>
public sealed record PlannedVerify(VerifyDef Def, IReadOnlyDictionary<string, string> Args, OperationBase? Op);

/// <summary>A fix resolved against a binding, with its effective metadata (raised from the primitives, never lowered).</summary>
public sealed record FixPlan
{
    /// <summary>Fix.</summary>
    public required FixDef Fix { get; init; }

    /// <summary>Binding.</summary>
    public required FixBinding Binding { get; init; }

    /// <summary>Steps in apply order.</summary>
    public required IReadOnlyList<PlannedStep> Steps { get; init; }

    /// <summary>Guards (standard ones first).</summary>
    public required IReadOnlyList<PlannedGuard> Guards { get; init; }

    /// <summary>Verification checks.</summary>
    public required IReadOnlyList<PlannedVerify> Verify { get; init; }

    /// <summary>Planning errors (block reasons); empty when the plan is sound.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>Effective risk.</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>Effective reversibility.</summary>
    public required Reversibility Reversibility { get; init; }

    /// <summary>Effective reboot need.</summary>
    public required RebootNeed Reboot { get; init; }

    /// <summary>Needs elevation.</summary>
    public required bool NeedsAdmin { get; init; }

    /// <summary>Can cut connectivity (a deadman is required).</summary>
    public required bool AffectsNetwork { get; init; }
}

/// <summary>
/// Resolves a KB fix against a binding: validates the binding against <see cref="FixDef.Bind"/>, expands <c>pick: all</c>
/// bindings into one step per entity, substitutes <c>$name.id</c> / <c>$name.name</c>, validates every argument against
/// the primitive's spec, adds the runner's standard guards (S9), and computes the effective metadata.
/// </summary>
public static partial class FixPlanner
{
    // Declared first: static initializers run in textual order and the guard lists below use it.
    private static readonly IReadOnlyDictionary<string, string> Empty = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Most resolved steps one fix may have.</summary>
    public const int MaxSteps = 64;

    /// <summary>Most entities one binding name may carry.</summary>
    public const int MaxEntitiesPerBinding = 16;

    /// <summary>Free disk space required before any persistent apply (design_B.md section 6.2), in MB.</summary>
    public const string MinFreeMb = "200";

    /// <summary>The standard guards added to every persistent fix (S9).</summary>
    public static IReadOnlyList<PlannedGuard> StandardPersistentGuards { get; } =
    [
        new(GuardIds.GameRunning, Empty, Tri.False, GuardSeverity.Block, "guard.game_running"),
        new(GuardIds.ClientRunning, Empty, Tri.False, GuardSeverity.Block, "guard.client_running"),
        new(GuardIds.PendingReboot, Empty, Tri.False, GuardSeverity.Block, "guard.pending_reboot"),
        new(GuardIds.WindowsUpdateInstalling, Empty, Tri.False, GuardSeverity.Block, "guard.wu_installing"),
        new(GuardIds.DiskFreeAbove, new Dictionary<string, string>(StringComparer.Ordinal) { ["mb"] = MinFreeMb }, Tri.True, GuardSeverity.Block, "guard.disk_full"),
        new(GuardIds.MonitorActive, Empty, Tri.False, GuardSeverity.Block, "guard.monitor_active"),
    ];

    /// <summary>The standard guard added to every session-only fix.</summary>
    public static IReadOnlyList<PlannedGuard> StandardSessionGuards { get; } =
    [
        new(GuardIds.MonitorActive, Empty, Tri.False, GuardSeverity.Block, "guard.monitor_active"),
    ];

    /// <summary>Firmware/boot changes with BitLocker on need the recovery key step first (S11): blocked here.</summary>
    public static PlannedGuard BitLockerGuard { get; } = new(GuardIds.BitLockerOn, Empty, Tri.False, GuardSeverity.Block, "guard.bitlocker_on");

    /// <summary>Plans a fix.</summary>
    /// <param name="fix">Fix definition.</param>
    /// <param name="binding">Binding.</param>
    /// <param name="registry">Primitives.</param>
    public static FixPlan Plan(FixDef fix, FixBinding binding, OperationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(fix);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(registry);
        var errors = new List<string>();
        ValidateBinding(fix, binding, errors);

        var steps = new List<PlannedStep>();
        foreach (var def in fix.Steps)
        {
            var op = registry.FindBase(def.Op);
            if (op is null)
            {
                errors.Add("op.unknown:" + def.Op);
            }

            foreach (var (args, entities) in Expand(def.Args, binding, errors))
            {
                if (steps.Count >= MaxSteps)
                {
                    errors.Add("fix.too_many_steps");
                    break;
                }

                if (op is not null && ArgValidator.Validate(op.Args, args) is { } problem)
                {
                    errors.Add(string.Create(CultureInfo.InvariantCulture, $"args.invalid:{steps.Count}:{problem}"));
                }

                if (op is not null && op.Op == OpIds.StartupDisable)
                {
                    foreach (var e in entities.Where(e => e.Kind == EntityKind.StartupItem))
                    {
                        var loc = e.Id.Split('|', 2)[0];
                        if (!string.Equals(loc, args.GetValueOrDefault("location"), StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add("binding.location_mismatch");
                        }
                    }
                }

                steps.Add(new PlannedStep(steps.Count, def, op, args, entities));
            }
        }

        var guards = new List<PlannedGuard>();
        guards.AddRange(fix.Scope == FixScope.Persistent ? StandardPersistentGuards : StandardSessionGuards);
        if (steps.Any(s => s.Op?.Op == OpIds.BcdSetValue))
        {
            guards.Add(BitLockerGuard);
        }

        foreach (var g in fix.Guards)
        {
            foreach (var (args, _) in Expand(g.Args, binding, errors))
            {
                if (!guards.Any(x => x.Probe == g.Probe && SameArgs(x.Args, args)))
                {
                    guards.Add(new PlannedGuard(g.Probe, args, g.Must, g.Severity, g.FailKey));
                }
            }
        }

        var verify = new List<PlannedVerify>();
        foreach (var v in fix.Verify)
        {
            var op = registry.FindBase(v.Probe);
            foreach (var (args, _) in Expand(v.Args, binding, errors))
            {
                if (op is not null)
                {
                    if (ArgValidator.Validate(op.Args, args, partial: true) is { } problem)
                    {
                        errors.Add("verify.args_invalid:" + problem);
                    }

                    if (v.Expect.Keys.Any(k => !op.StateKeys.Contains(k, StringComparer.Ordinal)))
                    {
                        errors.Add("verify.unknown_key:" + v.Probe);
                    }
                }

                verify.Add(new PlannedVerify(v, args, op));
            }
        }

        var ops = steps.Where(s => s.Op is not null).Select(s => s.Op!).ToList();
        var risk = ops.Select(o => o.RiskFloor).Append(fix.Risk).Max();
        var rev = ops.Select(o => o.Reversibility).Append(fix.Reversibility).Max();
        var reboot = ops.Select(o => o.Reboot).Append(fix.Reboot).Max();
        var admin = fix.Admin || ops.Any(o => o.RequiresAdmin);
        var network = ops.Any(o => o.AffectsNetwork)
                      || steps.Any(s => s.Op?.Op == OpIds.DeviceDisable && s.Entities.Any(e => e.Kind == EntityKind.Adapter));
        return new FixPlan
        {
            Fix = fix,
            Binding = binding,
            Steps = steps,
            Guards = guards,
            Verify = verify,
            Errors = [.. errors.Distinct(StringComparer.Ordinal)],
            Risk = risk,
            Reversibility = rev,
            Reboot = reboot,
            NeedsAdmin = admin,
            AffectsNetwork = network,
        };
    }

    /// <summary>Resolves <c>$name.id</c> / <c>$name.name</c> for one entity (StartupItem names come from the id).</summary>
    /// <param name="entity">Entity.</param>
    /// <param name="field"><c>id</c> or <c>name</c>.</param>
    public static string Resolve(EntityRef entity, string field)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (string.Equals(field, "id", StringComparison.Ordinal))
        {
            return entity.Id;
        }

        if (entity.Kind == EntityKind.StartupItem)
        {
            var bar = entity.Id.IndexOf('|', StringComparison.Ordinal);
            return bar >= 0 ? entity.Id[(bar + 1)..] : entity.Id;
        }

        return entity.DisplayName ?? entity.Id;
    }

    private static void ValidateBinding(FixDef fix, FixBinding binding, List<string> errors)
    {
        foreach (var name in binding.Entities.Keys)
        {
            if (!fix.Bind.ContainsKey(name))
            {
                errors.Add("binding.unknown:" + name);
            }
        }

        foreach (var (name, def) in fix.Bind)
        {
            if (!binding.Entities.TryGetValue(name, out var entities) || entities.Count == 0)
            {
                errors.Add("binding.missing:" + name);
                continue;
            }

            if (entities.Any(e => e.Kind != def.Kind))
            {
                errors.Add("binding.kind_mismatch:" + name);
            }

            if (def.Pick == BindPick.One && entities.Count != 1)
            {
                errors.Add("binding.not_one:" + name);
            }

            if (entities.Count > MaxEntitiesPerBinding)
            {
                errors.Add("binding.too_many:" + name);
            }

            if (entities.Any(e => string.IsNullOrWhiteSpace(e.Id) || e.Id.Length > 1024 || e.Id.Any(char.IsControl)))
            {
                errors.Add("binding.bad_id:" + name);
            }

            if (entities.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entities.Count)
            {
                errors.Add("binding.duplicate:" + name);
            }
        }
    }

    /// <summary>Expands argument templates over the bound entities they reference (cross product, binding order kept).</summary>
    private static IEnumerable<(Dictionary<string, string> Args, IReadOnlyList<EntityRef> Entities)> Expand(IReadOnlyDictionary<string, string> template, FixBinding binding, List<string> errors)
    {
        var refs = new List<string>();
        foreach (var value in template.Values)
        {
            if (!value.StartsWith('$'))
            {
                continue;
            }

            var m = Reference().Match(value);
            if (!m.Success)
            {
                errors.Add("args.bad_reference:" + value);
                continue;
            }

            var name = m.Groups["n"].Value;
            if (!binding.Entities.TryGetValue(name, out var list) || list.Count == 0)
            {
                errors.Add("binding.missing:" + name);
                continue;
            }

            if (!refs.Contains(name, StringComparer.Ordinal))
            {
                refs.Add(name);
            }
        }

        var combos = new List<Dictionary<string, EntityRef>> { new(StringComparer.Ordinal) };
        foreach (var name in refs)
        {
            var next = new List<Dictionary<string, EntityRef>>();
            foreach (var combo in combos)
            {
                foreach (var e in binding.Entities[name])
                {
                    next.Add(new Dictionary<string, EntityRef>(combo, StringComparer.Ordinal) { [name] = e });
                }
            }

            combos = next;
        }

        foreach (var combo in combos)
        {
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            var ok = true;
            foreach (var (key, value) in template)
            {
                if (!value.StartsWith('$'))
                {
                    args[key] = value;
                    continue;
                }

                var m = Reference().Match(value);
                if (!m.Success || !combo.TryGetValue(m.Groups["n"].Value, out var entity))
                {
                    ok = false;
                    break;
                }

                args[key] = Resolve(entity, m.Groups["f"].Value);
            }

            if (ok)
            {
                yield return (args, combo.Values.ToList());
            }
        }
    }

    private static bool SameArgs(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));

    [GeneratedRegex(@"^\$(?<n>[A-Za-z][A-Za-z0-9_]{0,31})\.(?<f>id|name)$", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}
