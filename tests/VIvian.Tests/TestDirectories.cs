namespace VIvian.Tests;

internal static class TestDirectories
{
    /// <summary>On Windows, antivirus or indexing can hold a just-closed file briefly; retry instead of failing the test.</summary>
    public static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(100); }
            catch (IOException) { return; } // leftover temp files must not fail an otherwise passing test
        }
    }
}
