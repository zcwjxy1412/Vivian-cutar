using System.Globalization;
using System.Text;

namespace VIvian.Infrastructure.Engines.OneDragon;

public enum OneDragonTaskPlanStatus { Available, InstanceUnavailable, Missing, Empty, Invalid, UnsupportedTask, Unavailable, LinkRejected }

public sealed record OneDragonTaskDefinition(string Id, string DisplayName, bool UsesStaminaPlan);

public sealed record OneDragonTaskPlan(int InstanceIndex, OneDragonTaskPlanStatus Status, IReadOnlyList<OneDragonTaskDefinition> Tasks);

/// <summary>Reads the ordered, saved default group of one verified script profile. Does not migrate old groups.</summary>
public static class OneDragonTaskPlans
{
    public static IReadOnlyList<OneDragonTaskDefinition> Catalog { get; } = Array.AsReadOnly<OneDragonTaskDefinition>(
    [
        new("engagement_reward", "活跃度奖励", false),
        new("email", "邮件", false),
        new("coffee", "咖啡店", false),
        new("charge_plan", "体力刷本", true),
    ]);

    public static async Task<OneDragonTaskPlan> ReadAsync(string installRoot, int instanceIndex, CancellationToken ct = default)
    {
        var instances = await OneDragonInstanceCatalog.ReadAsync(installRoot, ct).ConfigureAwait(false);
        if (!instances.ContainsExactlyOne([instanceIndex]))
            return new(instanceIndex, OneDragonTaskPlanStatus.InstanceUnavailable, []);
        try
        {
            var file = Path.Combine(instanceIndex.ToString("D2", CultureInfo.InvariantCulture), "one_dragon", "_group.yml");
            var text = await OneDragonConfigRead.ReadAsync(installRoot, file, ct).ConfigureAwait(false);
            if (text is null) return new(instanceIndex, OneDragonTaskPlanStatus.Missing, []);
            return Parse(instanceIndex, text);
        }
        catch (OneDragonConfigLinkException) { return new(instanceIndex, OneDragonTaskPlanStatus.LinkRejected, []); }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        { return new(instanceIndex, OneDragonTaskPlanStatus.Invalid, []); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        { return new(instanceIndex, OneDragonTaskPlanStatus.Unavailable, []); }
    }

    private static OneDragonTaskPlan Parse(int index, string yaml)
    {
        var parsed = OneDragonConfigRead.ReadSequence(yaml, "app_list");
        if (parsed is null || !parsed.HasSequence || parsed.Scalars.Count > 0)
            return new(index, OneDragonTaskPlanStatus.Invalid, []);

        var tasks = new List<OneDragonTaskDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in parsed.Rows)
        {
            if (row.Count != 2 || !row.TryGetValue("app_id", out var app)
                || app.Value.Length > 64 || !app.Value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                || !row.TryGetValue("enabled", out var enabled)
                || OneDragonConfigRead.ReadBool(enabled) is not { } isEnabled || !seen.Add(app.Value))
                return new(index, OneDragonTaskPlanStatus.Invalid, []);
            if (!isEnabled) continue;

            var definition = Catalog.FirstOrDefault(task => task.Id == app.Value);
            if (definition is null) return new(index, OneDragonTaskPlanStatus.UnsupportedTask, []);
            tasks.Add(definition);
        }
        return tasks.Count == 0
            ? new(index, OneDragonTaskPlanStatus.Empty, [])
            : new(index, OneDragonTaskPlanStatus.Available, tasks.AsReadOnly());
    }
}
