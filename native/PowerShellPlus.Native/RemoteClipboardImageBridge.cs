using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PowerShellPlus.Native;

internal readonly record struct RemoteImageUploadResult(bool Succeeded, string? RemotePath, string? Error);

internal static class RemoteClipboardImageBridge
{
    internal const int MaximumImageBytes = 20 * 1024 * 1024;
    private const string ResultPrefix = "PSP_REMOTE_IMAGE:";
    private static readonly Regex RemotePathPattern = new(@"^/[A-Za-z0-9._~/-]{1,4096}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<RemoteImageUploadResult> UploadPngAsync(byte[] imageBytes, string[] connectionArguments,
        TimeSpan? timeout = null, string sshExecutable = "ssh.exe")
    {
        if (imageBytes.Length is 0 or > MaximumImageBytes) return new(false, null, "Clipboard image is empty or larger than 20 MB.");
        if (!SshRecovery.TryNormalizeConnectionArguments(connectionArguments, out var normalized, out var destination))
            return new(false, null, "The active SSH connection could not be verified safely.");

        var fileName = CreateRemoteFileName(DateTime.UtcNow, Guid.NewGuid());
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
            if (process is null) return new(false, null, "Could not start the SSH image transfer.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.StandardInput.BaseStream.WriteAsync(imageBytes);
            await process.StandardInput.BaseStream.FlushAsync();
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                try { process.Kill(true); } catch { }
                return new(false, null, "The SSH image upload timed out.");
            }
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) return new(false, null, AbbreviateError(error));
            return TryReadRemotePath(output, out var remotePath)
                ? new(true, remotePath, null)
                : new(false, null, "The remote host did not confirm the uploaded image path.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            return new(false, null, exception.Message);
        }
    }

    internal static string[] BuildSshArgumentsForTest(string[] connectionArguments, string fileName)
    {
        if (!SshRecovery.TryNormalizeConnectionArguments(connectionArguments, out var normalized, out var destination)) return [];
        return BuildSshArguments(normalized, destination, BuildRemoteCommand(fileName)).ToArray();
    }

    internal static string CreateRemoteFileName(DateTime utcNow, Guid randomValue)
        => $"img-{utcNow:HHmmss}-{randomValue:N}"[..19] + ".png";

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
            throw new ArgumentException("Unsafe remote image name.", nameof(fileName));
        return "umask 077; dir=\"$HOME/.cache/powershellplus/images\"; mkdir -p -- \"$dir\" "
            + $"&& path=\"$dir/{fileName}\"; set -C; cat > \"$path\" && chmod 600 -- \"$path\" "
            + $"&& printf '\\n{ResultPrefix}%s\\n' \"$path\"";
    }

    private static string AbbreviateError(string error)
    {
        var value = string.Join(" ", (error ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (value.Length == 0) return "The SSH image upload failed.";
        return value.Length <= 240 ? value : value[..237] + "...";
    }
}
