using VIvian.Infrastructure.Accounts;
using Xunit;

namespace VIvian.Tests.Accounts;

public sealed class March7thGamePathTests
{
    [Theory]
    [InlineData(" # 游戏安装路径。")]
    [InlineData("")]
    public void ReadsExistingGamePathWithoutTreatingYamlCommentAsFileName(string suffix)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vivian-game-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "StarRail.exe");
            File.WriteAllText(executable, "fixture, never executed");
            File.WriteAllText(Path.Combine(directory, "config.yaml"), $"game_path: {executable}{suffix}\r\n");
            Assert.Equal(executable, March7thGamePath.Read(directory));
        }
        finally { TestDirectories.DeleteWithRetry(directory); }
    }

    [Fact]
    public void RejectsDuplicatePathDeclarationsInsteadOfChoosingOne()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vivian-game-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "StarRail.exe");
            File.WriteAllText(executable, "fixture, never executed");
            File.WriteAllText(Path.Combine(directory, "config.yaml"), $"game_path: {executable}\ngame_path: {executable}\n");
            Assert.Empty(March7thGamePath.Read(directory));
        }
        finally { TestDirectories.DeleteWithRetry(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsQuotedWindowsPathWithComment(bool doubleQuoted)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vivian-game-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "StarRail.exe");
            File.WriteAllText(executable, "fixture, never executed");
            var quoted = doubleQuoted ? System.Text.Json.JsonSerializer.Serialize(executable) : $"'{executable}'";
            File.WriteAllText(Path.Combine(directory, "config.yaml"), $"game_path: {quoted} # 游戏安装路径\n");
            Assert.Equal(executable, March7thGamePath.Read(directory));
        }
        finally { TestDirectories.DeleteWithRetry(directory); }
    }
}
