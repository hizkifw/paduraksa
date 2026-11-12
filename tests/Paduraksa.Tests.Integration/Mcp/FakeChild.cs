using System.Text;

using Paduraksa.Mcp;

namespace Paduraksa.Tests.Integration.Mcp;

/// <summary>A shell script standing in for the child, so that a test can play the part of a hostile or broken one frame by frame.</summary>
internal static class FakeChild
{
    /// <summary>The run id the pool hands out when a test sets <c>Pool.RunIds</c> to <see cref="FixedRunId"/>: a fake child cannot read the run frame, so it is told.</summary>
    public const string FixedRunId = "FIXEDRID";

    public const string ReadyJson = """{"t":"ready","pid":1,"hardening":[]}""";

    /// <summary><c>printf</c> of the frame: a length prefix and the JSON, as octal escapes.</summary>
    public static string Frame(string json) =>
        "printf '" + string.Concat(BitConverter.GetBytes(Encoding.UTF8.GetByteCount(json)).Reverse().Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'))) + json + "'";

    public static string Result(string runId, string value = "1") => Frame($$"""{"t":"result","rid":"{{runId}}","ok":true,"value":"{{value}}","truncated":false}""");

    /// <summary>A child that says it is ready and then runs <paramref name="afterReady"/>.</summary>
    public static SandboxChildCommand After(string afterReady) => new("/bin/sh", ["-c", $"{Frame(ReadyJson)}; {afterReady}"]);

    public static SandboxChildCommand Raw(string script) => new("/bin/sh", ["-c", script]);

    public static CodeModeTools Tools(string afterReady, SandboxLimits? limits = null, TimeSpan? idle = null, TimeProvider? time = null, Action<string>? log = null)
    {
        var tools = SandboxFixture.Create(limits, idle, time, log, After(afterReady));
        tools.Pool.RunIds = () => FixedRunId;
        return tools;
    }
}
