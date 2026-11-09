using MudBlazor;

namespace Paduraksa.Dashboard.Theme;

/// <summary>
/// The dashboard's MudBlazor theme: warm charcoal and stone neutrals, monochrome (the primary is the brightest neutral in the dark
/// palette and the darkest in the light one), so colour is left to what it means: success, a warning, an error. A dark palette (the
/// default) and a light one. Type is the platform's own UI font (nothing is downloaded: the admin works offline on
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
            Primary = "#E7E5E0",
            PrimaryContrastText = "#171614",
            Secondary = "#A8A49C",
            SecondaryContrastText = "#171614",
            Tertiary = "#C9C5BD",
            Info = "#7FA7C9",
            Success = "#5DBB8A",
            Warning = "#D9A957",
            Error = "#E5736B",
            Dark = "#0C0B0A",
            Black = "#080807",
            Background = "#111110",
            BackgroundGray = "#151513",
            Surface = "#1A1917",
            AppbarBackground = "#141412EB",
            AppbarText = "#EDECE8",
            DrawerBackground = "#141412",
            DrawerText = "#CFCCC5",
            DrawerIcon = "#9C988F",
            TextPrimary = "#EDECE8",
            TextSecondary = "#9C988F",
            TextDisabled = "#EDECE861",
            ActionDefault = "#9C988F",
            ActionDisabled = "#EDECE84D",
            ActionDisabledBackground = "#EDECE81F",
            LinesDefault = "#2A2926",
            LinesInputs = "#3A3834",
            Divider = "#2A2926",
            DividerLight = "#201F1D",
            TableLines = "#2A2926",
            TableStriped = "#FFFFFF05",
            TableHover = "#FFFFFF0A",
            Skeleton = "#FFFFFF0F",
            OverlayDark = "#080807B3",
            GrayLight = "#322F2C",
            GrayLighter = "#201F1D",
        },
        PaletteLight = new PaletteLight
        {
            Primary = "#26241F",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#6B675F",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#4A4741",
            Info = "#2F6E9E",
            Success = "#23824F",
            Warning = "#A86F12",
            Error = "#C2413A",
            Background = "#F5F4F1",
            BackgroundGray = "#EDECE8",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFFEB",
            AppbarText = "#1C1B18",
            DrawerBackground = "#FAF9F7",
            DrawerText = "#33312C",
            DrawerIcon = "#6B675F",
            TextPrimary = "#1C1B18",
            TextSecondary = "#6B675F",
            ActionDefault = "#6B675F",
            LinesDefault = "#E5E3DE",
            LinesInputs = "#D1CEC7",
            Divider = "#E5E3DE",
            TableLines = "#E5E3DE",
            TableStriped = "#0000000A",
            TableHover = "#26241F0A",
            Skeleton = "#0000000F",
        },
        // Dialogs (a confirmation asked from inside a drawer) above the drawer stack (DrawerHost.BaseZ), tooltips above both.
        ZIndex = new ZIndex { Dialog = 1700, Tooltip = 1800 },
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
