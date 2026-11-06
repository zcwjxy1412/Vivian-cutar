using System.Text.RegularExpressions;

namespace VIvian.Infrastructure.Diagnostics;

/// <summary>
/// Removes personal and secret-looking values from text before it leaves the machine (diagnostic bundles, evidence
/// exports). It is deliberately conservative: it may hide harmless text, it must not leak the user's identity.
/// </summary>
public sealed partial class Redactor
{
    private readonly List<(string Value, string Replacement)> _literals = [];

    public Redactor(string? userProfile, string? userName, string? machineName, IEnumerable<string>? extraSecrets = null)
    {
        if (!string.IsNullOrWhiteSpace(userProfile) && userProfile.Length >= 4)
            _literals.Add((userProfile.TrimEnd('\\', '/'), "%USERPROFILE%"));
        // Two-character names are common in CJK (e.g. a Chinese account name); ASCII names need three to avoid noise.
        if (!string.IsNullOrWhiteSpace(userName) && (userName.Length >= 3 || (userName.Length == 2 && userName.Any(c => c > 0x7F))))
            _literals.Add((userName, "<user>"));
        if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length >= 3) _literals.Add((machineName, "<machine>"));
        foreach (var secret in extraSecrets ?? [])
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 4) _literals.Add((secret, "<secret>"));
        // Longest first, so a profile path is replaced before the user name inside it.
        _literals.Sort((a, b) => b.Value.Length.CompareTo(a.Value.Length));
    }

    /// <summary>The redactor for the current Windows user.</summary>
    public static Redactor ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = text;
        foreach (var (value, replacement) in _literals)
            result = result.Replace(value, replacement, StringComparison.OrdinalIgnoreCase);
        result = SecretAssignment().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + "<redacted>");
        result = Email().Replace(result, "<email>");
        result = BearerToken().Replace(result, "$1<redacted>");
        result = OtherUserProfile().Replace(result, "$1<user>");
        return result;
    }

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|api[_-]?key|access[_-]?key|cookie|session|authorization|uid|account)(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    /// <summary>Any C:\Users\name path (another account, or a profile under a different drive letter).</summary>
    [GeneratedRegex(@"(?i)([a-z]:\\users\\)(?!public\\|default\\)[^\\/:*?""<>|\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex OtherUserProfile();
}
