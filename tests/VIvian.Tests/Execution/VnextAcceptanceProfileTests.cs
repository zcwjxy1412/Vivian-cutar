using VIvian.Infrastructure;
using Xunit;

namespace VIvian.Tests.Execution;

public sealed class VnextAcceptanceProfileTests
{
    [Fact]
    public void OnlyExplicitUniqueChildOfAcceptanceDirectoryIsAllowed()
    {
        var valid = Path.Combine(VnextAcceptanceProfile.ParentDirectory, Guid.NewGuid().ToString("N"));
        Assert.Equal(valid, VnextAcceptanceProfile.ParseOpen([VnextAcceptanceProfile.OpenArgument, valid]));
        Assert.Equal(valid, VnextAcceptanceProfile.ParsePrepare([VnextAcceptanceProfile.PrepareArgument, valid]));
        Assert.Throws<ArgumentException>(() => VnextAcceptanceProfile.ParsePrepare([valid, VnextAcceptanceProfile.PrepareArgument]));
        Assert.Throws<ArgumentException>(() => VnextAcceptanceProfile.ParsePrepare([VnextAcceptanceProfile.PrepareArgument]));
        Assert.Throws<ArgumentException>(() => VnextAcceptanceProfile.ParsePrepare([VnextAcceptanceProfile.PrepareArgument, UnifiedProfilePreparation.InstalledDirectory]));
        Assert.Null(VnextAcceptanceProfile.ParseOpen([]));
        Assert.Throws<ArgumentException>(() => VnextAcceptanceProfile.ParseOpen([VnextAcceptanceProfile.OpenArgument]));
        Assert.Throws<ArgumentException>(() => VnextAcceptanceProfile.ParseOpen(["--smoke-test", VnextAcceptanceProfile.OpenArgument, valid]));
        foreach (var path in new[] { UnifiedProfilePreparation.InstalledDirectory, Path.GetTempPath(),
                     "relative", Path.Combine(VnextAcceptanceProfile.ParentDirectory, Guid.Empty.ToString("N")),
                     Path.Combine(VnextAcceptanceProfile.ParentDirectory, "..", Guid.NewGuid().ToString("N")),
                     Path.Combine(valid, "nested") })
            Assert.Throws<ArgumentException>(() => VnextAcceptanceProfile.ValidateDirectory(path));
    }
}
