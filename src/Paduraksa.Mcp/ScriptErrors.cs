using System.Text.RegularExpressions;

using Jint;
using Jint.Native;
using Jint.Runtime;

namespace Paduraksa.Mcp;

/// <summary>What a failed script is told: the error's own message with where in the model's code it happened, never anything of this process.</summary>
internal static partial class ScriptErrors
{
    public static string Describe(Exception exception, SandboxLimits limits) => exception switch
    {
        SandboxLimitException limit => limit.Message,
        StatementsCountOverflowException => $"The script ran more than {limits.MaxStatements:N0} statements. Do less per call: filter before you loop.",
        MemoryLimitExceededException => $"The script built an array of more than {limits.MaxArraySize:N0} elements. Build less data per call.",
        RecursionDepthOverflowException => $"The script called functions more than {limits.MaxCallDepth} deep.",
        System.Text.RegularExpressions.RegexMatchTimeoutException => "A regular expression took too long to match; simplify it.",
        JavaScriptException script => FromScript(script),
        PromiseRejectedException rejected => Describe(rejected.RejectedValue),
        ScriptPreparationException { InnerException: Acornima.SyntaxErrorException syntax } => $"SyntaxError: {syntax.Description} (line {syntax.LineNumber}, column {syntax.Column + 1})",
        ScriptPreparationException preparation => "SyntaxError: " + Clip(preparation.Message),
        OutOfMemoryException or InsufficientMemoryException => "The script ran out of memory. Build less data per call.",
        _ => "The sandbox failed on this script. Try a simpler one.",
    };

    /// <summary>A value a script threw or rejected with: an error's name and message and its position, or the value as text.</summary>
    public static string Describe(JsValue value)
    {
        if (value.IsObject() && value.AsObject() is { } error && error.Get("message") is { } message && !message.IsUndefined())
        {
            var name = error.Get("name");
            var text = Clean($"{(name.IsUndefined() ? "Error" : name.ToString())}: {message}");
            return !text.Contains("(line ", StringComparison.Ordinal) && error.Get("stack") is { } stack && stack.IsString() && Position().Match(stack.AsString()) is { Success: true } at
                ? text + $" (line {at.Groups[1].Value}, column {at.Groups[2].Value})"
                : text;
        }

        return "Uncaught " + Clip(Brief(value));
    }

    private static string FromScript(JavaScriptException exception)
    {
        var text = Describe(exception.Error);
        if (text.Contains("(line ", StringComparison.Ordinal))
        {
            return text;
        }

        var start = exception.Location.Start;
        return start.Line > 0 ? $"{text} (line {start.Line}, column {start.Column + 1})" : text;
    }

    /// <summary>The engine writes a syntax error's position into its message as <c>(&lt;anonymous&gt;:1:23)</c>; this puts it where the other errors have it.</summary>
    private static string Clean(string text)
    {
        var at = Suffix().Match(text);
        return at.Success ? text[..at.Index] + $" (line {at.Groups[1].Value}, column {at.Groups[2].Value})" : text;
    }

    /// <summary>A thrown value that is not an error, as the JSON the script would have returned for it.</summary>
    private static string Brief(JsValue value)
    {
        if (value.IsUndefined())
        {
            return "undefined";
        }

        CappedJson json = new(300, 6);
        try
        {
            json.Write(value);
            return json.Text;
        }
        catch (JsonBudgetExceededException)
        {
            return json.Prefix();
        }
    }

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "...";

    [GeneratedRegex(@"(?:<anonymous>|code\.js):(\d+):(\d+)")]
    private static partial Regex Position();

    [GeneratedRegex(@"\s*\((?:<anonymous>|code\.js):(\d+):(\d+)\)\s*$")]
    private static partial Regex Suffix();
}
