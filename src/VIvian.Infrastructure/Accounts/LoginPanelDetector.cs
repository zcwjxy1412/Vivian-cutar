namespace VIvian.Infrastructure.Accounts;

public sealed record LoginPanelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>
/// Finds the bright miHoYo login card. Used as an independent signal (besides OCR) that the login dialog is
/// present, and to locate its dropdown arrow without hard-coded screen coordinates.
/// </summary>
public static class LoginPanelDetector
{
    public static LoginPanelRect? Detect(CapturedFrame frame)
    {
        var left = (int)(frame.Width * 0.12);
        var right = (int)(frame.Width * 0.88);
        var top = (int)(frame.Height * 0.15);
        var bottom = (int)(frame.Height * 0.88);
        if (right - left < 200 || bottom - top < 120) return null;

        var rows = new int[frame.Height];
        for (var y = top; y < bottom; y++)
        {
            var count = 0;
            var offset = (y * frame.Width + left) * 4;
            for (var x = left; x < right; x++, offset += 4)
            {
                if (IsPanelPixel(frame.Bgra, offset)) count++;
            }
            rows[y] = count;
        }

        // The card contains separators and a yellow button, so rows are not contiguously bright. Collect the
        // widest bright band while tolerating small gaps (separator lines, text rows).
        var rowThreshold = Math.Max(40, (right - left) / 40);
        var bestStart = -1;
        var bestEnd = -1;
        var bandFirst = -1;
        var bandLast = -1;
        var gap = 0;
        for (var y = top; y < bottom; y++)
        {
            if (rows[y] >= rowThreshold)
            {
                if (bandFirst < 0) bandFirst = y;
                bandLast = y;
                gap = 0;
            }
            else if (bandFirst >= 0 && ++gap > 24)
            {
                if (bandLast - bandFirst > bestEnd - bestStart) { bestStart = bandFirst; bestEnd = bandLast; }
                bandFirst = -1;
                gap = 0;
            }
        }
        if (bandFirst >= 0 && bandLast - bandFirst > bestEnd - bestStart) { bestStart = bandFirst; bestEnd = bandLast; }
        if (bestStart < 0 || bestEnd - bestStart + 1 < 80) return null;
        var bestLength = bestEnd - bestStart + 1;

        var columns = new int[frame.Width];
        for (var y = bestStart; y < bestStart + bestLength; y++)
        {
            var offset = (y * frame.Width + left) * 4;
            for (var x = left; x < right; x++, offset += 4)
            {
                if (IsPanelPixel(frame.Bgra, offset)) columns[x]++;
            }
        }
        var columnThreshold = Math.Max(20, bestLength / 4);
        var first = -1;
        var last = -1;
        for (var x = left; x < right; x++)
        {
            if (columns[x] < columnThreshold) continue;
            if (first < 0) first = x;
            last = x;
        }
        if (first < 0 || last - first < 200) return null;
        return new LoginPanelRect(first, bestStart, last - first + 1, bestLength);
    }

    public static bool IsVisible(CapturedFrame frame) => Detect(frame) is { Width: >= 240, Height: >= 140 };

    private static bool IsPanelPixel(byte[] bgra, int offset)
    {
        var b = bgra[offset];
        var g = bgra[offset + 1];
        var r = bgra[offset + 2];
        var min = Math.Min(b, Math.Min(g, r));
        var max = Math.Max(b, Math.Max(g, r));
        return min >= 224 && max - min <= 14;
    }
}
