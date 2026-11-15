using System.Reflection;

namespace Paduraksa.Dashboard.AboutArea;

/// <summary>Paduraksa's own license: the <c>LICENSE</c> and <c>NOTICE</c> files of the repository, embedded in the dashboard assembly at build time.</summary>
public sealed class ProductLicense
{
    public const string LicenseResource = "Paduraksa.Dashboard.About.LICENSE";

    public const string NoticeResource = "Paduraksa.Dashboard.About.NOTICE";

    /// <summary>The SPDX ID of the license in <see cref="LicenseText"/>.</summary>
    public const string SpdxId = "Apache-2.0";

    public ProductLicense(string licenseText, string noticeText)
    {
        LicenseText = licenseText.Trim('\n');
        NoticeText = noticeText.Trim('\n');
        // NOTICE is the product's name, then its copyright line: that line is the one place the holder is written.
        Copyright = NoticeText.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("Copyright", StringComparison.Ordinal)) ?? string.Empty;
    }

    public string LicenseText { get; }

    public string NoticeText { get; }

    /// <summary>The copyright line of <see cref="NoticeText"/>, for example <c>Copyright 2026 Hizkia Felix</c>.</summary>
    public string Copyright { get; }

    /// <summary>The year and holder of <see cref="Copyright"/> without its first word, for a line that already says "(c)": <c>2026 Hizkia Felix</c>.</summary>
    public string Holder => Copyright.StartsWith("Copyright ", StringComparison.Ordinal) ? Copyright["Copyright ".Length..] : Copyright;

    public static ProductLicense Embedded { get; } = FromAssembly(typeof(ProductLicense).Assembly);

    public static ProductLicense FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return new ProductLicense(Read(assembly, LicenseResource), Read(assembly, NoticeResource));
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The assembly has no {name}; the Dashboard project embeds it from the repository root.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
