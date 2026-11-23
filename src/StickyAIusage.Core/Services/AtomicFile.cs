using System.IO;
using System.Text;

namespace StickyAIusage.Services;

/// <summary>
/// Replaces a file so that a reader sees either the old content or the new,
/// never a half-written file: the text goes to a temp file in the same folder
/// (so the rename stays on one volume), is flushed to disk, and is then
/// renamed over the target. Throws on failure, after removing the temp file;
/// callers on background paths catch and log.
/// </summary>
public static class AtomicFile
{
    private const int MoveAttempts = 5;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <param name="backupPath">When set and the target exists, the target is
    /// copied there first, so the previous version survives a bad write.</param>
    public static void WriteAllText(string path, string text, string? backupPath = null) =>
        WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text), backupPath);

    /// <summary>The same for binary content (a share card's PNG).</summary>
    public static void WriteAllBytes(string path, byte[] bytes, string? backupPath = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (backupPath is not null && File.Exists(path))
                File.Copy(path, backupPath, overwrite: true);

            // Another process (an editor, an antivirus scan, a reader) may
            // hold the target open without share-delete for a moment. Windows
            // then refuses the replace with "access denied", not an IOException.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
                {
                    Thread.Sleep(MoveRetryDelay);
                }
            }
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}
