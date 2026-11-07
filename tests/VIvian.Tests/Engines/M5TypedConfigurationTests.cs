using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Engines;
using Xunit;

namespace VIvian.Tests.Engines;

/// <summary>Typed March7th parameters: enum, integer, nested names; structure-preserving writes; cross-field rules.</summary>
public sealed class M5TypedConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-m5t-" + Guid.NewGuid().ToString("N"));
    private readonly VersionedConfigurationControl _control = new();
    private string Config => Path.Combine(_root, "config.yaml");

    // Shape copied from v2026.10.31 assets/config/config.example.yaml (CRLF, comments, quoted number, capitalised bool).
    private const string Yaml =
        "power_enable: true # 清体力总开关\r\n" +
        "power_plan: []\r\n" +
        "instance_type: 拟造花萼（金） # 设置副本类型\r\n" +
        "instance_names:\r\n" +
        "  拟造花萼（金）: 回忆之蕾\r\n" +
        "  拟造花萼（赤）: 收容舱段\r\n" +
        "  凝滞虚影: 无\r\n" +
        "  侵蚀隧洞: 睿治之径\r\n" +
        "  饰品提取: 永恒笑剧\r\n" +
        "  历战余响: 毁灭的开端 # 配置各副本类型的名称。\r\n" +
        "instance_names_challenge_count:\r\n" +
        "  拟造花萼（金）: 24\r\n" +
        "instance_team_enable: false\r\n" +
        "instance_team_number: \"6\" # 自动切换的队伍编号。\r\n" +
        "use_fuel: False # 是否使用燃料。\r\n" +
        "echo_of_war_enable: false\r\n" +
        "echo_of_war_start_day_of_week: 1 # 周几开始\r\n" +
        "reward_enable: true\r\n" +
        "daily_enable: true\r\n" +
        "refresh_hour: 4\r\n" +
        "future_unknown: {a: 1} # keep\r\n";

    public M5TypedConfigurationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets", "config"));
        File.WriteAllText(Path.Combine(_root, "assets", "config", "version.txt"), "v2026.10.31\n");
        File.WriteAllText(Config, Yaml);
    }
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    private ConfigurationSnapshot Read() => _control.ReadConfiguration(GameId.StarRail, _root);
    private static string? Value(ConfigurationSnapshot s, string path) => s.Values.Single(v => v.Schema.Path == path).Value;

    [Fact]
    public void ReadsTypedValuesIncludingNestedNamesQuotedNumbersAndCapitalisedBooleans()
    {
        var s = Read();
        Assert.Equal("拟造花萼（金）", Value(s, "instance_type"));
        Assert.Equal("回忆之蕾", Value(s, "instance_names.拟造花萼（金）"));
        Assert.Equal("毁灭的开端", Value(s, "instance_names.历战余响"));
        Assert.Equal("6", Value(s, "instance_team_number"));
        Assert.Equal("false", Value(s, "use_fuel"));
        Assert.Equal("1", Value(s, "echo_of_war_start_day_of_week"));
        Assert.Null(Value(s, "power_plan")); // upstream-managed list stays read-only
        Assert.Null(Value(s, "tp_before_instance")); // missing in this file
    }

    [Fact]
    public void WritesOnlyTheValueSpansAndKeepsQuotesCommentsCrLfAndUnknownFields()
    {
        var s = Read();
        var proposal = _control.ProposeChanges(s, new Dictionary<string, string>
        {
            ["instance_type"] = "拟造花萼（赤）",
            ["instance_names.拟造花萼（赤）"] = "孽兽之形",
            ["instance_team_number"] = "4",
            ["echo_of_war_start_day_of_week"] = "3",
            ["use_fuel"] = "true",
        });
        var validation = _control.ValidateChanges(proposal, false);
        Assert.True(validation.Valid, validation.Reason);
        Assert.Contains("燃料", validation.Reason); // irreversible resource warning
        _control.ApplyChanges(proposal, Path.Combine(_root, "audit"), false);
        var expected = Yaml
            .Replace("instance_type: 拟造花萼（金） #", "instance_type: 拟造花萼（赤） #")
            .Replace("  拟造花萼（赤）: 收容舱段\r\n", "  拟造花萼（赤）: 孽兽之形\r\n")
            .Replace("instance_team_number: \"6\"", "instance_team_number: \"4\"")
            .Replace("echo_of_war_start_day_of_week: 1 #", "echo_of_war_start_day_of_week: 3 #")
            .Replace("use_fuel: False #", "use_fuel: true #");
        Assert.Equal(expected, File.ReadAllText(Config));
        Assert.Equal("回忆之蕾", Value(Read(), "instance_names.拟造花萼（金）")); // sibling untouched
    }

    [Theory]
    [InlineData("instance_type", "历战余响")]        // not an instance_type option
    [InlineData("instance_team_number", "9")]       // outside 3-7
    [InlineData("echo_of_war_start_day_of_week", "0")]
    [InlineData("refresh_hour", "24")]
    [InlineData("refresh_hour", "4.5")]
    [InlineData("instance_names.侵蚀隧洞", "name: injected")]
    [InlineData("instance_names.侵蚀隧洞", "a # comment")]
    [InlineData("instance_names.侵蚀隧洞", "")]
    public void OutOfRangeOrUnsafeValuesAreRejected(string path, string value)
    {
        var proposal = _control.ProposeChanges(Read(), new Dictionary<string, string> { [path] = value });
        Assert.False(_control.ValidateChanges(proposal, false).Valid);
    }

    [Fact]
    public void SelectingAnInstanceTypeWhoseNameIsNoneIsAConflict()
    {
        var proposal = _control.ProposeChanges(Read(), new Dictionary<string, string> { ["instance_type"] = "凝滞虚影" });
        var result = _control.ValidateChanges(proposal, false);
        Assert.False(result.Valid);
        Assert.Contains("凝滞虚影", result.Reason);
        var fixedProposal = _control.ProposeChanges(Read(), new Dictionary<string, string>
            { ["instance_type"] = "凝滞虚影", ["instance_names.凝滞虚影"] = "空海之形" });
        Assert.True(_control.ValidateChanges(fixedProposal, false).Valid);
    }

    [Fact]
    public void PowerOffWarnsThatEchoOfWarAndDailyPointsAreAffected()
    {
        var proposal = _control.ProposeChanges(Read(), new Dictionary<string, string>
            { ["power_enable"] = "false", ["echo_of_war_enable"] = "true" });
        var result = _control.ValidateChanges(proposal, false);
        Assert.True(result.Valid);
        Assert.Contains("历战余响", result.Reason);
        Assert.Contains("500", result.Reason);
    }

    [Fact]
    public void DuplicateOrFlowStyleFieldsStayReadOnly()
    {
        File.WriteAllText(Config, Yaml + "instance_type: 凝滞虚影\r\nrefresh_hour_x: 1\r\n");
        var s = Read();
        Assert.Null(Value(s, "instance_type"));
        Assert.Throws<InvalidOperationException>(() => _control.ProposeChanges(s,
            new Dictionary<string, string> { ["instance_type"] = "侵蚀隧洞" }));
        Assert.Null(YamlScalarEditor.Locate(YamlScalarEditor.Lines("future_unknown: {a: 1}"), "future_unknown"));
        Assert.Null(YamlScalarEditor.Locate(YamlScalarEditor.Lines("a: \"x\\\"y\""), "a"));
    }

    [Fact]
    public void EveryWritableParameterHasChineseTitleTypeGroupAndSourceLine()
    {
        foreach (var p in EngineCatalog.March7th.Parameters)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.Matches(@"config\.example\.yaml:\d+$", p.Source);
            Assert.False(string.IsNullOrWhiteSpace(p.Group));
            if (p.Access == ParameterAccess.VerifiedReadWrite)
            {
                Assert.True(ParameterRules.IsScalar(p), p.Path);
                Assert.True(ParameterRules.IsValid(p, ParameterRules.Normalize(p, p.DefaultValue)), p.Path + " default");
            }
        }
    }
}

/// <summary>Instance names are chosen from the installed release's own instance_names.json.</summary>
public sealed class M5InstanceChoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-m5i-" + Guid.NewGuid().ToString("N"));
    private readonly VersionedConfigurationControl _control = new();

    public M5InstanceChoiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets", "config"));
        File.WriteAllText(Path.Combine(_root, "assets", "config", "version.txt"), "v2026.10.31\n");
        // Shape of v2026.10.31 assets/config/instance_names.json (type → name → purpose).
        File.WriteAllText(Path.Combine(_root, "assets", "config", "instance_names.json"),
            "{\"拟造花萼（金）\": {\"无\": \"跳过\", \"回忆之蕾\": \"角色经验\", \"以太之蕾\": \"武器经验\"}," +
            " \"凝滞虚影\": {\"无\": \"跳过\", \"空海之形\": \"\"}}");
        File.WriteAllText(Path.Combine(_root, "config.yaml"),
            "instance_type: 拟造花萼（金）\r\ninstance_names:\r\n  拟造花萼（金）: 回忆之蕾\r\n  凝滞虚影: 无\r\n  侵蚀隧洞: 睿治之径\r\n");
    }
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    [Fact]
    public void InstanceNamesBecomeChoicesWithPurposeLabels()
    {
        var s = _control.ReadConfiguration(GameId.StarRail, _root);
        var golden = s.Values.Single(v => v.Schema.Path == "instance_names.拟造花萼（金）");
        Assert.Equal("enum", golden.Schema.DataType);
        Assert.Equal(["无", "回忆之蕾", "以太之蕾"], ParameterRules.Options(golden.Schema));
        Assert.Contains(new ParameterOption("以太之蕾", "以太之蕾（武器经验）"), ParameterRules.LabelledOptions(golden.Schema));
        Assert.Contains(new ParameterOption("空海之形", "空海之形"),
            ParameterRules.LabelledOptions(s.Values.Single(v => v.Schema.Path == "instance_names.凝滞虚影").Schema));
        // Types missing from the file keep the conservative free-text rule.
        Assert.Equal("text", s.Values.Single(v => v.Schema.Path == "instance_names.侵蚀隧洞").Schema.DataType);
    }

    [Fact]
    public void ANameOutsideTheInstalledListIsRejectedAndAListedOneIsWritten()
    {
        var s = _control.ReadConfiguration(GameId.StarRail, _root);
        Assert.False(_control.ValidateChanges(_control.ProposeChanges(s,
            new Dictionary<string, string> { ["instance_names.拟造花萼（金）"] = "不存在之蕾" }), false).Valid);
        var ok = _control.ProposeChanges(s, new Dictionary<string, string> { ["instance_names.拟造花萼（金）"] = "以太之蕾" });
        Assert.True(_control.ValidateChanges(ok, false).Valid);
        _control.ApplyChanges(ok, Path.Combine(_root, "audit"), false);
        Assert.Contains("  拟造花萼（金）: 以太之蕾\r\n", File.ReadAllText(Path.Combine(_root, "config.yaml")));
    }
}
