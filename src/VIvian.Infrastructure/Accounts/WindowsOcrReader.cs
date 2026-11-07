using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Accounts;

public sealed record OcrReadResult(bool Available, string? Error, IReadOnlyList<RecognizedText> Lines);

/// <summary>
/// Local OCR through the Windows.Media.Ocr engine. No screenshot or text ever leaves the machine.
/// </summary>
public static class WindowsOcrReader
{
    private static readonly string[] PreferredLanguages = ["zh-Hans-CN", "zh-CN", "en-US"];

    public static Task<OcrReadResult> ReadAsync(CapturedFrame frame, CancellationToken cancellationToken) =>
        ReadBitmapAsync(frame.Bgra, frame.Width, frame.Height, cancellationToken);

    public static async Task<OcrReadResult> ReadBitmapAsync(byte[] bgra, int width, int height,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = CreateEngine(out var error);
        if (engine is null) return new OcrReadResult(false, error, Array.Empty<RecognizedText>());
        if (width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension)
            return new OcrReadResult(false, $"图像尺寸 {width}x{height} 超过本地 OCR 上限。", Array.Empty<RecognizedText>());

        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(bgra);
            var buffer = writer.DetachBuffer();
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height,
                BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
            var lines = result.Lines.Select(line =>
            {
                var words = line.Words.ToArray();
                if (words.Length == 0) return null;
                var left = words.Min(w => w.BoundingRect.X);
                var top = words.Min(w => w.BoundingRect.Y);
                var right = words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                var bottom = words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                return new RecognizedText(line.Text, left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
            }).Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Text)).Cast<RecognizedText>()
                .OrderBy(l => l.Y).ThenBy(l => l.X).ToArray();
            return new OcrReadResult(true, null, lines);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new OcrReadResult(false, $"本地 OCR 失败：{ex.Message}", Array.Empty<RecognizedText>());
        }
    }

    public static async Task<OcrReadResult> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var frame = await DecodeFileAsync(path, cancellationToken).ConfigureAwait(false);
        return frame is null
            ? new OcrReadResult(false, $"无法读取截图文件：{path}", Array.Empty<RecognizedText>())
            : await ReadAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CapturedFrame?> DecodeFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellationToken);
            using var stream = await file.OpenReadAsync().AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
            var length = checked(bitmap.PixelWidth * bitmap.PixelHeight * 4);
            var buffer = new Windows.Storage.Streams.Buffer((uint)length);
            bitmap.CopyToBuffer(buffer);
            using var reader = DataReader.FromBuffer(buffer);
            var bytes = new byte[buffer.Length];
            reader.ReadBytes(bytes);
            return new CapturedFrame(bytes, bitmap.PixelWidth, bitmap.PixelHeight, path);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    public static async Task SavePngAsync(CapturedFrame frame, string path, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(full)!).AsTask(cancellationToken);
        var file = await folder.CreateFileAsync(Path.GetFileName(full), CreationCollisionOption.ReplaceExisting)
            .AsTask(cancellationToken);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite).AsTask(cancellationToken);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)frame.Width,
            (uint)frame.Height, 96, 96, frame.Bgra);
        await encoder.FlushAsync().AsTask(cancellationToken);
    }

    private static OcrEngine? CreateEngine(out string? error)
    {
        error = null;
        foreach (var tag in PreferredLanguages)
        {
            try
            {
                var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag));
                if (engine is not null) return engine;
            }
            catch (ArgumentException) { }
        }
        var profile = OcrEngine.TryCreateFromUserProfileLanguages();
        if (profile is not null) return profile;
        error = "本机没有可用的 Windows OCR 语言包（需要 zh-Hans-CN 或 en-US）。";
        return null;
    }
}
