using System.Text.Json;

namespace VIvian.Infrastructure.Accounts;

/// <summary>
/// The elevated input host accepts a deliberately small command set. It never accepts a path, a command line
/// or an arbitrary process name, so a compromised UI process cannot turn it into a general-purpose launcher.
/// </summary>
public sealed record InputRequest(string Op, long Hwnd = 0, int X = 0, int Y = 0, int ProcessId = 0, string? Path = null);

public sealed record InputResponse(bool Ok, string? Error = null, string? Detail = null)
{
    public static InputResponse Failure(string error) => new(false, error);
    public static InputResponse Success(string? detail = null) => new(true, null, detail);
}

public static class InputHostProtocol
{
    public const string PipePrefix = "vivian-input-";
    public const string Argument = "--input-host";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
