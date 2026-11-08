using MudBlazor;

namespace Paduraksa.Dashboard.Theme;

/// <summary>
/// The dashboard's MudBlazor theme: quiet slate neutrals with one accent, a jade teal (the colour of weathered bronze on a temple gate),
/// in a dark palette (the default) and a light one. Type is the platform's own UI font (nothing is downloaded: the admin works offline on
/// a LAN) on a small, regular scale; corners are softly rounded. The sign-in pages, which are plain HTML, mirror the same colours in
/// <c>app.css</c>.
/// </summary>
public static class PaduraksaTheme
{
    private static readonly string[] SansSerif =
        ["Inter", "\"Segoe UI Variable Text\"", "\"Segoe UI\"", "system-ui", "-apple-system", "BlinkMacSystemFont", "Roboto", "\"Helvetica Neue\"", "Arial", "sans-serif"];

    /// <summary>The monospace stack, for IDs, fingerprints and code (<c>.pdk-mono</c> in <c>app.css</c> uses the same).</summary>
    public const string Monospace = "\"JetBrains Mono\", \"Cascadia Code\", ui-monospace, SFMono-Regular, Menlo, Consolas, monospace";

    /// <summary>The one theme instance (MudBlazor reads it on every render; it is never changed).</summary>
    public static MudTheme Theme { get; } = Create();

    private static MudTheme Create() => new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#3FBFAE",
            PrimaryContrastText = "#04211D",
            Secondary = "#D4A25A",
            SecondaryContrastText = "#21170A",
            Tertiary = "#8C96F5",
            Info = "#5AA9E6",
            Success = "#4CC38A",
            Warning = "#E8B04B",
            Error = "#EF6B67",
            Dark = "#0B0F13",
            Black = "#06090C",
            Background = "#0E1216",
            BackgroundGray = "#12171C",
            Surface = "#151A20",
            AppbarBackground = "#11161BEB",
            AppbarText = "#E6EAEE",
            DrawerBackground = "#11161B",
            DrawerText = "#C5CDD5",
            DrawerIcon = "#97A3AF",
            TextPrimary = "#E6EAEE",
            TextSecondary = "#97A3AF",
            TextDisabled = "#E6EAEE61",
            ActionDefault = "#97A3AF",
            ActionDisabled = "#E6EAEE4D",
            ActionDisabledBackground = "#E6EAEE1F",
            LinesDefault = "#242B33",
            LinesInputs = "#323B45",
            Divider = "#242B33",
            DividerLight = "#1B2128",
            TableLines = "#242B33",
            TableStriped = "#FFFFFF05",
            TableHover = "#FFFFFF0A",
            Skeleton = "#FFFFFF0F",
            OverlayDark = "#06090CB3",
            GrayLight = "#2A323B",
            GrayLighter = "#1B2128",
        },
        PaletteLight = new PaletteLight
        {
            Primary = "#0E7D6F",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#9A6B2A",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#4F5BD5",
            Info = "#1F78C1",
            Success = "#1E8A55",
            Warning = "#B7791F",
            Error = "#C9403C",
            Background = "#F4F6F8",
            BackgroundGray = "#ECEFF2",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFFEB",
            AppbarText = "#18212B",
            DrawerBackground = "#FAFBFC",
            DrawerText = "#2B3540",
            DrawerIcon = "#5B6773",
            TextPrimary = "#18212B",
            TextSecondary = "#5B6773",
            ActionDefault = "#5B6773",
            LinesDefault = "#E2E7EC",
            LinesInputs = "#CBD3DB",
            Divider = "#E2E7EC",
            TableLines = "#E2E7EC",
            TableStriped = "#0000000A",
            TableHover = "#0E7D6F0D",
            Skeleton = "#0000000F",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            DrawerWidthLeft = "252px",
            AppbarHeight = "60px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = SansSerif, FontSize = "0.875rem", LineHeight = "1.5", LetterSpacing = "normal" },
            H1 = new H1Typography { FontFamily = SansSerif, FontSize = "2.5rem", FontWeight = "650", LineHeight = "1.2", LetterSpacing = "-0.02em" },
            H2 = new H2Typography { FontFamily = SansSerif, FontSize = "2rem", FontWeight = "650", LineHeight = "1.25", LetterSpacing = "-0.015em" },
            H3 = new H3Typography { FontFamily = SansSerif, FontSize = "1.625rem", FontWeight = "650", LineHeight = "1.3", LetterSpacing = "-0.01em" },
            H4 = new H4Typography { FontFamily = SansSerif, FontSize = "1.375rem", FontWeight = "650", LineHeight = "1.35", LetterSpacing = "-0.01em" },
            H5 = new H5Typography { FontFamily = SansSerif, FontSize = "1.125rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "normal" },
            H6 = new H6Typography { FontFamily = SansSerif, FontSize = "1rem", FontWeight = "600", LineHeight = "1.45", LetterSpacing = "normal" },
            Subtitle1 = new Subtitle1Typography { FontFamily = SansSerif, FontSize = "0.9375rem", FontWeight = "500", LineHeight = "1.5", LetterSpacing = "normal" },
            Subtitle2 = new Subtitle2Typography { FontFamily = SansSerif, FontSize = "0.8125rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "normal" },
            Body1 = new Body1Typography { FontFamily = SansSerif, FontSize = "0.9375rem", FontWeight = "400", LineHeight = "1.55", LetterSpacing = "normal" },
            Body2 = new Body2Typography { FontFamily = SansSerif, FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "normal" },
            Button = new ButtonTypography { FontFamily = SansSerif, FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.75", LetterSpacing = "0.01em", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = SansSerif, FontSize = "0.75rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "0.01em" },
            Overline = new OverlineTypography { FontFamily = SansSerif, FontSize = "0.6875rem", FontWeight = "600", LineHeight = "2", LetterSpacing = "0.08em", TextTransform = "uppercase" },
        },
    };
}
