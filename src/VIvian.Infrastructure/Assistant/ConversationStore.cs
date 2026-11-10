using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VIvian.Infrastructure.Assistant;

public sealed class ConversationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Summary { get; set; } = "";
    public List<JsonArray> Turns { get; set; } = [];
    public string DisplayName => $"{StartedAt.LocalDateTime:g} · {Turns.Count} 轮";
}

public sealed class ConversationState
{
    public int Version { get; set; } = 1;
    public ConversationRecord Active { get; set; } = new();
    public List<ConversationRecord> Archived { get; set; } = [];
    public ConversationState Copy() => JsonSerializer.Deserialize<ConversationState>(JsonSerializer.Serialize(this))!;
}

/// <summary>One encrypted snapshot commits archival and the active conversation together.</summary>
public sealed class ConversationStore
{
    private const int MaxBytes = 32 * 1024 * 1024;
    private readonly string _path;
    private string? _expectedHash;
    private bool _loaded;
    public ConversationStore(string directory, AssistantEndpoint endpoint)
    {
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            endpoint.ChatUri?.AbsoluteUri + "\n" + endpoint.Model + "\n" + endpoint.ApiKey)));
        _path = Path.Combine(directory, "assistant-conversations", scope + ".bin");
    }

    public ConversationState Load()
    {
        try
        {
            RequireSafeDirectory();
            using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!File.Exists(_path)) { _loaded = true; _expectedHash = null; return new(); }
            var bytes = ReadBytes();
            var state = JsonSerializer.Deserialize<ConversationState>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            Validate(state);
            _expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
            _loaded = true;
            return state!;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
        { throw new AssistantException("对话存档无法验证，已保留原文件。请先恢复存档，不能覆盖继续。"); }
    }

    public void Save(ConversationState state)
    {
        if (!_loaded) throw new InvalidOperationException("Load the conversation before saving.");
        Validate(state);
        var plain = JsonSerializer.SerializeToUtf8Bytes(state);
        if (plain.Length > MaxBytes - 4096) throw new IOException("Conversation archive size limit reached.");
        var bytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        RequireSafeDirectory();
        using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var actual = File.Exists(_path) ? Convert.ToHexString(SHA256.HashData(ReadBytes())) : null;
        if (actual != _expectedHash) throw new IOException("Conversation changed in another session.");
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
            _expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private byte[] ReadBytes()
    {
        var info = new FileInfo(_path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxBytes)
            throw new InvalidDataException("Unsafe or oversized conversation file.");
        return File.ReadAllBytes(_path);
    }

    private void RequireSafeDirectory()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Conversation encryption requires Windows.");
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Conversation directory must not use links.");
        if (File.Exists(_path + ".lock") && (File.GetAttributes(_path + ".lock") & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Conversation lock must not use links.");
    }

    private static void Validate(ConversationState? state)
    {
        if (state is null || state.Version != 1 || state.Active is null || state.Archived is null)
            throw new InvalidDataException("Unknown conversation format.");
        var records = state.Archived.Append(state.Active).ToArray();
        if (records.Any(r => r is null || r.Id == Guid.Empty || r.Summary is null || r.Summary.Length > 6000 || r.Turns is null)
            || records.Select(r => r.Id).Distinct().Count() != records.Length)
            throw new InvalidDataException("Invalid conversation metadata.");
        foreach (var turn in records.SelectMany(r => r.Turns))
        {
            if (turn is null || turn.Count < 2 || turn[0]?["role"]?.ToString() != "user"
                || turn.Last()?["role"]?.ToString() != "assistant")
                throw new InvalidDataException("Incomplete conversation turn.");
            foreach (var message in turn)
                if (message is not JsonObject obj || obj.ContainsKey("reasoning_content")
                    || obj["role"]?.ToString() is not ("user" or "assistant" or "tool"))
                    throw new InvalidDataException("Invalid conversation message.");
        }
    }
}
