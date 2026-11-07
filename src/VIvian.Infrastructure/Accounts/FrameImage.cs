namespace VIvian.Infrastructure.Accounts;

/// <summary>Small in-memory image operations used to enlarge the login card before OCR. No files are written.</summary>
public static class FrameImage
{
    public static CapturedFrame Crop(this CapturedFrame frame, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Math.Max(0, frame.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, frame.Height - 1));
        width = Math.Clamp(width, 1, frame.Width - x);
        height = Math.Clamp(height, 1, frame.Height - y);
        var pixels = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(frame.Bgra, ((y + row) * frame.Width + x) * 4, pixels, row * width * 4, width * 4);
        }
        return new CapturedFrame(pixels, width, height, frame.Source);
    }

    /// <summary>Bilinear upscale. OCR is markedly more reliable on a 3x login card than on the 24 px row.</summary>
    public static CapturedFrame Scale(this CapturedFrame frame, int factor)
    {
        if (factor <= 1) return frame;
        var width = frame.Width * factor;
        var height = frame.Height * factor;
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = (y + 0.5) / factor - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, frame.Height - 1);
            var y1 = Math.Min(y0 + 1, frame.Height - 1);
            var ty = Math.Clamp(sourceY - Math.Floor(sourceY), 0, 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX = (x + 0.5) / factor - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, frame.Width - 1);
                var x1 = Math.Min(x0 + 1, frame.Width - 1);
                var tx = Math.Clamp(sourceX - Math.Floor(sourceX), 0, 1);
                var target = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var topLeft = frame.Bgra[(y0 * frame.Width + x0) * 4 + channel];
                    var topRight = frame.Bgra[(y0 * frame.Width + x1) * 4 + channel];
                    var bottomLeft = frame.Bgra[(y1 * frame.Width + x0) * 4 + channel];
                    var bottomRight = frame.Bgra[(y1 * frame.Width + x1) * 4 + channel];
                    var top = topLeft + (topRight - topLeft) * tx;
                    var bottom = bottomLeft + (bottomRight - bottomLeft) * tx;
                    pixels[target + channel] = (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * ty), 0, 255);
                }
            }
        }
        return new CapturedFrame(pixels, width, height, frame.Source);
    }
}
