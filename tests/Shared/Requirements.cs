namespace Paduraksa.Tests.Shared;

/// <summary>
/// What CI insists on having: a test that needs a browser, QUIC or Docker skips when it is missing, and with <c>PADURAKSA_REQUIRE</c> naming it the
/// missing piece fails the test instead, so a runner that lost its Chromium shows red rather than a green run that tested nothing.
/// </summary>
internal static class Requirements
{
    /// <summary>A comma-separated list of the capabilities that must be present (<see cref="Playwright"/>, <see cref="Quic"/>, <see cref="Pebble"/>).</summary>
    public const string Variable = "PADURAKSA_REQUIRE";

    public const string Playwright = "playwright";

    public const string Quic = "quic";

    public const string Pebble = "pebble";

    public static bool IsRequired(string capability) =>
        IsRequired(Environment.GetEnvironmentVariable(Variable), capability);

    internal static bool IsRequired(string? setting, string capability) =>
        setting is not null
        && setting.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(capability, StringComparer.OrdinalIgnoreCase);

    /// <summary>Ends the test: failed when <paramref name="capability"/> is required, skipped otherwise.</summary>
    public static void SkipOrFail(string capability, string reason)
    {
        if (IsRequired(capability))
        {
            Assert.Fail($"{Variable} names {capability}, but it is not available: {reason}");
        }

        Assert.Skip(reason);
    }

    /// <summary>Skips (or fails, when required) unless <paramref name="unavailableReason"/> is null.</summary>
    public static void RequireAvailable(string capability, string? unavailableReason)
    {
        if (unavailableReason is not null)
        {
            SkipOrFail(capability, unavailableReason);
        }
    }
}
