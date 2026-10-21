using System.Text.RegularExpressions;

namespace VTD.Reporting;

/// <summary>How much a text scrub removes.</summary>
internal enum ScrubMode
{
    /// <summary>Local level: secrets only (tokens, key=value secrets, passwords, ids next to their key, JWTs, lockfile passwords).</summary>
    SecretsOnly = 0,

    /// <summary>Share level: secrets plus e-mails, SIDs, profile paths, OneDrive organizations, default computer names, MAC
    /// addresses, network ids next to their key and every known identifier value.</summary>
    Full = 1,
}

/// <summary>Records which rule changed what (for the manifest and <see cref="Contracts.Snapshots.RedactionState.AppliedRules"/>).</summary>
/// <param name="rule">Rule id.</param>
/// <param name="count">Replacements.</param>
internal delegate void RuleSink(string rule, int count);

/// <summary>Applies <see cref="TextPatterns"/> to one string.</summary>
internal static class TextScrubber
{
    /// <summary>Rule ids.</summary>
    public static class Rules
    {
        public const string CommandLine = "secret.command_line";
        public const string AuthToken = "secret.auth_token";
        public const string KeyValue = "secret.key_value";
        public const string Password = "secret.password";
        public const string Bearer = "secret.bearer";
        public const string AuthScheme = "secret.auth_scheme";
        public const string Lockfile = "secret.lockfile";
        public const string IdNextToKey = "secret.id_next_to_key";
        public const string Jwt = "secret.jwt";
        public const string Email = "pii.email";
        public const string Sid = "pii.sid";
        public const string NetworkKey = "pii.network_id";
        public const string Mac = "pii.mac";
        public const string Profile = "path.profile";
        public const string OneDrive = "path.onedrive_org";
        public const string DefaultHost = "host.default_name";
        public const string Identifier = "identifier.value";
    }

    /// <summary>Scrubs one string.</summary>
    /// <param name="text">Text.</param>
    /// <param name="mode">Mode.</param>
    /// <param name="identifiers">Identifier values to replace (Full mode).</param>
    /// <param name="sink">Rule sink (may be null).</param>
    /// <param name="identifierReplacement">How an identifier is replaced (default <see cref="TextPatterns.IdPlaceholder"/>;
    /// entity ids use a salted hash so they stay distinct and resolvable).</param>
    public static string Scrub(string text, ScrubMode mode, IdentifierSet identifiers, RuleSink? sink, Func<string, string>? identifierReplacement = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var s = text;
        s = Replace(s, TextPatterns.CommandLine(), m => m.Groups[1].Value + "=" + TextPatterns.Redacted, Rules.CommandLine, sink);
        s = KeyedSecret(s, TextPatterns.AuthFlag(), Rules.AuthToken, sink);
        s = KeyedSecret(s, TextPatterns.PasswordValue(), Rules.Password, sink);
        s = KeyedSecret(s, TextPatterns.SecretKeyValue(), Rules.KeyValue, sink);
        s = KeyedSecret(s, TextPatterns.Bearer(), Rules.Bearer, sink);
        s = KeyedSecret(s, TextPatterns.OtherAuthScheme(), Rules.AuthScheme, sink);
        s = KeyedSecret(s, TextPatterns.Lockfile(), Rules.Lockfile, sink);
        s = KeyedSecret(s, TextPatterns.IdNextToKey(), Rules.IdNextToKey, sink);
        s = Replace(s, TextPatterns.Jwt(), _ => "[jwt]", Rules.Jwt, sink);
        if (mode == ScrubMode.SecretsOnly)
        {
            return s;
        }

        s = Replace(s, TextPatterns.Email(), _ => "[email]", Rules.Email, sink);
        s = Replace(s, TextPatterns.Sid(), _ => "[sid]", Rules.Sid, sink);
        s = KeyedSecret(s, TextPatterns.NetworkKeyValue(), Rules.NetworkKey, sink);
        s = Replace(s, TextPatterns.MacAddress(), _ => "[mac]", Rules.Mac, sink);
        s = ReplaceProfiles(s, sink);
        s = Replace(s, TextPatterns.OneDriveOrg(), m => IsPlaceholder(m.Groups[2].Value) ? m.Value : m.Groups[1].Value + "[org]", Rules.OneDrive, sink);
        s = Replace(s, TextPatterns.DefaultComputerName(), _ => "[host]", Rules.DefaultHost, sink);
        s = identifiers.Replace(s, sink, identifierReplacement);
        return s;
    }

    /// <summary>User names that appear in profile paths (<c>C:\Users\&lt;name&gt;</c>), for the identifier first pass.</summary>
    /// <param name="text">Text.</param>
    public static IEnumerable<string> ProfileNames(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match m in TextPatterns.ProfilePath().Matches(text))
        {
            var name = m.Groups["name"].Value.Trim();
            if (!TextPatterns.NeutralProfileNames.Contains(name) && !TextPatterns.PlaceholderProfileNames.Contains(name) && !name.StartsWith('%'))
            {
                yield return name;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> starts with a placeholder this redactor writes (<c>[redacted]</c>, <c>[id]</c>,
    /// <c>[jwt]</c>...), also when Markdown-escaped (<c>\[redacted\]</c>). "Starts with" keeps scrub and sweep idempotent
    /// when the value runs on (<c>password=[redacted] · next</c>).
    /// </summary>
    /// <param name="value">Value.</param>
    public static bool IsPlaceholder(string value)
    {
        var v = value.Replace("\\", string.Empty, StringComparison.Ordinal);
        var close = v.IndexOf(']', StringComparison.Ordinal);
        return v.Length > 2 && v[0] == '[' && close > 1 && Placeholders.Contains(v[1..close]);
    }

    private static readonly HashSet<string> Placeholders = new(["redacted", "id", "jwt", "email", "sid", "mac", "host", "org", "ip"], StringComparer.Ordinal);

    private static string KeyedSecret(string s, Regex regex, string rule, RuleSink? sink)
        => Replace(s, regex, m => IsPlaceholder(m.Groups[3].Value.Trim()) ? m.Value : m.Groups[1].Value + m.Groups[2].Value + TextPatterns.Redacted, rule, sink);

    private static string ReplaceProfiles(string s, RuleSink? sink)
        => Replace(
            s,
            TextPatterns.ProfilePath(),
            m =>
            {
                var name = m.Groups["name"].Value.Trim();
                return TextPatterns.NeutralProfileNames.Contains(name) ? m.Value : TextPatterns.UserProfile;
            },
            Rules.Profile,
            sink);

    private static string Replace(string s, Regex regex, Func<Match, string> evaluator, string rule, RuleSink? sink)
    {
        var count = 0;
        var result = regex.Replace(s, m =>
        {
            var replacement = evaluator(m);
            if (!string.Equals(replacement, m.Value, StringComparison.Ordinal))
            {
                count++;
            }

            return replacement;
        });
        if (count > 0)
        {
            sink?.Invoke(rule, count);
        }

        return result;
    }
}
