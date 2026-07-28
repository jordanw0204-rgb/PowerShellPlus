using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PowerShellPlus.Native;

internal readonly record struct PersistentSshCommandResult(bool Succeeded, int ExitCode, string Output, string Message);

/// <summary>
/// Maintains one non-PTY SSH command session and frames individual commands
/// over its standard streams. This avoids a complete SSH handshake for every
/// tmux scrollbar wheel event while retaining the validated OpenSSH boundary.
/// </summary>
internal sealed class PersistentSshCommandChannel : IDisposable
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private readonly string[] connectionArguments;
    private readonly string destination;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object processSync = new();
    private Process? process;
    private Task<string>? stderrDrain;
    private bool completedCommand;
    private bool disposed;

    private PersistentSshCommandChannel(string[] connectionArguments, string destination)
    {
        this.connectionArguments = connectionArguments;
        this.destination = destination;
    }

    public static bool TryCreate(SessionRecoveryEntry recovery, out PersistentSshCommandChannel? channel)
    {
        channel = null;
        if (!SshRecovery.TryNormalizeConnectionArguments(recovery.SshConnectionArguments, out var normalized, out var destination))
            return false;
        channel = new PersistentSshCommandChannel(normalized, destination);
        return true;
    }

    public async Task<PersistentSshCommandResult> ExecuteAsync(string remoteCommand, CancellationToken cancellationToken = default)
    {
        if (disposed || string.IsNullOrWhiteSpace(remoteCommand))
            return new PersistentSshCommandResult(false, -1, string.Empty, "The persistent SSH command channel is unavailable.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try { await commandGate.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return new PersistentSshCommandResult(false, -1, string.Empty, "The persistent SSH command channel was closed.");
        }
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var active = EnsureProcess();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    timeout.CancelAfter(completedCommand ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(12));
                    var marker = "PSP_SSH_RESULT_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                    var frame = BuildCommandFrame(remoteCommand, marker);
                    await active.StandardInput.WriteLineAsync(frame.AsMemory(), timeout.Token).ConfigureAwait(false);
                    await active.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

                    while (true)
                    {
                        var line = await active.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                        if (line is null) throw new IOException("The persistent SSH command channel closed unexpectedly.");
                        if (line.StartsWith(marker + ":", StringComparison.Ordinal))
                        {
                            var framed = line[(marker.Length + 1)..];
                            var separator = framed.IndexOf(':');
                            if (separator <= 0) throw new IOException("The persistent SSH command channel returned an incomplete result marker.");
                            var statusText = framed[..separator];
                            if (!int.TryParse(statusText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode))
                                throw new IOException("The persistent SSH command channel returned an invalid result marker.");
                            string output;
                            try { output = Encoding.UTF8.GetString(Convert.FromBase64String(framed[(separator + 1)..])); }
                            catch (FormatException) { throw new IOException("The persistent SSH command channel returned invalid output data."); }
                            if (output.Length > MaximumOutputCharacters) output = output[..MaximumOutputCharacters];
                            completedCommand = true;
                            return new PersistentSshCommandResult(exitCode == 0, exitCode, output,
                                exitCode == 0 ? "SSH command completed." : $"SSH command exited with code {exitCode}.");
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
                {
                    ResetProcess();
                    if (attempt == 1)
                        return new PersistentSshCommandResult(false, -1, string.Empty, "The persistent SSH command timed out.");
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    return new PersistentSshCommandResult(false, -1, string.Empty, "The persistent SSH command channel was closed.");
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    ResetProcess();
                    if (attempt == 1)
                        return new PersistentSshCommandResult(false, -1, string.Empty, exception.Message);
                }
            }
            return new PersistentSshCommandResult(false, -1, string.Empty, "The persistent SSH command failed.");
        }
        finally
        {
            commandGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        ResetProcess();
    }

    internal static bool ContractPassesForTest()
    {
        var recovery = new SessionRecoveryEntry
        {
            SshWasActive = true,
            SshConnectionArguments = ["-i", @"C:\Users\Example\.ssh\vps_key", "ubuntu@example.com"]
        };
        if (!TryCreate(recovery, out var channel) || channel is null) return false;
        try
        {
            var start = channel.BuildStartInfo();
            const string command = "tmux display-message -p '#{history_size}'";
            const string marker = "PSP_SSH_RESULT_0123456789abcdef";
            var frame = BuildCommandFrame(command, marker);
            return !start.UseShellExecute && start.CreateNoWindow
                && start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError
                && start.ArgumentList.Contains("-T") && start.ArgumentList.Contains("-a") && start.ArgumentList.Contains("-x")
                && start.ArgumentList.Contains("BatchMode=yes") && start.ArgumentList.Contains("ServerAliveInterval=15")
                && start.ArgumentList[^1] == "sh"
                && !frame.Contains(command, StringComparison.Ordinal)
                && frame.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(command)), StringComparison.Ordinal)
                && frame.Contains(marker, StringComparison.Ordinal)
                && frame.Contains("mktemp", StringComparison.Ordinal)
                && frame.Contains("base64 <", StringComparison.Ordinal);
        }
        finally { channel.Dispose(); }
    }

    private Process EnsureProcess()
    {
        lock (processSync)
        {
            if (process is { HasExited: false }) return process;
            ResetProcessLocked();
            var active = new Process { StartInfo = BuildStartInfo(), EnableRaisingEvents = true };
            if (!active.Start())
            {
                active.Dispose();
                throw new InvalidOperationException("Windows could not start the persistent SSH command channel.");
            }
            process = active;
            stderrDrain = active.StandardError.ReadToEndAsync();
            completedCommand = false;
            return active;
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var start = new ProcessStartInfo
        {
            FileName = "ssh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in connectionArguments.Take(connectionArguments.Length - 1)) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("-T");
        start.ArgumentList.Add("-a");
        start.ArgumentList.Add("-x");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("BatchMode=yes");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ConnectTimeout=6");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ServerAliveInterval=15");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ServerAliveCountMax=2");
        start.ArgumentList.Add(destination);
        start.ArgumentList.Add("sh");
        return start;
    }

    private static string BuildCommandFrame(string remoteCommand, string marker)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(remoteCommand));
        return $"umask 077; __psp_out=$(mktemp) || exit 125; "
            + $"printf %s '{payload}' | base64 -d | sh >\"$__psp_out\"; __psp_rc=$?; "
            + "__psp_data=$(base64 <\"$__psp_out\" | tr -d '\\r\\n'); rm -f \"$__psp_out\"; "
            + $"printf '\\n{marker}:%s:%s\\n' \"$__psp_rc\" \"$__psp_data\"";
    }

    private void ResetProcess()
    {
        lock (processSync) ResetProcessLocked();
    }

    private void ResetProcessLocked()
    {
        var previous = process;
        process = null;
        stderrDrain = null;
        completedCommand = false;
        if (previous is null) return;
        try { previous.StandardInput.Close(); } catch { }
        try { if (!previous.HasExited) previous.Kill(true); } catch { }
        previous.Dispose();
    }
}
