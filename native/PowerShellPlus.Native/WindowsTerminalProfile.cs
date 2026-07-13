using System.Text.Json;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;

namespace PowerShellPlus.Native;

public sealed record TerminalAppearance(string ProfileName, string FontFace, int FontSize, TerminalTheme Theme);

public sealed class WindowsTerminalProfile
{
    public string ProfileName { get; init; } = "Windows PowerShell";
    public string CommandLine { get; init; } = "powershell.exe";
    public string FontFace { get; init; } = "Cascadia Mono";
    public int FontSize { get; init; } = 12;
    public string SchemeName { get; init; } = "Campbell";
    public TerminalTheme Theme { get; init; }
    public Color Background { get; init; } = Color.FromRgb(12, 12, 12);

    public static WindowsTerminalProfile Load()
    {
        var defaultTheme = CreateDefaultTheme();
        foreach (var settingsPath in CandidatePaths())
        {
            if (!File.Exists(settingsPath)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = document.RootElement;
                var defaultGuid = root.TryGetProperty("defaultProfile", out var defaultProfile) ? defaultProfile.GetString() : null;
                var defaults = root.GetProperty("profiles").TryGetProperty("defaults", out var profileDefaults) ? profileDefaults : default;
                JsonElement selected = default;
                foreach (var profile in root.GetProperty("profiles").GetProperty("list").EnumerateArray())
                {
                    if (profile.TryGetProperty("guid", out var guid) && string.Equals(guid.GetString(), defaultGuid, StringComparison.OrdinalIgnoreCase)) { selected = profile; break; }
                }

                var name = GetString(selected, "name") ?? "Windows PowerShell";
                var command = Environment.ExpandEnvironmentVariables(GetString(selected, "commandline") ?? ResolveGeneratedCommand(selected));
                var scheme = GetString(selected, "colorScheme") ?? GetString(defaults, "colorScheme") ?? "Campbell";
                var fontFace = GetNestedString(selected, "font", "face") ?? GetNestedString(defaults, "font", "face") ?? "Cascadia Mono";
                var fontSize = GetNestedInt(selected, "font", "size") ?? GetNestedInt(defaults, "font", "size") ?? 12;
                var colors = FindScheme(root, scheme);
                return new WindowsTerminalProfile
                {
                    ProfileName = name,
                    CommandLine = command,
                    FontFace = fontFace,
                    FontSize = fontSize,
                    SchemeName = scheme,
                    Background = ParseColor(GetString(colors, "background"), Color.FromRgb(12, 12, 12)),
                    Theme = BuildTheme(colors)
                };
            }
            catch { }
        }
        return new WindowsTerminalProfile { Theme = defaultTheme };
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");
        yield return Path.Combine(local, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json");
        yield return Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json");
    }

    private static string ResolveGeneratedCommand(JsonElement profile)
    {
        var source = GetString(profile, "source") ?? string.Empty;
        if (source.Contains("PowershellCore", StringComparison.OrdinalIgnoreCase)) return "pwsh.exe";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static JsonElement FindScheme(JsonElement root, string scheme)
    {
        if (root.TryGetProperty("schemes", out var schemes))
            foreach (var value in schemes.EnumerateArray()) if (string.Equals(GetString(value, "name"), scheme, StringComparison.OrdinalIgnoreCase)) return value;
        return default;
    }

    private static TerminalTheme BuildTheme(JsonElement scheme)
    {
        var fallback = CreateDefaultTheme();
        if (scheme.ValueKind == JsonValueKind.Undefined) return fallback;
        string[] names = ["black", "red", "green", "yellow", "blue", "purple", "cyan", "white", "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite"];
        var table = names.Select(name => EasyTerminalControl.ColorToVal(ParseColor(GetString(scheme, name), Colors.Gray))).ToArray();
        return new TerminalTheme
        {
            DefaultBackground = EasyTerminalControl.ColorToVal(ParseColor(GetString(scheme, "background"), Color.FromRgb(12, 12, 12))),
            DefaultForeground = EasyTerminalControl.ColorToVal(ParseColor(GetString(scheme, "foreground"), Colors.Gainsboro)),
            DefaultSelectionBackground = EasyTerminalControl.ColorToVal(ParseColor(GetString(scheme, "selectionBackground"), Color.FromRgb(80, 80, 90))),
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable = table
        };
    }

    private static TerminalTheme CreateDefaultTheme()
    {
        uint C(byte r, byte g, byte b) => EasyTerminalControl.ColorToVal(Color.FromRgb(r, g, b));
        return new TerminalTheme { DefaultBackground = C(12, 12, 12), DefaultForeground = C(204, 204, 204), DefaultSelectionBackground = C(70, 70, 80), CursorStyle = CursorStyle.BlinkingBar, ColorTable = [C(12,12,12),C(197,15,31),C(19,161,14),C(193,156,0),C(0,55,218),C(136,23,152),C(58,150,221),C(204,204,204),C(118,118,118),C(231,72,86),C(22,198,12),C(249,241,165),C(59,120,255),C(180,0,158),C(97,214,214),C(242,242,242)] };
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(value); } catch { return fallback; }
    }
    private static string? GetString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    private static string? GetNestedString(JsonElement value, string parent, string child) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(parent, out var nested) ? GetString(nested, child) : null;
    private static int? GetNestedInt(JsonElement value, string parent, string child) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(parent, out var nested) && nested.TryGetProperty(child, out var number) && number.TryGetInt32(out var result) ? result : null;
}
