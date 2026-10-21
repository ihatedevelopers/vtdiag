using System.Text.RegularExpressions;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;

namespace VTD.Knowledge.Lint;

/// <summary>
/// KB lint (A2 done-criteria): every signal / fix / guide / question / code / symptom / table reference resolves,
/// every <c>op</c>, argument and verify key exists in <see cref="OpCatalog"/>, network-cutting fixes declare a deadman,
/// high-risk fixes have no steps, effective risk is never declared lower than a primitive's floor, pack collectors stay
/// inside <see cref="PackCollectorLimits"/>, regexes compile, and every string key exists in English (Turkish gaps and
/// placeholder mismatches are reported too). Pure: runs on any <see cref="KnowledgeBase"/>.
/// </summary>
public static class KbLinter
{
    private static readonly HashSet<string> SettingsAllowlist = new(StringComparer.Ordinal)
    {
        "ms-settings:windowsdefender", "ms-settings:bluetooth", "ms-settings:sound", "ms-settings:apps-volume", "ms-settings:display",
        "ms-settings:display-advancedgraphics", "ms-settings:windowsupdate", "ms-settings:windowsupdate-history",
        "ms-settings:windowsinsider", "ms-settings:appsfeatures", "ms-settings:startupapps", "ms-settings:dateandtime",
        "ms-settings:powersleep", "ms-settings:network-status", "ms-settings:network-advancedsettings", "ms-settings:recovery",
        "ms-settings:about", "ms-settings:gaming-gamebar", "ms-settings:mousetouchpad", "ms-settings:privacy-microphone",
    };

    /// <summary>The <c>ms-settings:</c> pages a guide may open.</summary>
    public static IReadOnlySet<string> AllowedSettingsPages => SettingsAllowlist;

    /// <summary>Lints a KB.</summary>
    /// <param name="kb">KB.</param>
    public static IReadOnlyList<KbIssue> Lint(KnowledgeBase kb)
    {
        ArgumentNullException.ThrowIfNull(kb);
        var ctx = new Ctx(kb);
        ctx.Signals();
        ctx.Collectors();
        ctx.Rules();
        ctx.Fixes();
        ctx.Guides();
        ctx.Questions();
        ctx.Codes();
        ctx.Conflicts();
        ctx.Strings();
        return ctx.Issues;
    }

    private sealed class Ctx(KnowledgeBase kb)
    {
        private readonly HashSet<string> _signals = kb.Signals.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _rules = kb.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _fixes = kb.Fixes.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _guides = kb.Guides.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _questions = kb.Questions.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _symptoms = kb.Symptoms.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _tables = kb.Tables.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _collectors = kb.Collectors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _guards = GuardIds.All.ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public List<KbIssue> Issues { get; } = [];

        public void Signals()
        {
            foreach (var s in kb.Signals)
            {
                Key(s.TitleKey, "signal " + s.Id);
                if (s.Source == SignalSource.Collector)
                {
                    if (s.FromCollector is null)
                    {
                        Error("ref.collector_spec", $"signal {s.Id}", "Collector signal without fromCollector.");
                    }
                    else if (!_collectors.Contains(s.FromCollector.Collector))
                    {
                        Error("ref.unknown_collector", $"signal {s.Id}", $"Unknown collector {s.FromCollector.Collector}.");
                    }
                }

                if (s.Source == SignalSource.Ask && (s.Question is null || !_questions.Contains(s.Question)))
                {
                    Error("ref.unknown_question", $"signal {s.Id}", $"Ask signal without a known question ({s.Question}).");
                }
            }
        }

        public void Collectors()
        {
            foreach (var c in kb.Collectors)
            {
                var where = "collector " + c.Id;
                Key(c.DescriptionKey, where);
                switch (c)
                {
                    case EventLogCollectorDef e:
                        if (e.SinceDays is < 1 or > PackCollectorLimits.MaxSinceDays)
                        {
                            Error("collector.limits", where, "sinceDays out of range.");
                        }

                        if (e.Max is < 1 or > PackCollectorLimits.MaxEvents)
                        {
                            Error("collector.limits", where, "max out of range.");
                        }

                        if (e.Providers.Count == 0 && e.EventIds.Count == 0)
                        {
                            Error("collector.unbounded", where, "An event collector needs providers or event ids.");
                        }

                        break;
                    case UninstallRegexCollectorDef u:
                        CheckRegex(u.DisplayNameRegex, where);
                        CheckRegex(u.PublisherRegex, where);
                        break;
                    case LoadedDriverCollectorDef d:
                        CheckRegex(d.NameRegex, where);
                        break;
                    case FaultingModuleCollectorDef f:
                        if (f.SinceDays is < 1 or > PackCollectorLimits.MaxSinceDays || f.Modules.Count == 0)
                        {
                            Error("collector.limits", where, "sinceDays out of range or no modules.");
                        }

                        break;
                    case RegistryValueCollectorDef r:
                        if (!RegistryAllowed(r.Hive, r.Key))
                        {
                            Error("collector.registry_root", where, $"Registry key not allowed: {r.Key}.");
                        }

                        if (r.Values.Count > PackCollectorLimits.MaxRegistryValues)
                        {
                            Error("collector.limits", where, "Too many registry values.");
                        }

                        break;
                    case FileExistsCollectorDef fe:
                        if (!PathAllowed(fe.Path))
                        {
                            Error("collector.path", where, $"File path not allowed: {fe.Path}.");
                        }

                        break;
                }
            }
        }

        public void Rules()
        {
            foreach (var r in kb.Rules)
            {
                var where = "rule " + r.Id;
                Key(r.TitleKey, where);
                Key(r.SummaryKey, where);
                foreach (var s in r.Requires.Concat(r.Excludes))
                {
                    Signal(s, where);
                }

                foreach (var e in r.Evidence)
                {
                    Signal(e.Signal, where);
                    Key(e.TextKey, where);
                    if (e.Ask is not null && !_questions.Contains(e.Ask))
                    {
                        Error("ref.unknown_question", where, e.Ask);
                    }

                    if (e.WhenTrue is null && e.WhenFalse is null && e.Veto is null)
                    {
                        Warn("rule.empty_evidence", where, $"Evidence {e.Id} has no weight and no veto.");
                    }

                    foreach (var sy in e.OnlyIfSymptoms)
                    {
                        Symptom(sy, where);
                    }
                }

                foreach (var sy in r.Symptoms)
                {
                    Symptom(sy, where);
                }

                foreach (var f in r.Fixes)
                {
                    Ref(_fixes, f, "fix", where);
                }

                foreach (var g in r.Guides)
                {
                    Ref(_guides, g, "guide", where);
                }

                foreach (var q in r.Questions)
                {
                    Ref(_questions, q, "question", where);
                }

                foreach (var x in r.Supersedes.Concat(r.Related))
                {
                    Ref(_rules, x, "rule", where);
                }

                if (r.Kind == RuleKind.Route && (r.Route is null || r.Requires.Count == 0))
                {
                    Error("rule.route", where, "A route rule needs a route kind and at least one required signal.");
                }

                if (r.Kind != RuleKind.Route && r.Route is not null)
                {
                    Error("rule.route", where, "Only route rules carry a route.");
                }

                if (r.Prior.Override is not null && string.IsNullOrWhiteSpace(r.Prior.OverrideReason))
                {
                    Error("rule.prior_override", where, "A prior override needs a reason.");
                }

                if (r.Wave is not null && r.Wave.To < r.Wave.From)
                {
                    Error("rule.wave", where, "Wave ends before it starts.");
                }
            }
        }

        public void Fixes()
        {
            foreach (var f in kb.Fixes)
            {
                var where = "fix " + f.Id;
                Key(f.TitleKey, where);
                Key(f.ExplainKey, where);
                Key(f.UndoExplainKey, where);
                Key(f.NoticeKey, where);
                foreach (var k in f.SideEffectKeys)
                {
                    Key(k, where);
                }

                Key(f.Outcome?.AskKey, where);
                Key(f.Drift?.ReasonKey, where);
                foreach (var h in f.AppliesTo)
                {
                    Ref(_rules, h, "rule", where);
                }

                foreach (var (name, b) in f.Bind)
                {
                    if (b.FromSignal is not null)
                    {
                        Signal(b.FromSignal, where + " bind " + name);
                    }
                    else if (!f.Template)
                    {
                        Error("fix.bind", where, $"Binding {name} has no source signal and the fix is not a template.");
                    }
                }

                foreach (var g in f.Guards)
                {
                    if (!_guards.Contains(g.Probe) && !_signals.Contains(g.Probe))
                    {
                        Error("ref.unknown_probe", where, g.Probe);
                    }

                    Key(g.FailKey, where);
                    BindRefs(f, g.Args.Values, where);
                }

                var networkCutting = false;
                var floor = RiskLevel.Low;
                foreach (var st in f.Steps)
                {
                    var op = OpCatalog.Find(st.Op);
                    if (op is null)
                    {
                        Error("ref.unknown_op", where, st.Op);
                        continue;
                    }

                    floor = (RiskLevel)Math.Max((int)floor, (int)op.RiskFloor);
                    networkCutting |= op.AffectsNetwork || (st.Op == OpIds.DeviceDisable && f.Bind.Values.Any(b => b.Kind == EntityKind.Adapter));
                    Args(op, st.Args, where);
                    BindRefs(f, st.Args.Values, where);
                }

                foreach (var v in f.Verify)
                {
                    var op = OpCatalog.Find(v.Probe);
                    if (op is null)
                    {
                        if (!_guards.Contains(v.Probe))
                        {
                            Error("ref.unknown_probe", where, "verify " + v.Probe);
                        }

                        continue;
                    }

                    foreach (var key in v.Expect.Keys.Where(k => !op.StateKeys.Contains(k, StringComparer.Ordinal)))
                    {
                        Error("fix.verify_key", where, $"{key} is not a state key of {v.Probe}.");
                    }

                    BindRefs(f, v.Args.Values, where);
                }

                if (networkCutting && f.Deadman is null)
                {
                    Error("fix.deadman", where, "A network-affecting fix must declare a deadman.");
                }

                if (f.Risk == RiskLevel.High && f.Steps.Count > 0)
                {
                    Error("fix.high_risk", where, "High-risk fixes are guide only (no steps).");
                }

                if (f.Risk < floor)
                {
                    Error("fix.risk_floor", where, $"Declared risk {f.Risk} is below the primitives' floor {floor}.");
                }

                if (f.Steps.Count == 0)
                {
                    Error("fix.no_steps", where, "A fix needs at least one step.");
                }

                if (f.Steps.Any(s => s.Op == OpIds.BcdSetValue))
                {
                    Error("fix.guide_only_op", where, "BCD changes are guided, not automated, in this KB.");
                }

                if (f.SuccessPrior is { } sp && (sp.Success < 0 || sp.Trials < sp.Success || sp.Trials <= 0))
                {
                    Error("fix.success_prior", where, "Success prior needs 0 <= success <= trials and trials > 0.");
                }
            }
        }

        public void Guides()
        {
            foreach (var g in kb.Guides)
            {
                var where = "guide " + g.Id;
                Key(g.TitleKey, where);
                Key(g.SummaryKey, where);
                Key(g.OutcomeAskKey, where);
                foreach (var h in g.AppliesTo)
                {
                    Ref(_rules, h, "rule", where);
                }

                foreach (var p in g.Preflight)
                {
                    if (!_guards.Contains(p.Probe) && !_signals.Contains(p.Probe))
                    {
                        Error("ref.unknown_probe", where, p.Probe);
                    }

                    Key(p.FailKey, where);
                }

                var branchIds = g.Branches.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
                if (branchIds.Count != g.Branches.Count)
                {
                    Error("guide.duplicate_branch", where, "Branch ids must be unique.");
                }

                if (!g.Branches.Any(b => b.When.Count == 0))
                {
                    Error("guide.no_default", where, "A guide needs a default branch (no conditions).");
                }

                foreach (var b in g.Branches)
                {
                    Key(b.TextKey, where);
                    foreach (var c in b.When.Concat(b.Verify))
                    {
                        Signal(c.Signal, where + "/" + b.Id);
                    }

                    if (b.NextOnFail is not null && !branchIds.Contains(b.NextOnFail))
                    {
                        Error("ref.unknown_branch", where, b.NextOnFail);
                    }

                    if (b.Steps.Count == 0)
                    {
                        Error("guide.empty_branch", where + "/" + b.Id, "A branch needs steps.");
                    }

                    foreach (var st in b.Steps)
                    {
                        Key(st.TextKey, where);
                        if (st.VendorTable is not null)
                        {
                            Ref(_tables, st.VendorTable, "table", where);
                        }

                        switch (st.Action)
                        {
                            case GuideAction.RunFix:
                                Ref(_fixes, st.Target ?? string.Empty, "fix", where);
                                break;
                            case GuideAction.RunCheck:
                                if (st.Target is null || (!_signals.Contains(st.Target) && !_guards.Contains(st.Target)))
                                {
                                    Error("ref.unknown_probe", where, "runCheck " + st.Target);
                                }

                                break;
                            case GuideAction.OpenSettings:
                                if (st.Target is null || !SettingsAllowlist.Contains(st.Target))
                                {
                                    Error("guide.settings_page", where, $"Settings page not allowlisted: {st.Target}.");
                                }

                                break;
                            case GuideAction.CopyText:
                                if (string.IsNullOrWhiteSpace(st.Target))
                                {
                                    Error("guide.copy_text", where, "copyText needs the text to copy.");
                                }

                                break;
                        }
                    }
                }
            }
        }

        public void Questions()
        {
            foreach (var q in kb.Questions)
            {
                var where = "question " + q.Id;
                if (!q.Id.StartsWith("ask.", StringComparison.Ordinal))
                {
                    Error("question.id", where, "Question ids start with ask.");
                }

                if (!_signals.Contains(q.Id))
                {
                    Error("ref.unknown_signal", where, "The question's signal is not declared.");
                }

                Key(q.TextKey, where);
                Key(q.HelpKey, where);
                foreach (var o in q.Options)
                {
                    Key(o.TextKey, where);
                }

                foreach (var s in q.Symptoms)
                {
                    Symptom(s, where);
                }

                foreach (var c in q.AskIf)
                {
                    Signal(c.Signal, where);
                }
            }

            foreach (var s in kb.Symptoms)
            {
                Key(s.TitleKey, "symptom " + s.Id);
            }
        }

        public void Codes()
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in kb.ErrorCodes)
            {
                var where = "code " + c.Id;
                Key(c.MeaningKey, where);
                foreach (var code in c.Codes)
                {
                    var n = CodeNormalizer.Normalize(code);
                    if (!string.Equals(n, code, StringComparison.Ordinal))
                    {
                        Error("code.not_normalized", where, $"{code} is not in normalized form ({n}).");
                    }

                    if (!seen.TryAdd(n, c.Id))
                    {
                        Error("code.duplicate", where, $"{code} is also in {seen[n]}.");
                    }
                }

                foreach (var p in c.PointsTo)
                {
                    Ref(_rules, p.Hypothesis, "rule", where);
                    if (p.Typed > p.Logged)
                    {
                        Error("code.typed_gt_logged", where, $"Typed strength exceeds logged for {p.Hypothesis}.");
                    }
                }

                if (c.AskIfTyped is not null)
                {
                    Ref(_questions, c.AskIfTyped, "question", where);
                }

                if (c.Route is null && c.PointsTo.Count == 0)
                {
                    Warn("code.no_causes", where, "Code has no route and points to no cause.");
                }

                foreach (var u in c.OfficialUrls.Where(u => !u.StartsWith("https://", StringComparison.Ordinal)))
                {
                    Error("code.url", where, $"Official URL must be https: {u}.");
                }
            }
        }

        public void Conflicts()
        {
            foreach (var c in kb.Conflicts)
            {
                var where = "conflict " + c.Id;
                foreach (var k in c.EffectKeys)
                {
                    Key(k, where);
                }

                Key(c.Fix.ActionKey, where);
                Key(c.NotesKey, where);
                foreach (var f in c.Fix.FixIds)
                {
                    Ref(_fixes, f, "fix", where);
                }

                foreach (var g in c.Fix.GuideIds)
                {
                    Ref(_guides, g, "guide", where);
                }

                CheckRegex(c.Detect.UninstallDisplayNameRegex, where);
                CheckRegex(c.Detect.NetworkAdapterRegex, where);
                CheckRegex(c.Detect.AudioEndpointRegex, where);
                foreach (var s in c.Symptoms)
                {
                    Symptom(s, where);
                }
            }
        }

        public void Strings()
        {
            var en = kb.Keys(VTD.Contracts.Languages.English).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _keys.Order(StringComparer.Ordinal))
            {
                if (!en.Contains(key))
                {
                    Error("string.missing_en", key, "Referenced string key has no English text.");
                }
            }

            foreach (var lang in kb.Languages.Where(l => !string.Equals(l, VTD.Contracts.Languages.English, StringComparison.Ordinal)))
            {
                foreach (var key in en.Order(StringComparer.Ordinal))
                {
                    var other = kb.RawText(key, lang);
                    if (other is null)
                    {
                        Warn("string.missing_" + lang, key, $"No {lang} text (falls back to English).");
                        continue;
                    }

                    var a = MessageFormatter.PlaceholderNames(kb.RawText(key, VTD.Contracts.Languages.English)!);
                    var b = MessageFormatter.PlaceholderNames(other);
                    if (!a.SetEquals(b))
                    {
                        Error("string.placeholders", key, $"{lang} placeholders differ from English.");
                    }
                }
            }
        }

        private void Args(OpSpec op, IReadOnlyDictionary<string, string> args, string where)
        {
            foreach (var (name, value) in args)
            {
                var spec = op.Args.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
                if (spec is null)
                {
                    Error("fix.arg_unknown", where, $"{op.Op} has no argument {name}.");
                    continue;
                }

                if (value.StartsWith('$'))
                {
                    continue;
                }

                if (spec.AllowedValues.Count > 0 && !spec.AllowedValues.Contains(value, StringComparer.Ordinal))
                {
                    Error("fix.arg_value", where, $"{op.Op}.{name}={value} is not allowed.");
                }

                if (spec.Pattern is not null && !System.Text.RegularExpressions.Regex.IsMatch(value, spec.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    Error("fix.arg_pattern", where, $"{op.Op}.{name}={value} fails {spec.Pattern}.");
                }
            }

            foreach (var spec in op.Args.Where(a => a.Required && !args.ContainsKey(a.Name)))
            {
                Error("fix.arg_missing", where, $"{op.Op} needs {spec.Name}.");
            }
        }

        private void BindRefs(FixDef f, IEnumerable<string> values, string where)
        {
            foreach (var v in values)
            {
                foreach (Match m in BindRef().Matches(v))
                {
                    if (!f.Bind.ContainsKey(m.Groups[1].Value))
                    {
                        Error("fix.unbound", where, $"${m.Groups[1].Value} is not bound.");
                    }
                }
            }
        }

        private void Signal(string id, string where)
        {
            if (_signals.Contains(id))
            {
                return;
            }

            if (id.Contains('*', StringComparison.Ordinal))
            {
                var rx = new Regex("^" + System.Text.RegularExpressions.Regex.Escape(id).Replace("\\*", "[^.]+", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);
                if (_signals.Any(s => rx.IsMatch(s)))
                {
                    return;
                }
            }

            Error("ref.unknown_signal", where, id);
        }

        private void Symptom(string id, string where)
        {
            if (!_symptoms.Contains(id))
            {
                Error("ref.unknown_symptom", where, id);
            }
        }

        private void Ref(HashSet<string> set, string id, string what, string where)
        {
            if (!set.Contains(id))
            {
                Error("ref.unknown_" + what, where, id);
            }
        }

        private void Key(string? key, string where)
        {
            if (key is not null)
            {
                _keys.Add(key);
            }
        }

        private void CheckRegex(string? pattern, string where)
        {
            if (pattern is null)
            {
                return;
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, PackCollectorLimits.RegexTimeout);
            }
            catch (ArgumentException e)
            {
                Error("regex.invalid", where, e.Message);
            }
        }

        private void Error(string code, string? path, string message) => Issues.Add(new KbIssue { Code = code, Severity = KbIssueSeverity.Error, Path = path, Message = message });

        private void Warn(string code, string? path, string message) => Issues.Add(new KbIssue { Code = code, Severity = KbIssueSeverity.Warning, Path = path, Message = message });
    }

    /// <summary>True when a registry collector key is inside the allowlist and outside the denied prefixes.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="key">Key path.</param>
    public static bool RegistryAllowed(RegistryHiveScope hive, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var normalized = ControlSet().Replace(key, "CurrentControlSet");
        var full = hive == RegistryHiveScope.HkcuPlayer ? "HKCU:" + normalized : normalized;
        if (PackCollectorLimits.DeniedRegistryPrefixes.Any(d => full.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return PackCollectorLimits.AllowedRegistryRoots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            && (full.Length == r.Length || full[r.Length] == '\\'));
    }

    /// <summary>True when a file collector path starts with an allowed token and has no traversal, wildcard, UNC or device part.</summary>
    /// <param name="path">Path template.</param>
    public static bool PathAllowed(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!PackCollectorLimits.AllowedPathTokens.Any(t => path.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !path.Contains("..", StringComparison.Ordinal) && !path.Contains('*', StringComparison.Ordinal) && !path.Contains('?', StringComparison.Ordinal)
               && !path.Contains("\\\\", StringComparison.Ordinal) && !path.Contains('/', StringComparison.Ordinal)
               && !path[1..].Contains(':', StringComparison.Ordinal);
    }

    private static Regex BindRef() => BindRefRegex;

    private static Regex ControlSet() => ControlSetRegex;

    private static readonly Regex BindRefRegex = new(@"\$([A-Za-z]+)\.(id|name)", RegexOptions.CultureInvariant);

    private static readonly Regex ControlSetRegex = new(@"ControlSet\d{3}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
