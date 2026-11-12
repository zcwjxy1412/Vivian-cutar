using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Accounts;

/// <summary>Checks the gold selected radio marker beside the OCR-located retention option (reference frame 4).</summary>
public static class LoginRetentionDetector
{
    public static bool IsSelected(CapturedFrame frame, IReadOnlyList<RecognizedText> lines, ScreenRegion? panel = null)
    {
        var modal = panel is { } region ? lines.Where(l => region.Contains(l.CenterX, l.CenterY)).ToArray() : lines;
        return HasSelectedMarker(frame, LoginStateClassifier.FindExact(modal, "退出并保留登录记录"));
    }

    internal static bool HasSelectedMarker(CapturedFrame frame, RecognizedText? label, int minimumBlue = 60)
    {
        if (label is null || label.Height < 5) return false;
        var left = Math.Max(0, (int)(label.X - label.Height * 1.8));
        var right = Math.Min(frame.Width, (int)label.X);
        var top = Math.Max(0, (int)(label.CenterY - label.Height * .65));
        var bottom = Math.Min(frame.Height, (int)(label.CenterY + label.Height * .65));
        var gold = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var p = (y * frame.Width + x) * 4;
            var b = frame.Bgra[p]; var g = frame.Bgra[p + 1]; var r = frame.Bgra[p + 2];
            if (r >= 220 && g >= 155 && g <= 235 && b >= minimumBlue && b <= 175 && r - b >= 65) gold++;
        }
        return gold >= Math.Max(8, label.Height * label.Height * .15);
    }
}
