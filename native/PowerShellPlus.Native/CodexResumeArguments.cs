namespace PowerShellPlus.Native;

/// <summary>
/// Reconstructs only the public, stable Codex CLI permission switches. Permission-profile
/// metadata describes how Codex arrived at the effective policy; it is not itself a scalar
/// config value and must never be written back as default_permissions.
/// </summary>
internal static class CodexResumeArguments
{
    public static bool TryBuild(string? permissionProfile, string? sandboxMode, string? approvalPolicy,
        string? approvalsReviewer, out string[] arguments)
    {
        arguments = [];
        var effectiveSandbox = ResolveSandboxMode(permissionProfile, sandboxMode);
        if (!CodexSessionLocator.IsSafeCodexSandboxMode(effectiveSandbox)
            || !CodexSessionLocator.IsSafeCodexApprovalPolicy(approvalPolicy)
            || approvalsReviewer is not null && !CodexSessionLocator.IsSafeCodexApprovalsReviewer(approvalsReviewer))
            return false;

        var result = new List<string>
        {
            "--sandbox", effectiveSandbox!,
        };
        if (approvalsReviewer is not null)
        {
            result.Add("--config");
            result.Add($"approvals_reviewer=\"{approvalsReviewer}\"");
        }
        result.Add("--ask-for-approval");
        result.Add(approvalPolicy!);
        arguments = [.. result];
        return true;
    }

    public static string? ResolveSandboxMode(string? permissionProfile, string? sandboxMode)
    {
        if (CodexSessionLocator.IsSafeCodexSandboxMode(sandboxMode)) return sandboxMode;
        return permissionProfile switch
        {
            "disabled" or ":danger-full-access" => "danger-full-access",
            ":workspace" => "workspace-write",
            ":read-only" => "read-only",
            _ => null
        };
    }

    public static string BuildPowerShell(string? permissionProfile, string? sandboxMode, string? approvalPolicy,
        string? approvalsReviewer)
    {
        if (!TryBuild(permissionProfile, sandboxMode, approvalPolicy, approvalsReviewer, out var arguments))
            throw new InvalidOperationException("The saved Codex permission level cannot be translated to supported CLI controls.");
        return string.Concat(arguments.Select(value => value.StartsWith("--", StringComparison.Ordinal)
            ? " " + value
            : " '" + value.Replace("'", "''") + "'"));
    }
}
