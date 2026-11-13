using VIvian.App;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Assistant;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class AssistantExclusiveActionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("furina-confirm-").FullName;
    private readonly AccountConfigurationTarget _target = new(GameId.StarRail, Guid.NewGuid());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecliningConfirmationDoesNotReleaseInputOrSubmit(bool run)
    {
        var events = 0;
        var submitted = false;
        var vm = Create(false, _ => { submitted = true; return Task.FromResult("accepted"); });
        vm.ExclusiveActionStarting += () => events++;
        var card = Card();
        if (run) await vm.ConfirmRunAsync(card, Plan());
        else await vm.ConfirmAsync(card);
        Assert.Equal(0, events);
        Assert.False(submitted);
        Assert.True(card.CanConfirm);
    }

    [Fact]
    public async Task RunSubmissionWaitsForSynchronousInputRelease()
    {
        var inputEnabled = true;
        var submitted = 0;
        var vm = Create(true, _ =>
        {
            Assert.False(inputEnabled);
            submitted++;
            return Task.FromResult("accepted");
        });
        vm.ExclusiveActionStarting += () => inputEnabled = false;
        var card = Card();
        await vm.ConfirmRunAsync(card, Plan());
        await vm.ConfirmRunAsync(card, Plan());
        Assert.Equal(1, submitted);
        Assert.Equal("accepted", card.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidationDuringConfirmationDoesNotReleaseInput(bool run)
    {
        var events = 0;
        var submitted = false;
        AssistantViewModel? vm = null;
        vm = Create(true, _ => { submitted = true; return Task.FromResult("accepted"); }, () => vm!.InvalidateProposals());
        vm.ExclusiveActionStarting += () => { events++; throw new ReleaseFailure(); };
        var card = Card();
        if (run) await vm.ConfirmRunAsync(card, Plan());
        else await vm.ConfirmAsync(card);
        Assert.Equal(0, events);
        Assert.False(submitted);
        Assert.False(card.CanConfirm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInputReleaseStopsBeforeWriteLockOrSubmission(bool run)
    {
        var submitted = false;
        var vm = Create(true, _ => { submitted = true; return Task.FromResult("accepted"); });
        var observed = false;
        vm.ExclusiveActionStarting += () => { observed = true; throw new ReleaseFailure(); };
        var card = Card();
        await Assert.ThrowsAsync<ReleaseFailure>(() => run ? vm.ConfirmRunAsync(card, Plan()) : vm.ConfirmAsync(card));
        Assert.True(observed);
        Assert.False(submitted);
        Assert.False(card.CanConfirm);
    }

    private AssistantViewModel Create(bool confirmed, Func<AssistantRunPlan, Task<string>> submit, Action? onConfirm = null) => new(
        new ThemeAssets(_root, _root), new AssistantSettingsStore(_root),
        new AssistantTools(() => [], _ => Task.FromResult<IReadOnlyList<RunRecord>>([])) { ScheduleProposalsEnabled = true },
        _ => true, _root, () => Task.CompletedTask, submit, (_, _, _) => { onConfirm?.Invoke(); return confirmed; });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseFailureIsReportedByTheExistingCardCommand(bool run)
    {
        var submitted = false;
        var vm = Create(true, _ => { submitted = true; return Task.FromResult("accepted"); });
        vm.ExclusiveActionStarting += () => throw new ReleaseFailure();
        var card = new ProposalCard(Guid.NewGuid(), _target, "fixture", "change", "title", "confirm",
            c => run ? vm.ConfirmRunAsync(c, Plan()) : vm.ConfirmAsync(c));
        var initialState = card.State;
        card.ConfirmCommand.Execute(null);
        Assert.NotEqual(initialState, card.State);
        Assert.False(submitted);
        Assert.False(card.CanConfirm);
    }

    private ProposalCard Card() => new(Guid.NewGuid(), _target, "fixture", "change", "title", "confirm", _ => Task.CompletedTask);
    private AssistantRunPlan Plan() => new(Guid.NewGuid(), _target, "fixture", "revision", 1, "saved", DateTimeOffset.UtcNow.AddMinutes(1));
    private sealed class ReleaseFailure : Exception;
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
