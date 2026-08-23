using System.Windows;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;

namespace PowerShellPlus.Native;

internal sealed record AppThemeDefinition(
    string Id,
    string Name,
    string Description,
    string Background,
    string Surface,
    string Surface2,
    string Raised,
    string Hover,
    string Border,
    string BorderStrong,
    string BorderEmphasis,
    string Text,
    string TextSecondary,
    string Muted,
    string MutedStrong,
    string Accent,
    string AccentSoft,
    string Success,
    string Warning,
    string Danger,
    string Teal,
    string Purple,
    string TerminalBackground,
    string TerminalForeground,
    string TerminalSelection)
{
    public Brush PreviewBackground => AppThemeCatalog.Brush(Background);
    public Brush PreviewSurface => AppThemeCatalog.Brush(Surface2);
    public Brush PreviewAccent => AppThemeCatalog.Brush(Accent);
    public Brush PreviewText => AppThemeCatalog.Brush(Text);
}

internal static class AppThemeCatalog
{
    internal const string DefaultThemeId = "mocha";
    internal const string BlackThemeId = "obsidian";

    internal static IReadOnlyList<AppThemeDefinition> Themes { get; } =
    [
        new(
            DefaultThemeId, "Mocha", "The original soft lavender workspace",
            "#11111B", "#181825", "#1E1E2E", "#242438", "#313244", "#313244", "#45475A", "#585B70",
            "#CDD6F4", "#A6ADC8", "#7F849C", "#6C7086", "#89B4FA", "#B4BEFE", "#A6E3A1", "#F9E2AF",
            "#F38BA8", "#94E2D5", "#CBA6F7", "#11111B", "#CDD6F4", "#45475A"),
        new(
            BlackThemeId, "Obsidian", "True black with crisp electric-blue detail",
            "#000000", "#050507", "#0A0A0D", "#111116", "#191921", "#24242E", "#323240", "#464658",
            "#F5F7FF", "#C9CCDA", "#85899B", "#696D7D", "#78A8FF", "#ACC8FF", "#6FE39A", "#FFD37A",
            "#FF7391", "#63E6D3", "#C8A4FF", "#000000", "#F5F7FF", "#30384C"),
        new(
            "midnight", "Midnight", "Deep navy surfaces with luminous cyan",
            "#070B14", "#0B1120", "#10182A", "#162139", "#1C2A45", "#253653", "#334B70", "#476791",
            "#E7F0FF", "#B7C7E2", "#7689A8", "#5F7394", "#67C7FF", "#A5DCFF", "#74E2B8", "#FFD078",
            "#FF7597", "#5CE1D0", "#B7A2FF", "#070B14", "#E7F0FF", "#263B5E"),
        new(
            "graphite", "Graphite", "Balanced charcoal with a cool violet accent",
            "#0D0E11", "#14161A", "#1B1E24", "#232731", "#2B303B", "#343A46", "#464E5D", "#5B6576",
            "#F0F2F7", "#C4C9D4", "#858C9B", "#6C7482", "#9AA8FF", "#C3CAFF", "#75DB9A", "#F5CA72",
            "#F47791", "#62D9CD", "#C3A7FF", "#0D0E11", "#F0F2F7", "#373D4B")
    ];

    internal static AppThemeDefinition Resolve(string? id) => Themes.FirstOrDefault(theme =>
        string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    internal static string Normalize(string? id) => Resolve(id).Id;

    internal static void Apply(string? id)
    {
        var theme = Resolve(id);
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        SetBrush(resources, "Bg", theme.Background);
        SetBrush(resources, "Surface", theme.Surface);
        SetBrush(resources, "Surface2", theme.Surface2);
        SetBrush(resources, "Raised", theme.Raised);
        SetBrush(resources, "Hover", theme.Hover);
        SetBrush(resources, "Border", theme.Border);
        SetBrush(resources, "BorderStrong", theme.BorderStrong);
        SetBrush(resources, "BorderEmphasis", theme.BorderEmphasis);
        SetBrush(resources, "Text", theme.Text);
        SetBrush(resources, "TextSecondary", theme.TextSecondary);
        SetBrush(resources, "Muted", theme.Muted);
        SetBrush(resources, "MutedStrong", theme.MutedStrong);
        SetBrush(resources, "Accent", theme.Accent);
        SetBrush(resources, "AccentSoft", theme.AccentSoft);
        SetBrush(resources, "Success", theme.Success);
        SetBrush(resources, "Warning", theme.Warning);
        SetBrush(resources, "Danger", theme.Danger);
        SetBrush(resources, "Teal", theme.Teal);
        SetBrush(resources, "Purple", theme.Purple);
        SetBrush(resources, "SurfaceDeep", Blend(theme.Background, theme.Surface, 0.42));
        SetBrush(resources, "SurfaceInset", Blend(theme.Background, theme.Surface, 0.68));
        SetBrush(resources, "SurfaceTint", Blend(theme.Surface2, theme.Accent, 0.10));
        SetBrush(resources, "DangerSurface", Blend(theme.Surface2, theme.Danger, 0.25));
        SetBrush(resources, "DangerDeep", Blend(theme.Background, theme.Danger, 0.14));
        SetBrush(resources, "MenuSurface", WithAlpha(theme.Surface2, 0xF5));
        SetBrush(resources, "GlassSurface", WithAlpha(theme.Surface, 0xF2));
        SetBrush(resources, "OverlaySurface", WithAlpha(theme.Background, 0xF2));
        SetBrush(resources, "OverlayDeep", WithAlpha(Blend(theme.Background, theme.Surface, 0.18), 0xF2));
        SetBrush(resources, "OverlayOpaque", WithAlpha(theme.Background, 0xFA));
        SetBrush(resources, "RevealSurface", WithAlpha(theme.Raised, 0xDD));
        SetBrush(resources, "SidebarToggleSurface", WithAlpha(theme.Raised, 0xE6));
        SetBrush(resources, "AgentFill", WithAlpha(theme.Surface2, 0x24));
        SetBrush(resources, "BorderGlass", WithAlpha(theme.BorderStrong, 0x70));
        SetBrush(resources, "AccentGlow", WithAlpha(theme.Accent, 0x20));
    }

    internal static TerminalTheme CreateTerminalTheme(TerminalTheme source, string? id)
    {
        var theme = Resolve(id);
        return new TerminalTheme
        {
            DefaultBackground = EasyTerminalControl.ColorToVal(Parse(theme.TerminalBackground)),
            DefaultForeground = EasyTerminalControl.ColorToVal(Parse(theme.TerminalForeground)),
            DefaultSelectionBackground = EasyTerminalControl.ColorToVal(Parse(theme.TerminalSelection)),
            CursorStyle = source.CursorStyle,
            ColorTable = source.ColorTable?.ToArray() ?? []
        };
    }

    internal static bool ContractPassesForTest()
    {
        var ids = Themes.Select(theme => theme.Id).ToArray();
        var black = Resolve(BlackThemeId);
        var terminal = CreateTerminalTheme(new TerminalTheme { ColorTable = new uint[16] }, BlackThemeId);
        return Themes.Count >= 4
            && ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Length
            && Normalize("missing") == DefaultThemeId
            && Parse(black.Background) == Colors.Black
            && terminal.DefaultBackground == EasyTerminalControl.ColorToVal(Colors.Black)
            && terminal.ColorTable.Length == 16;
    }

    internal static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush(Parse(value));
        brush.Freeze();
        return brush;
    }

    private static void SetBrush(ResourceDictionary resources, string key, string value)
    {
        var color = Parse(value);
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
        else
            resources[key] = new SolidColorBrush(color);
    }

    private static string Blend(string background, string foreground, double amount)
    {
        var from = Parse(background);
        var to = Parse(foreground);
        byte Mix(byte first, byte second) => (byte)Math.Round(first + (second - first) * amount);
        return $"#{Mix(from.R, to.R):X2}{Mix(from.G, to.G):X2}{Mix(from.B, to.B):X2}";
    }

    private static string WithAlpha(string value, byte alpha)
    {
        var color = Parse(value);
        return $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
}
