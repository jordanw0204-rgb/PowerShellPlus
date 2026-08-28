using System.Collections.Concurrent;
using System.Text;

namespace PowerShellPlus.Native;

/// <summary>
/// Serializes writers per destination and replaces files through a unique
/// temporary file. Windows can briefly deny a replace while antivirus,
/// indexing, or another startup path has the destination open, so transient
/// sharing failures are retried without exposing a half-written file.
/// </summary>
internal static class AtomicFileStore
{
    private const int MaximumReplaceAttempts = 6;
    private static readonly ConcurrentDictionary<string, object> PathGates =
        new(StringComparer.OrdinalIgnoreCase);

    public static void WriteAllText(string path, string contents, Encoding encoding,
        string? backupPath = null, bool durable = false, bool skipIfUnchanged = false)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = PathGates.GetOrAdd(fullPath, static _ => new object());
        lock (gate)
        {
            if (skipIfUnchanged && File.Exists(fullPath))
            {
                try
                {
                    if (File.ReadAllText(fullPath, encoding) == contents) return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (durable)
                {
                    using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 64 * 1024, FileOptions.WriteThrough);
                    using var writer = new StreamWriter(stream, encoding, 64 * 1024, leaveOpen: true);
                    writer.Write(contents);
                    writer.Flush();
                    stream.Flush(true);
                }
                else File.WriteAllText(temporary, contents, encoding);

                ReplaceWithRetry(temporary, fullPath, backupPath);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }

    private static void ReplaceWithRetry(string temporary, string destination, string? backupPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (backupPath is not null && File.Exists(destination))
                    File.Replace(temporary, destination, backupPath, ignoreMetadataErrors: true);
                else File.Move(temporary, destination, true);
                return;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)
                                              && attempt < MaximumReplaceAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(20 * (1 << (attempt - 1))));
            }
        }
    }

    internal static bool ContentionContractPassesForTest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PowerShellPlus-atomic-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.txt");
        try
        {
            Directory.CreateDirectory(directory);
            var values = Enumerable.Range(0, 32).Select(index => $"payload-{index:D2}").ToArray();
            Task.WaitAll(values.Select(value => Task.Run(() =>
                WriteAllText(path, value, new UTF8Encoding(false)))).ToArray());
            var saved = File.ReadAllText(path);
            return values.Contains(saved, StringComparer.Ordinal)
                && !Directory.EnumerateFiles(directory, "*.tmp").Any();
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
