using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerShellPlus.Native;

public sealed class SshLaunchMarker
{
    public int Version { get; set; } = 2;
    public string PaneId { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public int? ShellProcessId { get; set; }
    public string WorkingDirectory { get; set; } = string.Empty;
    public string[] ConnectionArguments { get; set; } = [];
    public bool RecoveryAttempt { get; set; }
    public int? ExitCode { get; set; }
    public DateTime? EndedUtc { get; set; }
    public bool IsActive => EndedUtc is null && StartedUtc > DateTime.UnixEpoch;
    public bool IsFailedRecovery => RecoveryAttempt && EndedUtc is not null && ExitCode == 255;
}

public static class SshLaunchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string? DirectoryOverride { get; set; }
    public static string DirectoryPath => DirectoryOverride ?? Path.Combine(SessionRecoveryStore.DirectoryPath, "ssh-launches");

    public static SshLaunchMarker? Load(string paneId, string? directoryPath = null)
    {
        try
        {
            var path = MarkerPath(paneId, directoryPath);
            if (!File.Exists(path)) return null;
            var marker = JsonSerializer.Deserialize<SshLaunchMarker>(File.ReadAllText(path), JsonOptions);
            if (marker is null || marker.Version is not (1 or 2) || marker.PaneId != paneId
                || !SshRecovery.TryNormalizeConnectionArguments(marker.ConnectionArguments, out var normalized, out _)) return null;
            marker.ConnectionArguments = normalized;
            return marker;
        }
        catch { return null; }
    }

    public static void Save(SshLaunchMarker marker, string? directoryPath = null)
    {
        if (!SshRecovery.TryNormalizeConnectionArguments(marker.ConnectionArguments, out var normalized, out _))
            throw new InvalidOperationException("Refusing to save an unsafe SSH recovery marker.");
        marker.ConnectionArguments = normalized;
        marker.Version = 2;
        var directory = directoryPath ?? DirectoryPath;
        Directory.CreateDirectory(directory);
        var path = MarkerPath(marker.PaneId, directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(marker, JsonOptions));
        File.Move(temporary, path, true);
    }

    public static string BuildPowerShellWrapper(string paneId, string? directoryPath = null)
    {
        var directory = directoryPath ?? DirectoryPath;
        Directory.CreateDirectory(directory);
        var markerPath = MarkerPath(paneId, directory);
        var escapedPaneId = EscapePowerShell(paneId);
        var escapedMarkerPath = EscapePowerShell(markerPath);
        return $$"""
$global:__PowerShellPlusSshCommand = (Get-Command ssh.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source;
if ($global:__PowerShellPlusSshCommand) {
    function global:ssh {
        $__pspArgs = @($args);
        $__pspSafe = [System.Collections.Generic.List[string]]::new();
        $__pspNoValue = @('-4', '-6', '-A', '-a', '-C', '-K', '-k', '-q', '-t', '-tt', '-X', '-x', '-Y');
        $__pspValue = @('-F', '-i', '-J', '-l', '-o', '-p');
        $__pspTestDestination = {
            param([string]$Value)
            if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 512 -or $Value.StartsWith('-') -or $Value -match '[\x00-\x1F\x7F]') { return $false }
            if ($Value.StartsWith('ssh://', [StringComparison]::OrdinalIgnoreCase)) {
                if ($Value -notmatch '^ssh://[A-Za-z0-9._~@:\[\]-]{1,500}$') { return $false }
                try { $__pspUri = [Uri]$Value } catch { return $false }
                if ($__pspUri.Scheme -ne 'ssh' -or [string]::IsNullOrWhiteSpace($__pspUri.Host) -or $__pspUri.UserInfo.Contains(':') -or $__pspUri.Query -or $__pspUri.Fragment -or ($__pspUri.AbsolutePath -and $__pspUri.AbsolutePath -ne '/')) { return $false }
                return -not $__pspUri.UserInfo -or $__pspUri.UserInfo -match '^[A-Za-z0-9._~-]{1,128}$'
            }
            if ($Value -notmatch '^[A-Za-z0-9._~@:\[\]-]{1,512}$' -or ([regex]::Matches($Value, '@')).Count -gt 1) { return $false }
            $__pspAt = $Value.IndexOf('@');
            return $__pspAt -lt 0 -or ($__pspAt -gt 0 -and $__pspAt -lt $Value.Length - 1 -and $Value.Substring(0, $__pspAt) -match '^[A-Za-z0-9._~-]{1,128}$')
        };
        $__pspTestOption = {
            param([string]$Option, [string]$Value)
            if ([string]::IsNullOrEmpty($Value) -or $Value.Length -gt 1024 -or $Value -match '[\x00-\x1F\x7F]') { return $false }
            switch ($Option) {
                '-p' { $__pspPort = 0; return [int]::TryParse($Value, [ref]$__pspPort) -and $__pspPort -ge 1 -and $__pspPort -le 65535 }
                '-l' { return $Value -match '^[A-Za-z0-9._~-]{1,128}$' }
                '-J' { $__pspJumps = @($Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries)); if ($__pspJumps.Count -eq 0) { return $false }; foreach ($__pspJump in $__pspJumps) { if (-not (& $__pspTestDestination $__pspJump)) { return $false } }; return $true }
                '-o' {
                    if ($Value -notmatch '^(?<name>ConnectionAttempts|ConnectTimeout|ServerAliveInterval|ServerAliveCountMax)=(?<number>[0-9]{1,3})$') { return $false }
                    $__pspNumber = [int]$Matches['number'];
                    switch ($Matches['name']) {
                        'ConnectionAttempts' { return $__pspNumber -ge 1 -and $__pspNumber -le 5 }
                        'ConnectTimeout' { return $__pspNumber -ge 1 -and $__pspNumber -le 60 }
                        'ServerAliveInterval' { return $__pspNumber -ge 1 -and $__pspNumber -le 300 }
                        'ServerAliveCountMax' { return $__pspNumber -ge 1 -and $__pspNumber -le 10 }
                    }
                    return $false
                }
                '-F' { return $true }
                '-i' { return $true }
                default { return $false }
            }
        };
        $__pspValid = $true;
        $__pspDestinationSeen = $false;
        $__pspDestinationIndex = -1;
        $__pspIndex = 0;
        while ($__pspIndex -lt $__pspArgs.Count -and -not $__pspDestinationSeen) {
            $__pspArg = [string]$__pspArgs[$__pspIndex];
            if ($__pspArg -eq '--') {
                $__pspIndex++;
                if ($__pspIndex -ge $__pspArgs.Count) { $__pspValid = $false; break }
                $__pspDestination = [string]$__pspArgs[$__pspIndex];
                if (-not (& $__pspTestDestination $__pspDestination)) { $__pspValid = $false; break }
                $__pspSafe.Add($__pspDestination);
                $__pspDestinationSeen = $true;
                $__pspDestinationIndex = $__pspIndex;
                $__pspIndex++;
                break;
            }
            if ($__pspArg -in $__pspNoValue) {
                if ($__pspArg -notin @('-t', '-tt')) { $__pspSafe.Add($__pspArg) }
                $__pspIndex++;
                continue;
            }
            if ($__pspArg -in $__pspValue) {
                if ($__pspIndex + 1 -ge $__pspArgs.Count) { $__pspValid = $false; break }
                $__pspOptionValue = [string]$__pspArgs[$__pspIndex + 1];
                if (-not (& $__pspTestOption $__pspArg $__pspOptionValue)) { $__pspValid = $false; break }
                if ($__pspArg -ne '-o') {
                    $__pspSafe.Add($__pspArg);
                    $__pspSafe.Add($__pspOptionValue);
                }
                $__pspIndex += 2;
                continue;
            }
            if ($__pspArg -match '^-(?<key>[FiJlp])(?<value>.+)$') {
                $__pspCombinedOption = '-' + $Matches['key'];
                if (-not (& $__pspTestOption $__pspCombinedOption $Matches['value'])) { $__pspValid = $false; break }
                $__pspSafe.Add($__pspArg);
                $__pspIndex++;
                continue;
            }
            if ($__pspArg.StartsWith('-')) { $__pspValid = $false; break }
            if (-not (& $__pspTestDestination $__pspArg)) { $__pspValid = $false; break }
            $__pspSafe.Add($__pspArg);
            $__pspDestinationSeen = $true;
            $__pspDestinationIndex = $__pspIndex;
            $__pspIndex++;
        }
        $__pspInternalRemoteCommand = $false;
        if ($__pspDestinationSeen -and $__pspIndex -ne $__pspArgs.Count) {
            $__pspExpectedRemotePrefix = 'export POWERSHELLPLUS_PANE_ID=''{{escapedPaneId}}''; ';
            if ([bool]$global:__PowerShellPlusSshRecoveryActive -and $__pspIndex -eq $__pspArgs.Count - 1 -and ([string]$__pspArgs[$__pspIndex]).StartsWith($__pspExpectedRemotePrefix, [StringComparison]::Ordinal)) {
                $__pspInternalRemoteCommand = $true;
                $__pspIndex++;
            }
            else { $__pspValid = $false }
        }
        $__pspMarker = $null;
        if ($__pspValid -and $__pspDestinationSeen) {
            $__pspMarker = [ordered]@{
                Version = 2;
                PaneId = '{{escapedPaneId}}';
                StartedUtc = [DateTime]::UtcNow.ToString('O');
                ShellProcessId = $PID;
                WorkingDirectory = (Get-Location).ProviderPath;
                ConnectionArguments = @($__pspSafe.ToArray());
                RecoveryAttempt = [bool]$global:__PowerShellPlusSshRecoveryActive;
                ExitCode = $null;
                EndedUtc = $null
            };
            $__pspMarker | ConvertTo-Json -Compress | Set-Content -LiteralPath '{{escapedMarkerPath}}' -Encoding UTF8;
        }
        $__pspExitCode = $null;
        $__pspInvokeArgs = @($__pspArgs);
        if ($null -ne $__pspMarker -and -not $__pspInternalRemoteCommand) {
            $__pspInstrumented = [System.Collections.Generic.List[string]]::new();
            for ($__pspCopyIndex = 0; $__pspCopyIndex -lt $__pspDestinationIndex; $__pspCopyIndex++) { $__pspInstrumented.Add([string]$__pspArgs[$__pspCopyIndex]) }
            $__pspInstrumented.Add('-tt');
            $__pspInstrumented.Add([string]$__pspArgs[$__pspDestinationIndex]);
            $__pspInstrumented.Add('export POWERSHELLPLUS_PANE_ID=''{{escapedPaneId}}''; exec "${SHELL:-/bin/sh}" -l');
            $__pspInvokeArgs = @($__pspInstrumented.ToArray());
        }
        try { & $global:__PowerShellPlusSshCommand @__pspInvokeArgs; $__pspExitCode = $LASTEXITCODE }
        finally {
            if ($null -ne $__pspMarker) {
                $__pspMarker.ExitCode = $__pspExitCode;
                $__pspMarker.EndedUtc = [DateTime]::UtcNow.ToString('O');
                $__pspMarker | ConvertTo-Json -Compress | Set-Content -LiteralPath '{{escapedMarkerPath}}' -Encoding UTF8;
            }
        }
    }
}
""";
    }

    private static string MarkerPath(string paneId, string? directoryPath = null)
        => Path.Combine(directoryPath ?? DirectoryPath, SessionRecoveryStore.SafeSessionId(paneId) + ".json");

    private static string EscapePowerShell(string value) => value.Replace("'", "''");
}

public readonly record struct HermesRecoveryState(bool WasActive, string? SessionId, string? Model, bool UseTui);
public sealed record SshResumePlan(string[] Arguments, string Destination, string Description);

public static class HermesRecovery
{
    private static readonly Regex SessionIdPattern = new(@"^\d{8}_\d{6}_[a-f0-9]{6,8}$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ModelPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._:/@+\-]{0,255}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SessionIdEvidencePattern = new(@"(?im)(?:(?:Session(?:\s+ID)?|HERMES_SESSION_ID)\s*[:=]\s*|Resumed\s+session\s+|hermes(?:\s+chat)?(?:\s+--tui)?[^\r\n]{0,80}?(?:--resume|-r)\s+)(?<id>\d{8}_\d{6}_[a-f0-9]{6,8})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InvocationPattern = new(@"(?im)^(?:(?:[^\r\n]{0,160})[$#>❯➜]\s*)?hermes(?:\s+(?:chat\b|--tui\b|-c\b|--continue\b|-r\b|--resume\b)|\s*$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TuiPattern = new(@"(?im)^(?:(?:[^\r\n]{0,160})[$#>❯➜]\s*)?hermes(?:\s+chat)?[^\r\n]*\s--tui(?:\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BannerPattern = new(@"(?i)\bHermes Agent\b|\bPrevious Conversation\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BannerModelPattern = new(@"(?im)^[ \t]*[^\r\nA-Za-z0-9]{0,12}[ \t]*(?<model>[A-Za-z0-9][A-Za-z0-9._:/@+\-]{0,255})[ \t]+·[ \t]+\d+[ \t]+tools\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StatusModelPattern = new(@"(?im)^[ \t]*[^\r\nA-Za-z0-9]{0,12}[ \t]*(?<model>[A-Za-z0-9][A-Za-z0-9._:/@+\-]{0,255})[ \t]+(?:·|│)[ \t]+(?:ctx[ \t]+--|\d+(?:\.\d+)?(?:K(?:/\d+(?:\.\d+)?K)?|%))(?=[ \t]*(?:·|│|$))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ModelCommandPattern = new(@"(?im)^[ \t]*(?:[^\r\n]{0,160}[>❯➜][ \t]*)?/model[ \t]+(?<model>[A-Za-z0-9][A-Za-z0-9._:/@+\-]{0,255})(?:[ \t]+--global)?[ \t]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static HermesRecoveryState Detect(string? terminalOutput, HermesRecoveryState previous = default)
    {
        var output = terminalOutput ?? string.Empty;
        var invocation = InvocationPattern.Matches(output).Cast<Match>().LastOrDefault();
        var lastInvocation = invocation?.Index ?? -1;
        var banner = BannerPattern.Matches(output).Cast<Match>().LastOrDefault();
        var lastEvidence = Math.Max(lastInvocation, banner?.Index ?? -1);
        var lastExit = output.LastIndexOf("Resume this session with:", StringComparison.OrdinalIgnoreCase);
        var strongHermesEvidence = lastEvidence >= 0;
        var wasActive = strongHermesEvidence && lastExit <= lastEvidence;
        if (!strongHermesEvidence && previous.WasActive) wasActive = true;

        string? sessionId = null;
        if (strongHermesEvidence)
            sessionId = SessionIdEvidencePattern.Matches(output).Cast<Match>().LastOrDefault()?.Groups["id"].Value;
        sessionId = IsSafeSessionId(sessionId) ? sessionId : previous.WasActive && IsSafeSessionId(previous.SessionId) ? previous.SessionId : null;
        var modelEvidence = BannerModelPattern.Matches(output).Cast<Match>()
            .Concat(StatusModelPattern.Matches(output).Cast<Match>())
            .Concat(ModelCommandPattern.Matches(output).Cast<Match>())
            .Where(match => IsSafeModel(match.Groups["model"].Value))
            .OrderBy(match => match.Index)
            .LastOrDefault();
        var model = modelEvidence?.Groups["model"].Value;
        model = IsSafeModel(model) ? model : previous.WasActive && IsSafeModel(previous.Model) ? previous.Model : null;
        var useTui = TuiPattern.IsMatch(output) || previous.WasActive && previous.UseTui;
        return new HermesRecoveryState(wasActive, sessionId, model, useTui);
    }

    public static bool IsSafeSessionId(string? value)
        => value is { Length: >= 22 and <= 24 } && SessionIdPattern.IsMatch(value);

    public static bool IsSafeModel(string? value)
        => value is { Length: >= 1 and <= 256 } && ModelPattern.IsMatch(value);
}

public static class SshRecovery
{
    private static readonly HashSet<string> NoValueOptions = new(StringComparer.Ordinal)
    {
        "-4", "-6", "-A", "-a", "-C", "-K", "-k", "-q", "-t", "-tt", "-X", "-x", "-Y"
    };
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal) { "-F", "-i", "-J", "-l", "-o", "-p" };
    private static readonly Regex UserPattern = new(@"^[A-Za-z0-9._~-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DestinationPattern = new(@"^[A-Za-z0-9._~@:\[\]-]{1,512}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SshUriPattern = new(@"^ssh://[A-Za-z0-9._~@:\[\]-]{1,500}$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryNormalizeConnectionArguments(IEnumerable<string>? arguments, out string[] normalized, out string destination)
    {
        normalized = [];
        destination = string.Empty;
        if (arguments is null) return false;
        var values = arguments.ToArray();
        if (values.Length is 0 or > 64 || values.Any(value => value is null || value.Length is 0 or > 1024)) return false;
        var safe = new List<string>(values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var argument = values[index];
            if (argument == "--")
            {
                if (++index >= values.Length || index != values.Length - 1 || !IsSafeDestination(values[index])) return false;
                destination = values[index];
                safe.Add(destination);
                break;
            }
            if (NoValueOptions.Contains(argument))
            {
                if (argument is not "-t" and not "-tt") safe.Add(argument);
                continue;
            }
            if (ValueOptions.Contains(argument))
            {
                if (++index >= values.Length || !IsSafeOptionValue(argument, values[index])) return false;
                safe.Add(argument);
                safe.Add(values[index]);
                continue;
            }
            if (argument.Length > 2 && ValueOptions.Contains(argument[..2]))
            {
                var option = argument[..2];
                var optionValue = argument[2..];
                if (!IsSafeOptionValue(option, optionValue)) return false;
                safe.Add(argument);
                continue;
            }
            if (argument.StartsWith("-", StringComparison.Ordinal) || !IsSafeDestination(argument) || index != values.Length - 1) return false;
            destination = argument;
            safe.Add(destination);
        }
        if (destination.Length == 0 || safe.Count == 0) return false;
        normalized = safe.ToArray();
        return true;
    }

    public static string? BuildPowerShellResumeCommand(SessionRecoveryEntry? recovery)
    {
        var plan = BuildResumePlan(recovery);
        if (plan is null) return null;
        var invocation = "& ssh " + string.Join(" ", plan.Arguments.Select(QuotePowerShell));
        return $"Write-Host '[PowerShellPlus] Restoring {plan.Description}...' -ForegroundColor Cyan; "
            + "$global:__PowerShellPlusSshRecoveryActive = $true; "
            + $"try {{ {invocation} }} finally {{ $global:__PowerShellPlusSshRecoveryActive = $false }}; "
            + "if ($LASTEXITCODE -ne 0) { Write-Warning '[PowerShellPlus] Automatic recovery could not connect. The saved session was kept; click the pane restart button to retry. This PowerShell prompt remains interactive.' }";
    }

    public static SshResumePlan? BuildResumePlan(SessionRecoveryEntry? recovery)
    {
        if (recovery?.SshWasActive != true
            || !TryNormalizeConnectionArguments(recovery.SshConnectionArguments, out var arguments, out var destination)) return null;
        var commandArguments = arguments.Take(arguments.Length - 1).ToList();
        AddReliabilityOption(commandArguments, "ConnectionAttempts", 2);
        AddReliabilityOption(commandArguments, "ConnectTimeout", 12);
        AddReliabilityOption(commandArguments, "ServerAliveInterval", 15);
        AddReliabilityOption(commandArguments, "ServerAliveCountMax", 3);
        commandArguments.Add("-tt");
        commandArguments.Add(destination);
        var paneId = SessionRecoveryStore.SafeSessionId(recovery.SessionId);
        string remoteCommand;
        if (recovery.HermesWasActive)
        {
            var hermesArguments = new List<string> { "hermes" };
            if (HermesRecovery.IsSafeModel(recovery.HermesModel))
            {
                hermesArguments.Add("--model");
                hermesArguments.Add(recovery.HermesModel!);
            }
            if (recovery.HermesUseTui) hermesArguments.Add("--tui");
            if (HermesRecovery.IsSafeSessionId(recovery.HermesSessionId))
            {
                hermesArguments.Add("--resume");
                hermesArguments.Add(recovery.HermesSessionId!);
            }
            else hermesArguments.Add("--continue");
            var hermesCommand = "exec " + string.Join(" ", hermesArguments.Select(QuotePosix));
            remoteCommand = $"export POWERSHELLPLUS_PANE_ID='{paneId}'; exec \"${{SHELL:-/bin/sh}}\" -lc {QuotePosix(hermesCommand)}";
        }
        else remoteCommand = RemoteCodexRecovery.BuildRemoteCommand(paneId, recovery);
        commandArguments.Add(remoteCommand);
        var description = recovery.HermesWasActive ? "SSH and Hermes session"
            : recovery.RemoteCodexWasActive ? "SSH and Codex session" : "SSH session";
        return new SshResumePlan(commandArguments.ToArray(), destination, description);
    }

    public static bool ShouldKeepPendingRecovery(SessionRecoveryEntry? previous, SshLaunchMarker? launch, bool sshProcessActive)
        => previous?.SshWasActive == true && launch?.RecoveryAttempt == true
            && (sshProcessActive || launch.IsFailedRecovery);

    public static bool ShouldPreserveTranscript(SessionRecoveryEntry? previous, SshLaunchMarker? launch,
        bool sshProcessActive, string? currentOutput)
        => ShouldKeepPendingRecovery(previous, launch, sshProcessActive)
            && (previous?.HermesWasActive == true && !HermesRecovery.Detect(currentOutput).WasActive
                || previous?.RemoteCodexWasActive == true);

    public static void Sanitize(SessionRecoveryEntry entry)
    {
        if (!entry.SshWasActive || !TryNormalizeConnectionArguments(entry.SshConnectionArguments, out var normalized, out _))
        {
            entry.SshWasActive = false;
            entry.SshConnectionArguments = [];
            entry.HermesWasActive = false;
            entry.HermesSessionId = null;
            entry.HermesModel = null;
            entry.HermesUseTui = false;
            RemoteCodexRecovery.Clear(entry);
            return;
        }
        entry.SshConnectionArguments = normalized;
        if (!entry.HermesWasActive)
        {
            entry.HermesSessionId = null;
            entry.HermesModel = null;
            entry.HermesUseTui = false;
        }
        else
        {
            if (!HermesRecovery.IsSafeSessionId(entry.HermesSessionId)) entry.HermesSessionId = null;
            if (!HermesRecovery.IsSafeModel(entry.HermesModel)) entry.HermesModel = null;
        }
        RemoteCodexRecovery.Sanitize(entry);
    }

    private static bool IsSafeOptionValue(string option, string value)
    {
        if (value.Length is 0 or > 1024 || value.Any(char.IsControl)) return false;
        return option switch
        {
            "-p" => int.TryParse(value, out var port) && port is >= 1 and <= 65535,
            "-l" => UserPattern.IsMatch(value),
            "-J" => value.Split(',', StringSplitOptions.RemoveEmptyEntries).Length > 0
                && value.Split(',', StringSplitOptions.RemoveEmptyEntries).All(IsSafeDestination),
            "-o" => IsSafeReliabilityOption(value),
            "-F" or "-i" => true,
            _ => false
        };
    }

    private static void AddReliabilityOption(List<string> arguments, string name, int value)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
            if (arguments[index] == "-o" && arguments[index + 1].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return;
        arguments.Add("-o");
        arguments.Add($"{name}={value}");
    }

    private static bool IsSafeReliabilityOption(string value)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator == value.Length - 1
            || !int.TryParse(value[(separator + 1)..], out var number)) return false;
        return value[..separator] switch
        {
            "ConnectionAttempts" => number is >= 1 and <= 5,
            "ConnectTimeout" => number is >= 1 and <= 60,
            "ServerAliveInterval" => number is >= 1 and <= 300,
            "ServerAliveCountMax" => number is >= 1 and <= 10,
            _ => false
        };
    }

    private static bool IsSafeDestination(string value)
    {
        if (value.Length is 0 or > 512 || value.StartsWith("-", StringComparison.Ordinal) || value.Any(char.IsControl)) return false;
        if (value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            if (!SshUriPattern.IsMatch(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || !uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(uri.Host)
                || uri.UserInfo.Contains(':', StringComparison.Ordinal) || uri.Query.Length > 0 || uri.Fragment.Length > 0
                || uri.AbsolutePath is not "" and not "/") return false;
            return uri.UserInfo.Length == 0 || UserPattern.IsMatch(uri.UserInfo);
        }
        if (!DestinationPattern.IsMatch(value) || value.Count(character => character == '@') > 1) return false;
        var at = value.IndexOf('@');
        return at < 0 || at > 0 && UserPattern.IsMatch(value[..at]) && at < value.Length - 1;
    }

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";
    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
