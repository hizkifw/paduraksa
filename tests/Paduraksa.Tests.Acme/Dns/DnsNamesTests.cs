using Paduraksa.Acme.Dns;

namespace Paduraksa.Tests.Acme.Dns;

/// <summary>Record names, zone candidates and TXT quoting.</summary>
public sealed class DnsNamesTests
{
    [Fact]
    public void ZoneCandidatesGoFromTheNameDownToTwoLabels() =>
        Assert.Equal(["_acme-challenge.a.example.com", "a.example.com", "example.com"], DnsNames.ZoneCandidates("_acme-challenge.a.example.com"));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("a..b")]
    [InlineData("a b.example.com")]
    [InlineData("*.example.com")]
    public void ANameThatIsNotPlainIsRefused(string name) => Assert.Throws<ArgumentException>(() => DnsNames.Normalize(name));

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("\"abc\"", "abc")]
    [InlineData("\"a\" \"b\"", "ab")]
    [InlineData("\"a\\\"b\\\\c\"", "a\"b\\c")]
    [InlineData("\"unterminated", "\"unterminated")]
    [InlineData("\"a\" junk", "\"a\" junk")]
    public void UnquoteJoinsQuotedStringsAndLeavesOtherShapesAlone(string content, string expected) =>
        Assert.Equal(expected, DnsNames.UnquoteTxt(content));

    [Theory]
    [InlineData("plain")]
    [InlineData("with \"quote\" and \\slash")]
    public void QuoteAndUnquoteRoundTrip(string value) => Assert.Equal(value, DnsNames.UnquoteTxt(DnsNames.QuoteTxt(value)));
}
