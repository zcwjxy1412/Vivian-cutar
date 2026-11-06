namespace VIvian.Core.Engines;

public sealed record ConfigurationValue(EngineParameterDefinition Schema, string? Value, string State);
public sealed record ConfigurationSnapshot(GameId Game, string PinnedVersion, string Path, string Revision,
    IReadOnlyList<ConfigurationValue> Values, DateTimeOffset ReadAt);
public sealed record ConfigurationChange(string Path, string OldValue, string NewValue);
public sealed record ConfigurationProposal(ConfigurationSnapshot Base, IReadOnlyList<ConfigurationChange> Changes);
public sealed record ConfigurationValidation(bool Valid, string Reason);
public sealed record ConfigurationApplyResult(ConfigurationSnapshot Snapshot, string BackupPath, string AuditPath);

/// <summary>Shared, typed configuration boundary for the WPF client and future controlled tools.</summary>
public interface IConfigurationControl
{
    IReadOnlyList<EngineTaskDefinition> ListTasks(GameId game);
    EngineTaskDefinition GetTaskSchema(GameId game, string taskId);
    ConfigurationSnapshot ReadConfiguration(GameId game, string installDirectory);
    ConfigurationProposal ProposeChanges(ConfigurationSnapshot snapshot, IReadOnlyDictionary<string, string> values);
    ConfigurationValidation ValidateChanges(ConfigurationProposal proposal, bool engineRunning);
    ConfigurationApplyResult ApplyChanges(ConfigurationProposal proposal, string auditDirectory, bool engineRunning);
}
