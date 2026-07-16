using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PowerShellPlus.Native;

internal static partial class TailscaleLoginManager
{
    private static readonly TimeSpan LoginUrlTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LoginCompletionTimeout = TimeSpan.FromMinutes(3.25);

    [GeneratedRegex("https://login\\.tailscale\\.com/[^\\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoginUrlPattern();

    internal static IReadOnlyList<string> BuildLoginArguments() => ["login", "--timeout=3m"];

    internal static bool IsOfficialLoginUri(Uri? uri) => uri is not null
        && uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.Host.Equals("login.tailscale.com", StringComparison.OrdinalIgnoreCase)
        && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0;

    internal static Uri ParseLoginUri(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (TryParseLoginUri(output, out var uri)) return uri;
        throw new InvalidOperationException("Tailscale did not provide a valid official browser sign-in address.");
    }

    internal static bool TryParseLoginUri(string output, out Uri uri)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (Match match in LoginUrlPattern().Matches(output))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '\'', '"');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) && IsOfficialLoginUri(parsed))
            {
                uri = parsed;
                return true;
            }
        }
        uri = null!;
        return false;
    }

    internal static ProcessStartInfo CreateLoginStartInfo(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in BuildLoginArguments()) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    internal static async Task SignInAsync(string executable, Action<Uri>? openBrowser = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException("The installed Tailscale command-line client is missing.", executable);

        using var process = new Process { StartInfo = CreateLoginStartInfo(executable), EnableRaisingEvents = true };
        var output = new StringBuilder();
        var outputGate = new object();
        var loginUriReady = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Capture(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null) return;
            string snapshot;
            lock (outputGate)
            {
                if (output.Length < 16_384) output.AppendLine(args.Data);
                snapshot = output.ToString();
            }
            try { loginUriReady.TrySetResult(ParseLoginUri(snapshot)); }
            catch (InvalidOperationException) { }
        }

        process.OutputDataReceived += Capture;
        process.ErrorDataReceived += Capture;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Windows did not start Tailscale's sign-in command.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var uriTask = loginUriReady.Task.WaitAsync(LoginUrlTimeout, cancellationToken);
            var first = await Task.WhenAny(uriTask, exitTask);
            if (first == exitTask && !loginUriReady.Task.IsCompleted)
            {
                await exitTask;
                if (process.ExitCode == 0) return;
                throw new InvalidOperationException(BuildFailure(process.ExitCode, output, outputGate));
            }

            Uri loginUri;
            try { loginUri = await uriTask; }
            catch (TimeoutException)
            {
                throw new TimeoutException("Tailscale did not provide a browser sign-in address within 20 seconds.");
            }

            (openBrowser ?? OpenOfficialBrowser)(loginUri);
            try { await exitTask.WaitAsync(LoginCompletionTimeout, cancellationToken); }
            catch (TimeoutException)
            {
                throw new TimeoutException("Tailscale sign-in was not completed within three minutes. Choose GLOBAL to try again.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(BuildFailure(process.ExitCode, output, outputGate));
        }
        finally
        {
            process.OutputDataReceived -= Capture;
            process.ErrorDataReceived -= Capture;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    internal static void OpenOfficialBrowser(Uri loginUri)
    {
        if (!IsOfficialLoginUri(loginUri))
            throw new InvalidOperationException("PowerShellPlus refused a non-Tailscale sign-in address.");
        using var browser = Process.Start(new ProcessStartInfo(loginUri.AbsoluteUri) { UseShellExecute = true });
        if (browser is null)
            throw new InvalidOperationException("Windows did not open Tailscale's browser sign-in page.");
    }

    private static string BuildFailure(int exitCode, StringBuilder output, object outputGate)
    {
        string detail;
        lock (outputGate) detail = output.ToString().Trim();
        detail = LoginUrlPattern().Replace(detail, "<official sign-in link>");
        if (detail.Length > 2_000) detail = detail[..2_000];
        return detail.Length == 0
            ? $"Tailscale sign-in stopped with exit code {exitCode}."
            : $"Tailscale sign-in stopped with exit code {exitCode}.{Environment.NewLine}{detail}";
    }
}
