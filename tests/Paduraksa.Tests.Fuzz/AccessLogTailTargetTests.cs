using Paduraksa.Fuzz;

namespace Paduraksa.Tests.Fuzz;

/// <summary>The access-log-tail target's rule that no value of the query reaches an output with the query string off: what it flags and what it leaves alone.</summary>
public sealed class AccessLogTailTargetTests
{
    private const string Value = "765sssss3ss8";

    [Fact]
    public void AValueAnEscapeSequenceAndItsNeighbourSpellIsNotALeak()
    {
        // The upstream field is the control character U+0007 then "65sssss3ss8": the escaper writes it as the four characters \x07, whose "7" joins the text after it into the query's value.
        const string Upstream = "\u000765sssss3ss8";
        string[] outputs = ["2026-01-01T00:00:00.000Z - - \"GET / HTTP/1.1\" 200 - 0.000 - \"\\x0765sssss3ss8\" - - - -", "\\x0765sssss3ss8"];

        Assert.Contains(Value, outputs[1], StringComparison.Ordinal);
        Assert.Null(AccessLogTailTarget.LeakedQueryValue("?=" + Value, [null, "GET", "/", "HTTP/1.1", Upstream, null, null, null, null], outputs));
    }

    [Fact]
    public void AValueAnotherFieldCarriesIsNotALeak()
    {
        Assert.Null(AccessLogTailTarget.LeakedQueryValue("?x=" + Value, [null, "GET", "/" + Value, null, null, null, null, null, null], ["\"GET /" + Value + " -\""]));
    }

    [Fact]
    public void AValueInAnOutputThatNoOtherFieldCarriesIsALeak()
    {
        // A formatter that kept the query in the target, or put it into the path, writes the value where no field of the entry has it.
        Assert.Equal(Value, AccessLogTailTarget.LeakedQueryValue("?x=" + Value, [null, "GET", "/a", null, "HTTP/1.1", null, null, null, null], ["\"GET /a?x=" + Value + " HTTP/1.1\""]));
        Assert.Equal(Value, AccessLogTailTarget.LeakedQueryValue("?x=" + Value, [null, "GET", "/a\u0007", null, null, null, null, null, null], ["\"GET /a\\x07\" " + Value]));
    }

    [Fact]
    public void AValueThatReachedNoOutputIsNotALeak()
    {
        Assert.Null(AccessLogTailTarget.LeakedQueryValue("?x=" + Value, [null, "GET", "/a", null, null, null, null, null, null], ["\"GET /a -\""]));
    }
}
