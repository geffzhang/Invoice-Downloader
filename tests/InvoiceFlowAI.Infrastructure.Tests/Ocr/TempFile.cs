namespace InvoiceFlowAI.Infrastructure.Tests.Ocr;

internal static class TempFile
{
    // Antivirus scanners can transiently lock freshly written temp files for a
    // couple of seconds, which makes a single File.Delete fail with a sharing
    // violation. Retry through the lock; a genuine leak still fails the test.
    public static void Delete(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
    }

    // Directories that held a just-terminated child process can stay locked for
    // a moment longer (scanner or kernel teardown). Retry the recursive delete;
    // a genuine leak still fails the test.
    public static void DeleteDirectory(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
    }
}
