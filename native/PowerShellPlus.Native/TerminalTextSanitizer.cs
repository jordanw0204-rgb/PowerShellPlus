using System.Text.RegularExpressions;
using EasyWindowsTerminalControl;

namespace PowerShellPlus.Native;

/// <summary>
/// Removes terminal protocol traffic that must never become visible text.
/// The live filter is deliberately narrow (device-attribute replies only),
/// while transcript cleanup removes all ANSI/OSC control sequences because a
/// recovered transcript is rendered by a plain WPF TextBox rather than a VT
/// parser.
/// </summary>
internal static class TerminalTextSanitizer
{
    private static readonly Regex DeviceAttributeReply = new(
        "(?:\\u001b\\[[?>][0-9;:]*c|\\[[?>][0-9;:]*c|(?<![A-Za-z0-9])(?:[0-9]{1,3};){3,}[0-9]{1,3}c(?=\\s|$))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TerminalControlSequence = new(
        "\\u001b(?:\\][^\\a\\u001b]*(?:\\a|\\u001b\\\\)|\\[[0-?]*[ -/]*[@-~]|[PX^_][\\s\\S]*?\\u001b\\\\|[@-_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyPowerShellDirectoryMarker = new(
        "e\\]9;9;\"[^\"\\r\\n]*\"(?:\\a|e\\\\)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ForLiveOutput(string value)
        => string.IsNullOrEmpty(value) ? string.Empty : DeviceAttributeReply.Replace(value, string.Empty);

    public static string ForTranscript(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var withoutProtocol = TerminalControlSequence.Replace(value, string.Empty);
        // TermPTY handles a few sequences its parser knows about. Run it after
        // the complete VT pass so it cannot turn an unknown ESC sequence into
        // visible text by removing only the introducer.
        withoutProtocol = TermPTY.StripColors(withoutProtocol);
        withoutProtocol = LegacyPowerShellDirectoryMarker.Replace(withoutProtocol, string.Empty);
        withoutProtocol = DeviceAttributeReply.Replace(withoutProtocol, string.Empty);
        return withoutProtocol.Replace("\r", string.Empty).Trim();
    }

    internal static bool RegressionCasesPassForTest()
    {
        const string normal = "normal;semicolon;text.cs";
        var polluted = "before\u001b]9;9;\"D:\\Dev\"\u001b\\after\n"
            + "e]9;9;\"D:\\Dev\"prompt\n"
            + "\u001b[>0;10;1c[>0;10;1c\n"
            + "1;22;23;24;28;32;42c\n" + normal;
        var cleaned = ForTranscript(polluted);
        return cleaned.Contains("beforeafter", StringComparison.Ordinal)
            && cleaned.Contains("prompt", StringComparison.Ordinal)
            && cleaned.Contains(normal, StringComparison.Ordinal)
            && !cleaned.Contains("9;9", StringComparison.Ordinal)
            && !cleaned.Contains("0;10;1c", StringComparison.Ordinal)
            && !cleaned.Contains("1;22;23;24;28;32;42c", StringComparison.Ordinal)
            && ForLiveOutput("x\u001b[>0;10;1cy") == "xy";
    }
}
