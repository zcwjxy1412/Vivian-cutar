namespace VIvian.Infrastructure.Accounts;

/// <summary>Reads the upstream's own game_path setting. No path is guessed or hard-coded.</summary>
public static class March7thGamePath
{
    public static string Read(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory)) return "";
        var config = Path.Combine(installDirectory, "config.yaml");
        if (!File.Exists(config)) return "";
        foreach (var line in File.ReadLines(config))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("game_path:", StringComparison.Ordinal)) continue;
            var value = trimmed["game_path:".Length..].Trim().Trim('"', '\'');
            return File.Exists(value) ? value : "";
        }
        return "";
    }
}
