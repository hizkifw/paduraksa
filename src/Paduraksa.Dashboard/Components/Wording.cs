using System.Globalization;

namespace Paduraksa.Dashboard.Components;

/// <summary>Small pieces of wording every page shares.</summary>
public static class Wording
{
    /// <summary>"1 certificate", "3 certificates".</summary>
    public static string Plural(int count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");
}
