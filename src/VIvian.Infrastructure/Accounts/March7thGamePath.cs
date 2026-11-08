using System.Text.Json;
using System.Text.RegularExpressions;

namespace VIvian.Infrastructure.Accounts;

/// <summary>Reads the upstream's own game_path setting. No path is guessed or hard-coded.</summary>
public static class March7thGamePath
{
    public static string Read(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory)) return "";
        var config = Path.Combine(installDirectory, "config.yaml");
        if (!File.Exists(config)) return "";
        var lines = Engines.YamlScalarEditor.Lines(File.ReadAllText(config));
        var location = Engines.YamlScalarEditor.Locate(lines, "game_path");
        var value = location?.Value;
        if (value is null)
        {
            // The editor deliberately refuses backslashes inside quotes for writes; reading a path is separate.
            var declarations = lines.Where(l => l.StartsWith("game_path:", StringComparison.Ordinal)).ToArray();
            if (declarations.Length != 1) return "";
            var raw = declarations[0]["game_path:".Length..].Trim();
            var single = Regex.Match(raw, @"^'((?:[^']|'')*)'\s*(?:#.*)?$");
            if (single.Success) value = single.Groups[1].Value.Replace("''", "'");
            else
            {
                var quoted = Regex.Match(raw, """^"((?:[^"\\]|\\.)*)"\s*(?:#.*)?$""");
                if (!quoted.Success) return "";
                try { value = JsonSerializer.Deserialize<string>("\"" + quoted.Groups[1].Value + "\""); }
                catch (JsonException) { return ""; }
            }
        }
        return value is not null && File.Exists(value) ? value : "";
    }
}
