using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Model;

/// <summary>The template syntax the validator, the compiler and the dashboard share.</summary>
public sealed class ErrorPageTextTests
{
    [Fact]
    public void LiteralTextAndVariablesAlternate()
    {
        var parts = ErrorPageText.Parse("Error {status} at {host}: {requestId}.", out var unknown);
        Assert.Empty(unknown);
        Assert.Equal(
            [
                new ErrorPageTemplatePart("Error ", null),
                new ErrorPageTemplatePart(null, ErrorPageVariable.Status),
                new ErrorPageTemplatePart(" at ", null),
                new ErrorPageTemplatePart(null, ErrorPageVariable.Host),
                new ErrorPageTemplatePart(": ", null),
                new ErrorPageTemplatePart(null, ErrorPageVariable.RequestId),
                new ErrorPageTemplatePart(".", null),
            ],
            parts);
    }

    [Fact]
    public void EveryVariableHasAName()
    {
        foreach (var variable in Enum.GetValues<ErrorPageVariable>())
        {
            var parts = ErrorPageText.Parse("{" + ErrorPageText.NameOf(variable) + "}", out var unknown);
            Assert.Empty(unknown);
            Assert.Equal([new ErrorPageTemplatePart(null, variable)], parts);
        }

        Assert.Equal(Enum.GetValues<ErrorPageVariable>().Length, ErrorPageText.VariableNames.Count);
    }

    [Theory]
    [InlineData("a{color:red}", "a{color:red}")]
    [InlineData("@media print{body{margin:0}}", "@media print{body{margin:0}}")]
    [InlineData("{}", "{}")]
    [InlineData("{ status }", "{ status }")]
    [InlineData("{1status}", "{1status}")]
    [InlineData("{status", "{status")]
    [InlineData("{{status}", "{status}")]
    [InlineData("}{", "}{")]
    public void ABraceThatIsNotAVariableIsLiteralText(string text, string literal)
    {
        var parts = ErrorPageText.Parse(text, out var unknown);
        Assert.Empty(unknown);
        Assert.Equal([new ErrorPageTemplatePart(literal, null)], parts);
    }

    [Fact]
    public void AnUnknownNameIsReportedOnceAndStaysInTheText()
    {
        var parts = ErrorPageText.Parse("{nope} {status} {nope} {Status}", out var unknown);
        Assert.Equal(["nope", "Status"], unknown);
        Assert.Equal(
            [
                new ErrorPageTemplatePart("{nope} ", null),
                new ErrorPageTemplatePart(null, ErrorPageVariable.Status),
                new ErrorPageTemplatePart(" {nope} {Status}", null),
            ],
            parts);
    }

    [Theory]
    [InlineData("A heading", true)]
    [InlineData("café ✓", true)]
    [InlineData("two\nlines", false)]
    [InlineData("tab\there", false)]
    [InlineData("nul\0", false)]
    [InlineData("c1\u0085", false)]
    public void ALineIsPlainWhenItHasNoControlCharacterAndIsWellFormed(string text, bool plain) =>
        Assert.Equal(plain, ErrorPageText.IsPlainLine(text));

    [Fact]
    public void ALoneSurrogateIsNotWellFormed() =>
        // Built at run time: an attribute argument can't carry one through the compiler.
        Assert.False(ErrorPageText.IsPlainLine("lone" + new string('\ud800', 1)));

    [Fact]
    public void ADocumentMayKeepTabsAndLineBreaksOnly()
    {
        Assert.True(ErrorPageText.IsPlainDocument("<p>\r\n\ta\n</p>"));
        Assert.False(ErrorPageText.IsPlainDocument("<p>\u0001</p>"));
        Assert.False(ErrorPageText.IsPlainDocument("<p>" + new string('\ud800', 1) + "</p>"));
    }
}
