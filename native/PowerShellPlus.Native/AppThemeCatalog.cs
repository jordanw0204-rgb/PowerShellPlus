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
    string TerminalSelection,
    string? GradientEnd = null,
    string GradientDirection = "Diagonal",
    bool IsCustom = false)
{
    public bool IsGradient => !string.IsNullOrWhiteSpace(GradientEnd);
    public string Kind => IsCustom ? (IsGradient ? "CUSTOM GRADIENT" : "CUSTOM") : (IsGradient ? "GRADIENT" : "PRESET");
    public Brush PreviewBackground => AppThemeCatalog.ThemeBrush(Background, GradientEnd, GradientDirection);
    public Brush PreviewSurface => AppThemeCatalog.Brush(Surface2);
    public Brush PreviewAccent => AppThemeCatalog.Brush(Accent);
    public Brush PreviewText => AppThemeCatalog.Brush(Text);
}

internal static class AppThemeCatalog
{
    internal const string DefaultThemeId = "mocha";
    internal const string BlackThemeId = "obsidian";
    private static List<CustomAppThemeState> customThemes = [];

    internal static IReadOnlyList<AppThemeDefinition> BuiltInThemes { get; } =
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
            "#F47791", "#62D9CD", "#C3A7FF", "#0D0E11", "#F0F2F7", "#373D4B"),
        new(
            "aurora", "Aurora", "Ocean blue fading into a quiet violet horizon",
            "#07111F", "#10182A", "#17233A", "#1D2B45", "#233653", "#29415F", "#3B587D", "#53739B",
            "#EAF3FF", "#BDD0E8", "#7F94B0", "#687D9A", "#63D6E6", "#A8EEF3", "#75E6AC", "#FFD278",
            "#FF7897", "#63D6E6", "#BEA7FF", "#0C1830", "#EAF3FF", "#29415F", "#26133F", "Diagonal"),
        new(
            "nebula", "Nebula", "A deep plum gradient with rose-gold energy",
            "#10091D", "#1B102A", "#28163B", "#322049", "#402958", "#4B3264", "#65457E", "#80609A",
            "#F8EEFF", "#D6C1E3", "#967DAA", "#806B93", "#F29AC3", "#FFC1DB", "#85DFB0", "#FFD083",
            "#FF799C", "#6FE2D2", "#C7A5FF", "#160D27", "#F8EEFF", "#503865", "#32122E", "Horizontal"),
        new(
            "ember", "Ember", "Near-black charcoal warmed by a subtle ember glow",
            "#09090B", "#151113", "#21181A", "#2B2021", "#37292A", "#443334", "#5D4545", "#775B59",
            "#FFF3EE", "#DBC7C0", "#99837D", "#816D68", "#FF9B72", "#FFC4A9", "#83DDA4", "#FFD27C",
            "#FF7A91", "#65DCCD", "#C3A7FF", "#100C0D", "#FFF3EE", "#503A39", "#301611", "Vertical")
    ];

    internal static IReadOnlyList<AppThemeDefinition> Themes =>
        [.. BuiltInThemes, .. customThemes.Select(CreateCustomTheme)];
    internal static IReadOnlyList<CustomAppThemeState> CustomThemeStates => customThemes.Select(value => value.Copy()).ToArray();

    internal static void ConfigureCustomThemes(IEnumerable<CustomAppThemeState>? values)
    {
        var builtInIds = BuiltInThemes.Select(theme => theme.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        customThemes = (values ?? [])
            .Where(value => value is not null)
            .Select(NormalizeCustomTheme)
            .Where(value => !builtInIds.Contains(value.Id))
            .GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(32)
            .ToList();
    }

    internal static AppThemeDefinition Resolve(string? id) => Themes.FirstOrDefault(theme =>
        string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? BuiltInThemes[0];

    internal static string Normalize(string? id) => Resolve(id).Id;

    internal static CustomAppThemeState NormalizeCustomTheme(CustomAppThemeState value)
    {
        var id = value.Id?.Trim();
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("custom-", StringComparison.OrdinalIgnoreCase))
            id = $"custom-{Guid.NewGuid():N}";
        return new CustomAppThemeState
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(value.Name) ? "My theme" : value.Name.Trim()[..Math.Min(value.Name.Trim().Length, 40)],
            Background = NormalizeColor(value.Background, "#11111B"),
            Surface = NormalizeColor(value.Surface, "#1E1E2E"),
            Accent = NormalizeColor(value.Accent, "#89B4FA"),
            Text = NormalizeColor(value.Text, "#CDD6F4"),
            UseGradient = value.UseGradient,
            GradientEnd = NormalizeColor(value.GradientEnd, "#242438"),
            GradientDirection = NormalizeDirection(value.GradientDirection)
        };
    }

    internal static AppThemeDefinition CreateCustomTheme(CustomAppThemeState raw)
    {
        var value = NormalizeCustomTheme(raw);
        var terminalBackground = value.UseGradient ? Blend(value.Background, value.GradientEnd, 0.32) : value.Background;
        return new AppThemeDefinition(
            value.Id, value.Name, value.UseGradient ? "Your custom gradient theme" : "Your custom color theme",
            value.Background, value.Surface, Blend(value.Surface, value.Text, 0.05), Blend(value.Surface, value.Text, 0.09),
            Blend(value.Surface, value.Text, 0.13), Blend(value.Surface, value.Text, 0.17), Blend(value.Surface, value.Text, 0.24),
            Blend(value.Surface, value.Text, 0.32), value.Text, Blend(value.Text, value.Background, 0.22),
            Blend(value.Text, value.Background, 0.48), Blend(value.Text, value.Background, 0.38), value.Accent,
            Blend(value.Accent, value.Text, 0.42), "#79D99A", "#F1CB78", "#F27691", "#65D8CC", "#BCA2F4",
            terminalBackground, value.Text, Blend(value.Surface, value.Accent, 0.32),
            value.UseGradient ? value.GradientEnd : null, value.GradientDirection, true);
    }

    internal static void Apply(string? id) => Apply(Resolve(id));

    internal static void Apply(AppThemeDefinition theme)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        resources["AppBackdrop"] = ThemeBrush(theme.Background, theme.GradientEnd, theme.GradientDirection);
        SetBrush(resources, "Bg", theme.Background);
        if (theme.IsGradient)
        {
            resources["Surface"] = ThemeBrush(theme.Surface, Blend(theme.Surface, theme.GradientEnd!, 0.30), theme.GradientDirection);
            resources["Surface2"] = ThemeBrush(theme.Surface2, Blend(theme.Surface2, theme.GradientEnd!, 0.22), theme.GradientDirection);
        }
        else
        {
            SetBrush(resources, "Surface", theme.Surface);
            SetBrush(resources, "Surface2", theme.Surface2);
        }
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
        var original = customThemes.Select(value => value.Copy()).ToList();
        try
        {
            var custom = new CustomAppThemeState
            {
                Id = "custom-contract", Name = "Contract", Background = "#010203", Surface = "#101820",
                Accent = "#22CCAA", Text = "#F0F2F4", UseGradient = true, GradientEnd = "#241040", GradientDirection = "Horizontal"
            };
            ConfigureCustomThemes([custom]);
            var ids = Themes.Select(theme => theme.Id).ToArray();
            var black = Resolve(BlackThemeId);
            var resolvedCustom = Resolve(custom.Id);
            var terminal = CreateTerminalTheme(new TerminalTheme { ColorTable = new uint[16] }, custom.Id);
            return BuiltInThemes.Count >= 7
                && BuiltInThemes.Count(theme => theme.IsGradient) >= 3
                && ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Length
                && Normalize("missing") == DefaultThemeId
                && Parse(black.Background) == Colors.Black
                && resolvedCustom.IsCustom && resolvedCustom.IsGradient
                && ThemeBrush(resolvedCustom.Background, resolvedCustom.GradientEnd, resolvedCustom.GradientDirection) is LinearGradientBrush
                && terminal.DefaultBackground == EasyTerminalControl.ColorToVal(Parse(resolvedCustom.TerminalBackground))
                && terminal.ColorTable.Length == 16;
        }
        finally { ConfigureCustomThemes(original); }
    }

    internal static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush(Parse(value));
        brush.Freeze();
        return brush;
    }

    internal static Brush ThemeBrush(string start, string? end, string direction)
    {
        if (string.IsNullOrWhiteSpace(end)) return Brush(start);
        var (startPoint, endPoint) = GradientPoints(direction);
        var brush = new LinearGradientBrush(Parse(start), Parse(end), 0) { StartPoint = startPoint, EndPoint = endPoint };
        brush.Freeze();
        return brush;
    }

    internal static string NormalizeColor(string? value, string fallback)
    {
        var candidate = value?.Trim().ToUpperInvariant();
        return candidate is { Length: 7 } && candidate[0] == '#' && candidate.Skip(1).All(Uri.IsHexDigit)
            ? candidate
            : fallback;
    }

    internal static string NormalizeDirection(string? value) => value switch
    {
        "Horizontal" => "Horizontal",
        "Vertical" => "Vertical",
        "Reverse diagonal" => "Reverse diagonal",
        _ => "Diagonal"
    };

    private static (Point Start, Point End) GradientPoints(string direction) => NormalizeDirection(direction) switch
    {
        "Horizontal" => (new Point(0, 0.5), new Point(1, 0.5)),
        "Vertical" => (new Point(0.5, 0), new Point(0.5, 1)),
        "Reverse diagonal" => (new Point(1, 0), new Point(0, 1)),
        _ => (new Point(0, 0), new Point(1, 1))
    };

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
