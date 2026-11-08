namespace Paduraksa.Dashboard.Theme;

/// <summary>Which palette a person asked for: none yet (follow the operating system's light or dark setting), or a fixed one.</summary>
public enum ThemePreference
{
    /// <summary>Follow the system; dark when it is not known.</summary>
    System = 0,

    Dark = 1,

    Light = 2,
}

/// <summary>
/// The palette of one page or circuit. Dark is the default; with no choice of their own a person gets their system's setting. The choice
/// is kept in a cookie (<see cref="PreferenceCookie"/>, written by the toggle through <c>paduraksa.js</c>), and the script also records the
/// system's setting (<see cref="SystemSchemeCookie"/>), so the server renders the right palette from the first byte on the next load.
/// </summary>
public sealed class ThemeState
{
    /// <summary>The person's choice: <c>dark</c> or <c>light</c>; absent means "follow the system". Not a secret, readable by the page's script.</summary>
    public const string PreferenceCookie = "paduraksa-theme";

    /// <summary>The system's setting as the page's script last saw it: <c>dark</c> or <c>light</c>.</summary>
    public const string SystemSchemeCookie = "paduraksa-scheme";

    private bool _initialized;

    /// <summary>The person's choice.</summary>
    public ThemePreference Preference { get; private set; }

    /// <summary>Whether the system asks for a light palette (as far as is known).</summary>
    public bool SystemPrefersLight { get; private set; }

    /// <summary>The palette to show.</summary>
    public bool IsDark => Preference switch
    {
        ThemePreference.Dark => true,
        ThemePreference.Light => false,
        _ => !SystemPrefersLight,
    };

    /// <summary>Raised after the palette may have changed.</summary>
    public event Action? Changed;

    /// <summary>The state the request's cookies describe (a no-op once initialised: the circuit keeps its own).</summary>
    public void Initialize(ThemePreference preference, bool systemPrefersLight)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Preference = preference;
        SystemPrefersLight = systemPrefersLight;
    }

    /// <summary>The system's setting, as the browser reports it now.</summary>
    public void SetSystemPreference(bool prefersLight)
    {
        if (SystemPrefersLight == prefersLight)
        {
            return;
        }

        SystemPrefersLight = prefersLight;
        Changed?.Invoke();
    }

    /// <summary>Flips to the other palette and makes it the person's choice; returns the cookie value to store.</summary>
    public string Toggle()
    {
        Preference = IsDark ? ThemePreference.Light : ThemePreference.Dark;
        Changed?.Invoke();
        return ToCookieValue(Preference);
    }

    /// <summary>The preference a cookie value names (anything else is <see cref="ThemePreference.System"/>).</summary>
    public static ThemePreference ParsePreference(string? value) => value switch
    {
        "dark" => ThemePreference.Dark,
        "light" => ThemePreference.Light,
        _ => ThemePreference.System,
    };

    /// <summary>The cookie value for <paramref name="preference"/>.</summary>
    public static string ToCookieValue(ThemePreference preference) => preference switch
    {
        ThemePreference.Dark => "dark",
        ThemePreference.Light => "light",
        _ => "system",
    };
}
