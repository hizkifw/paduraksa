using Bunit;

using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The QR encoder behind the TOTP enrolment (no image service, no package): the smallest version that fits, the structure a scanner looks
/// for, valid format information, and an inline SVG. (A scanner's reading of codes from version 1 to 40 was checked with zbarimg when the
/// encoder was written; set <c>PADURAKSA_QR_DUMP</c> to a directory to write the SVGs again.)
/// </summary>
public sealed class QrCodeTests
{
    public static TheoryData<int, int> Lengths => new()
    {
        { 1, 1 },
        { 14, 1 },
        { 15, 2 },
        { 106, 6 },
        { 107, 7 },
        { 213, 10 },
        { 2331, 40 },
    };

    [Theory]
    [MemberData(nameof(Lengths))]
    public void TheSmallestVersionThatHoldsTheTextIsChosen(int length, int version)
    {
        QrCode code = QrCode.Encode(new string('a', length));

        Assert.Equal((version, (version * 4) + 17), (code.Version, code.Size));
        Dump(code, $"len{length}");
    }

    [Fact]
    public void TooMuchTextIsRefused() =>
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('a', 2332)));

    [Fact]
    public void TheFindersTimingAndFormatInformationAreInPlace()
    {
        QrCode code = QrCode.Encode("otpauth://totp/Paduraksa:owner?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Paduraksa&algorithm=SHA1&digits=6&period=30");

        foreach ((int x, int y) in new[] { (0, 0), (code.Size - 7, 0), (0, code.Size - 7) })
        {
            // The finder: a dark 7x7 ring, a light ring, a dark 3x3 centre.
            Assert.True(code[x, y] && code[x + 6, y + 6] && code[x + 3, y + 3]);
            Assert.False(code[x + 1, y + 1] || code[x + 5, y + 1]);
        }

        for (int i = 8; i < code.Size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, code[i, 6]);
            Assert.Equal(i % 2 == 0, code[6, i]);
        }

        // The format information next to the top-left finder decodes to level M and the chosen mask.
        int bits = 0;
        int[] rows = [0, 1, 2, 3, 4, 5, 7, 8];
        for (int i = 0; i < 8; i++)
        {
            bits |= (code[8, rows[i]] ? 1 : 0) << i;
        }

        for (int i = 9; i < 15; i++)
        {
            bits |= (code[14 - i, 8] ? 1 : 0) << i;
        }

        bits |= (code[7, 8] ? 1 : 0) << 8;
        int format = (bits ^ 0x5412) >> 10;
        Assert.Equal((0, code.Mask), (format >> 3, format & 7));
        Assert.True(code[8, code.Size - 8], "the dark module");
        Dump(code, "otpauth");
    }

    [Fact]
    public void TheImageIsInlineSvgWithAQuietZone()
    {
        using DashboardTestContext context = new();
        IRenderedComponent<QrCodeImage> image = context.Render<QrCodeImage>(parameters => parameters
            .Add(qr => qr.Text, "hello")
            .Add(qr => qr.Label, "Scan me"));

        var svg = image.Find("svg");
        Assert.Equal("0 0 29 29", svg.GetAttribute("viewBox"));
        Assert.Equal("Scan me", svg.GetAttribute("aria-label"));
        Assert.StartsWith("M4,4h7v1h-7z", image.Find("path").GetAttribute("d"), StringComparison.Ordinal);
        Assert.Empty(image.FindAll("img"));
    }

    [Fact]
    public void EveryVersionCanBeDumpedForAScanner()
    {
        int last = 0;
        for (int length = 1; length <= 2331; length += 7)
        {
            QrCode code = QrCode.Encode(string.Concat(Enumerable.Repeat("Paduraksa-", (length / 10) + 1))[..length]);
            if (code.Version != last)
            {
                last = code.Version;
                Dump(code, $"v{code.Version:D2}-len{length}");
            }
        }

        Assert.Equal(40, last);
    }

    private static void Dump(QrCode code, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("PADURAKSA_QR_DUMP");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        int extent = code.Size + 8;
        File.WriteAllText(
            Path.Combine(directory, $"{name}.svg"),
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {extent} {extent}\" width=\"{extent * 8}\" height=\"{extent * 8}\" shape-rendering=\"crispEdges\"><rect width=\"{extent}\" height=\"{extent}\" fill=\"#fff\"/><path d=\"{code.ToSvgPath()}\"/></svg>");
    }
}
