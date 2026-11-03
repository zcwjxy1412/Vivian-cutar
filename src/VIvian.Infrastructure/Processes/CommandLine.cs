using System.Text;

namespace VIvian.Infrastructure.Processes;

/// <summary>
/// Builds a Windows command line that the standard C runtime parser splits back into exactly the given
/// arguments. Nothing goes through a shell, so characters such as &amp; | &gt; are passed literally.
/// </summary>
public static class CommandLine
{
    public static string Build(string fileName, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (fileName.Contains('"')) throw new ArgumentException("程序路径不能包含双引号。", nameof(fileName));
        var builder = new StringBuilder();
        builder.Append('"').Append(fileName).Append('"');
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (argument.Contains('\0')) throw new ArgumentException("参数不能包含空字符。", nameof(arguments));
            builder.Append(' ');
            AppendQuoted(builder, argument);
        }
        return builder.ToString();
    }

    private static void AppendQuoted(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            if (ch == '"')
            {
                // Backslashes before a quote must be doubled, and the quote itself escaped.
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(ch);
            }
            backslashes = 0;
        }
        // Backslashes before the closing quote must be doubled so they do not escape it.
        builder.Append('\\', backslashes * 2).Append('"');
    }

    /// <summary>Builds a sorted Unicode environment block ("k=v\0...\0\0") from the current environment plus overrides.</summary>
    public static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        foreach (var (key, value) in overrides)
        {
            if (string.IsNullOrEmpty(key) || key.Contains('=') || key.Contains('\0') || value.Contains('\0'))
                throw new ArgumentException("环境变量名称或值无效。", nameof(overrides));
            variables[key] = value;
        }
        var block = new StringBuilder();
        foreach (var (key, value) in variables) block.Append(key).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return block.ToString().ToCharArray();
    }
}
