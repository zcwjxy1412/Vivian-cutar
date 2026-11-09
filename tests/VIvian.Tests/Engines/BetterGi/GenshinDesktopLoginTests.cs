using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class GenshinDesktopLoginTests
{
    private static readonly StarRailWindowInfo Window = new((IntPtr)123, 45, "", 1920, 1080, 0, 0, DateTimeOffset.UtcNow);
    private static readonly LoginTarget A = new("130****01", "130******01", 0, null);
    private static readonly LoginTarget B = new("130****02", "130******02", 1, "234567891", A.MaskedPhone, "234567890");
    private static readonly SwitchPolicy Policy = new(StepTimeout: TimeSpan.FromSeconds(1));

    [Fact]
    public async Task AToBRequiresLogoutThenFreshTargetCardAndContinuousWorld()
    {
        var desktop = new Desktop(
            Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Gate),
            Frame(GenshinView.World), Frame(GenshinView.World),
            Frame(GenshinView.World), Frame(GenshinView.Menu), Frame(GenshinView.LeaveWorld),
            Frame(GenshinView.Gate), Frame(GenshinView.ConfirmLogout), Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Dropdown, A.NormalizedPhone, B.NormalizedPhone),
            Frame(GenshinView.Card, B.NormalizedPhone), Frame(GenshinView.Gate),
            Frame(GenshinView.World, uid: B.ObservedUid), Frame(GenshinView.World, uid: B.ObservedUid));
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero);

        Assert.True((await driver.EnsureLoginAsync(Policy, default)).Ok);
        Assert.Equal("verified", (await driver.SwitchAsync(A, Policy, default)).Evidence?.Verdict);
        Assert.True((await driver.RestartToLoginAsync(Policy, default)).Ok);
        var second = await driver.SwitchAsync(B, Policy, default);
        Assert.True(second.Ok, second.Detail);
        Assert.Equal(B.ObservedUid, second.Uid);
        Assert.Equal([GenshinInput.EnterAccount, GenshinInput.OpenGate, GenshinInput.OpenMenu,
            GenshinInput.LeaveMenu, GenshinInput.ConfirmLeave, GenshinInput.LogoutGate, GenshinInput.ConfirmLogout,
            GenshinInput.OpenDropdown, GenshinInput.ChooseAccount, GenshinInput.EnterAccount, GenshinInput.OpenGate],
            desktop.Actions);
    }

    [Fact]
    public async Task DuplicateDropdownRowsNeverGetClicked()
    {
        var desktop = new Desktop(Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Dropdown, B.NormalizedPhone, B.NormalizedPhone));
        var result = await new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero).SwitchAsync(B, Policy, default);
        Assert.False(result.Ok);
        Assert.True(result.Fatal);
        Assert.Equal([GenshinInput.OpenDropdown], desktop.Actions);
    }

    [Fact]
    public async Task WorldOnReplacedProcessCannotProveIdentity()
    {
        var replacement = Frame(GenshinView.World) with { Window = Window with { ProcessId = 123 } };
        var desktop = new Desktop(Frame(GenshinView.Card, A.NormalizedPhone), Frame(GenshinView.Gate), replacement);
        var result = await new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero).SwitchAsync(A, Policy, default);
        Assert.False(result.Ok);
        Assert.True(result.Fatal);
    }

    [Fact]
    public async Task PreviousWorldUidCannotPassForAnotherAccount()
    {
        var desktop = new Desktop(Frame(GenshinView.Card, B.NormalizedPhone), Frame(GenshinView.Gate),
            Frame(GenshinView.World, uid: B.PreviousUid), Frame(GenshinView.World, uid: B.PreviousUid));
        var result = await new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero).SwitchAsync(B, Policy, default);
        Assert.False(result.Ok);
        Assert.Equal("mismatch", result.Evidence?.Verdict);
    }

    [Fact]
    public async Task VerificationPromptStopsWithoutInput()
    {
        var desktop = new Desktop(Frame(GenshinView.NeedsUser));
        var result = await new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero).EnsureLoginAsync(Policy, default);
        Assert.True(result.NeedsUser);
        Assert.Empty(desktop.Actions);
    }

    [Fact]
    public void TemplateMatcherRequiresUniqueVisualEvidence()
    {
        var icon = new CapturedFrame(new byte[18 * 12 * 4], 18, 12, "test-icon");
        var random = new Random(490);
        random.NextBytes(icon.Bgra);
        for (var i = 3; i < icon.Bgra.Length; i += 4) icon.Bgra[i] = 255;
        var frame = new CapturedFrame(new byte[80 * 1080 * 4], 80, 1080, "test-screen");
        var region = new ScreenRegion(0, 0, 80, 30);
        Assert.Null(GenshinLoginTemplates.Match(frame, icon, region, default));
        Paste(5);
        Assert.Equal(new ScreenRegion(5, 8, 18, 12), GenshinLoginTemplates.Match(frame, icon, region, default));
        Paste(40);
        Assert.Null(GenshinLoginTemplates.Match(frame, icon, region, default));

        void Paste(int x)
        {
            for (var row = 0; row < icon.Height; row++)
                Buffer.BlockCopy(icon.Bgra, row * icon.Stride, frame.Bgra, (row + 8) * frame.Stride + x * 4, icon.Stride);
        }
    }

    private static GenshinDesktopFrame Frame(GenshinView view, string? mask = null, string? second = null, string? uid = null)
    {
        var region = new ScreenRegion(1, 1, 4, 4);
        var rows = new[] { mask, second }.OfType<string>().Select((phone, i) =>
            new LoginAccountObservation(phone, phone, i, 3, i * 20, 20, 15, .9, "")).ToArray();
        return new(Window, view, rows, region, region, uid);
    }

    private sealed class Desktop(params GenshinDesktopFrame[] frames) : IGenshinLoginDesktop
    {
        private readonly Queue<GenshinDesktopFrame> _frames = new(frames);
        public List<GenshinInput> Actions { get; } = [];
        public Task<GenshinDesktopFrame> ReadAsync(CancellationToken ct) => Task.FromResult(_frames.Dequeue());
        public bool TryInput(GenshinDesktopFrame frame, GenshinInput input, ScreenRegion? point)
        {
            Actions.Add(input);
            return true;
        }
    }
}
