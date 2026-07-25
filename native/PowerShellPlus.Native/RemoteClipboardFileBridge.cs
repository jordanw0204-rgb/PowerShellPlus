using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PowerShellPlus.Native;

internal readonly record struct RemoteFileUploadResult(bool Succeeded, string? RemotePath, string? Error);

internal static class RemoteClipboardFileBridge
{
    internal const long MaximumFileBytes = 100L * 1024 * 1024;
    private const string ResultPrefix = "PSP_REMOTE_FILE:";
    private static readonly Regex ExtensionPattern = new(@"^\.[A-Za-z0-9]{1,10}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RemotePathPattern = new(@"^/[A-Za-z0-9._~/-]{1,4096}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<RemoteFileUploadResult> UploadFileAsync(string localPath, string[] connectionArguments,
        TimeSpan? timeout = null, string sshExecutable = "ssh.exe")
    {
        try
        {
            var file = new FileInfo(localPath);
            if (!file.Exists) return new(false, null, "The attached local file no longer exists.");
            if (file.Length is <= 0 or > MaximumFileBytes) return new(false, null, "Attached files must be between 1 byte and 100 MB.");
            await using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            return await UploadStreamAsync(stream, file.Length, Path.GetExtension(file.Name), connectionArguments, timeout, sshExecutable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, null, exception.Message);
        }
    }

    public static async Task<RemoteFileUploadResult> UploadBytesAsync(byte[] contents, string extension, string[] connectionArguments,
        TimeSpan? timeout = null, string sshExecutable = "ssh.exe")
    {
        if (contents.LongLength is <= 0 or > MaximumFileBytes) return new(false, null, "Attached files must be between 1 byte and 100 MB.");
        await using var stream = new MemoryStream(contents, writable: false);
        return await UploadStreamAsync(stream, contents.LongLength, extension, connectionArguments, timeout, sshExecutable);
    }

    private static async Task<RemoteFileUploadResult> UploadStreamAsync(Stream contents, long length, string extension,
        string[] connectionArguments, TimeSpan? timeout, string sshExecutable)
    {
        if (length is <= 0 or > MaximumFileBytes) return new(false, null, "Attached files must be between 1 byte and 100 MB.");
        if (!SshRecovery.TryNormalizeConnectionArguments(connectionArguments, out var normalized, out var destination))
            return new(false, null, "The active SSH connection could not be verified safely.");
        var fileName = CreateRemoteFileName(DateTime.UtcNow, Guid.NewGuid(), extension);
        var remoteCommand = BuildRemoteCommand(fileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = sshExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in BuildSshArguments(normalized, destination, remoteCommand)) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new(false, null, "Could not start the SSH file transfer.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await contents.CopyToAsync(process.StandardInput.BaseStream);
            await process.StandardInput.BaseStream.FlushAsync();
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(45)); }
            catch (TimeoutException)
            {
                try { process.Kill(true); } catch { }
                return new(false, null, "The SSH file upload timed out.");
            }
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) return new(false, null, AbbreviateError(error));
            return TryReadRemotePath(output, out var remotePath)
                ? new(true, remotePath, null)
                : new(false, null, "The remote host did not confirm the uploaded file path.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            return new(false, null, exception.Message);
        }
    }

    internal static string CreateRemoteFileName(DateTime utcNow, Guid randomValue, string extension)
    {
        var safeExtension = NormalizeExtension(extension);
        return $"file-{utcNow:HHmmss}-{randomValue:N}"[..20] + safeExtension;
    }

    internal static string[] BuildSshArgumentsForTest(string[] connectionArguments, string fileName)
    {
        if (!SshRecovery.TryNormalizeConnectionArguments(connectionArguments, out var normalized, out var destination)) return [];
        return BuildSshArguments(normalized, destination, BuildRemoteCommand(fileName)).ToArray();
    }

    internal static bool TryReadRemotePath(string? output, out string remotePath)
    {
        remotePath = (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(value => value.StartsWith(ResultPrefix, StringComparison.Ordinal))?[ResultPrefix.Length..] ?? string.Empty;
        return RemotePathPattern.IsMatch(remotePath) && !remotePath.Contains("..", StringComparison.Ordinal);
    }

    private static IEnumerable<string> BuildSshArguments(string[] normalized, string destination, string remoteCommand)
    {
        foreach (var argument in normalized.Take(normalized.Length - 1)) yield return argument;
        yield return "-T";
        yield return "-o";
        yield return "BatchMode=yes";
        yield return "-o";
        yield return "ConnectTimeout=12";
        yield return destination;
        yield return remoteCommand;
    }

    private static string BuildRemoteCommand(string fileName)
    {
        if (!Regex.IsMatch(fileName, @"^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Unsafe remote file name.", nameof(fileName));
        return "umask 077; dir=\"$HOME/.cache/powershellplus/files\"; mkdir -p -- \"$dir\" "
            + $"&& path=\"$dir/{fileName}\"; set -C; cat > \"$path\" && chmod 600 -- \"$path\" "
            + $"&& printf '\n{ResultPrefix}%s\n' \"$path\"";
    }

    private static string NormalizeExtension(string? extension)
    {
        var value = (extension ?? string.Empty).Trim();
        if (value.Length > 0 && !value.StartsWith('.')) value = "." + value;
        return ExtensionPattern.IsMatch(value) ? value.ToLowerInvariant() : ".bin";
    }

    private static string AbbreviateError(string error)
    {
        var value = string.Join(" ", (error ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (value.Length == 0) return "The SSH file upload failed.";
        return value.Length <= 240 ? value : value[..237] + "...";
    }
}
