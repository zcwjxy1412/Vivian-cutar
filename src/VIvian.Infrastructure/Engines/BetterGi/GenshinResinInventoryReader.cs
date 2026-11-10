using System.Globalization;
using System.Security.Cryptography;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal sealed class GenshinResinInventoryReader(CapturedFrame mapSettings, CapturedFrame originalIcon,
    CapturedFrame condensedIcon, CapturedFrame? currentCondensedIcon = null)
{
    internal static async Task<GenshinResinInventoryReader> LoadAsync(string betterGiRoot, CancellationToken ct)
    {
        // Pinned BetterGI assets referenced by QuickTeleport/Assets/Recognition.json and Common/ResinRecognition.cs.
        var settings = await LoadIconAsync("QuickTeleport", "MapSettingsButton.png",
            "DADFBE3595324A16A0A0F5479170C7BA58AC33B341164596D45DDB62A7AD74D3").ConfigureAwait(false);
        var original = await LoadIconAsync("AutoBoss", "original_resin_top_icon.png",
            "737D0A14F0FB8247038C8C468ACE618DD5D19345310ED4CB42463140909585AD").ConfigureAwait(false);
        var condensed = await LoadIconAsync("AutoFight", "condensed_resin_top_icon.png",
            "11CD388D7BD3ADCEFFAA549E2D12FE4B6F84305F3BE7C5DA17CD5DE54896E272").ConfigureAwait(false);
        var current = await LoadIconAsync("", "",
            "5491D913470330CFADFB4A9319CB08B41239C30310D8F4BC7BB8122E28987354",
            Path.Combine(AppContext.BaseDirectory, "Assets", "Genshin", "condensed-resin-map.png")).ConfigureAwait(false);
        return new(settings, original, condensed, current);

        async Task<CapturedFrame> LoadIconAsync(string task, string name, string sha256, string? suppliedPath = null)
        {
            ct.ThrowIfCancellationRequested();
            var path = suppliedPath ?? Path.Combine(betterGiRoot, "GameTask", task, "Assets", "1920x1080", name);
            if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
                throw new InvalidDataException("原神树脂识别素材路径包含链接。");
            await using var stream = ReadOnlyFileAccess.OpenForStableRead(path);
            if (!ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, path) || stream.Length > 128 * 1024
                || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) != sha256)
                throw new InvalidDataException("原神树脂识别素材与固定版本不符。");
            return await WindowsOcrReader.DecodeFileAsync(path, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("原神树脂识别素材无法解码。");
        }
    }

    internal Task<(int Original, int Condensed)?> ReadAsync(CapturedFrame frame, CancellationToken ct) =>
        ReadAsync(frame, WindowsOcrReader.ReadAsync, ct);

    internal bool IsMap(CapturedFrame frame, CancellationToken ct)
    {
        if (frame.Height is < 1080 or > 2160 || frame.Width * 9L != frame.Height * 16L ||
            frame.Bgra.LongLength != frame.Width * (long)frame.Height * 4) return false;
        var scale = frame.Height / 1080d;
        return GenshinLoginTemplates.Match(frame, mapSettings, new(25 * scale, 990 * scale, 58 * scale, 62 * scale), ct) is not null;
    }

    internal async Task<(int Original, int Condensed)?> ReadAsync(CapturedFrame frame,
        Func<CapturedFrame, CancellationToken, Task<OcrReadResult>> readOcr, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (frame.Height is < 1080 or > 2160 || frame.Width * 9L != frame.Height * 16L
            || frame.Bgra.LongLength != frame.Width * (long)frame.Height * 4)
            return null;

        var scale = frame.Height / 1080d;
        ScreenRegion Region(double x, double y, double width, double height) =>
            new(x * scale, y * scale, width * scale, height * scale);

        if (!IsMap(frame, ct) || GenshinLoginTemplates.Match(frame, originalIcon, Region(1200, 25, 580, 50), ct) is not { } original)
            return null;

        // The top bar can omit a zero counter. A failed icon match looks the same to this reader.
        var condensedRegion = new ScreenRegion(original.X - 180 * scale, original.Y - 15 * scale, 90 * scale, 50 * scale);
        var condensed = GenshinLoginTemplates.Match(frame, condensedIcon, condensedRegion, ct);
        var currentLayout = false;
        if (condensed is null && currentCondensedIcon is not null)
        {
            condensed = GenshinLoginTemplates.Match(frame, currentCondensedIcon, condensedRegion, ct);
            currentLayout = true;
        }
        if (condensed is null) return null;

        var originalText = await ReadCounterAsync(frame, new(original.Right + 20 * scale, 25 * scale, 125 * scale, 40 * scale),
            readOcr, ct).ConfigureAwait(false);
        var parts = originalText?.Split('/', StringSplitOptions.TrimEntries);
        if (parts is not [var current, "200"] || !TryReadNumber(current, 3, out var originalCount)) return null;

        var condensedText = await ReadCounterAsync(frame, currentLayout
            ? new(condensed.Right + 10 * scale, 25 * scale, 30 * scale, 40 * scale)
            : new(condensed.Right + 20 * scale, condensed.Y, 30 * scale, condensed.Height),
            readOcr, ct).ConfigureAwait(false);
        if (condensedText is null && currentLayout)
        {
            // Windows OCR can drop a lone zero; repeat the same pixels, then require three identical digits.
            var repeated = await ReadCounterAsync(frame, new(condensed.Right + 10 * scale, 25 * scale, 30 * scale, 40 * scale),
                readOcr, ct, repeat: true).ConfigureAwait(false);
            var digits = repeated?.Replace(" ", "", StringComparison.Ordinal);
            if (digits is { Length: 3 } && digits.All(ch => ch == digits[0]) && digits[0] is >= '0' and <= '5')
                condensedText = digits[..1];
        }
        return condensedText is not null && TryReadNumber(condensedText, 1, out var condensedCount) && condensedCount <= 5
            ? (originalCount, condensedCount)
            : null;
    }

    private static bool TryReadNumber(string text, int maxDigits, out int number)
    {
        number = 0;
        return text.Length > 0 && text.Length <= maxDigits && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static async Task<string?> ReadCounterAsync(CapturedFrame frame, ScreenRegion region,
        Func<CapturedFrame, CancellationToken, Task<OcrReadResult>> readOcr, CancellationToken ct, bool repeat = false)
    {
        if (region.X < 0 || region.Y < 0 || region.Right > frame.Width || region.Bottom > frame.Height) return null;
        var crop = frame.Crop((int)region.X, (int)region.Y, (int)region.Width, (int)region.Height);
        for (var i = 0; i < crop.Bgra.Length; i += 4)
        {
            var gray = (crop.Bgra[i] * 29 + crop.Bgra[i + 1] * 150 + crop.Bgra[i + 2] * 77) >> 8;
            // Preserve dimmed/anti-aliased digits during the map fade; a fixed white threshold erased them.
            crop.Bgra[i] = crop.Bgra[i + 1] = crop.Bgra[i + 2] = (byte)(255 - gray);
            crop.Bgra[i + 3] = 255;
        }
        if (repeat)
        {
            var width = crop.Width * 3;
            var bytes = new byte[width * crop.Height * 4];
            for (var y = 0; y < crop.Height; y++)
            for (var copy = 0; copy < 3; copy++)
                Buffer.BlockCopy(crop.Bgra, y * crop.Stride, bytes, (y * width + copy * crop.Width) * 4, crop.Stride);
            crop = new(bytes, width, crop.Height, "repeated resource digit");
        }
        var result = await readOcr(crop.Scale(Math.Max(2, (int)Math.Ceiling(72d / crop.Height))), ct).ConfigureAwait(false);
        return result is { Available: true, Lines: [var line] } ? line.Text.Trim() : null;
    }
}
