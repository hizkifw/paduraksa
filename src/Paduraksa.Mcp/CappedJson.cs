using System.Globalization;
using System.Text;

using Jint;
using Jint.Native;
using Jint.Native.Error;
using Jint.Native.Object;

namespace Paduraksa.Mcp;

/// <summary>The writer would pass its byte budget: what it holds is a prefix of the document.</summary>
internal sealed class JsonBudgetExceededException : Exception
{
    public JsonBudgetExceededException()
        : base("The JSON is larger than the budget.")
    {
    }
}

/// <summary>
/// A script's value as JSON, written with a byte budget and a depth limit. The engine's own serializer is not used for what a script returns (or for
/// the script's <c>JSON.stringify</c>) because it recurses over the whole structure with no bound: a value built from shared references expands
/// exponentially, a deeply nested one is quadratic in time and ends the process with a stack overflow. This walk stops at the budget, so its work is
/// bounded by the output it may produce.
/// </summary>
internal sealed class CappedJson(int maxBytes, int maxDepth = CappedJson.DefaultDepth)
{
    public const int DefaultDepth = 100;

    private readonly StringBuilder _text = new();
    private readonly List<ObjectInstance> _path = [];
    private int _bytes;

    /// <summary>The JSON written so far.</summary>
    public string Text => _text.ToString();

    /// <summary>Writes <paramref name="value"/> as JSON, or <c>null</c> when it has none (undefined, a function, a symbol). Indentation is two spaces per level when <paramref name="indent"/> is non-empty.</summary>
    public void Write(JsValue value, string indent = "") => WriteValue(value, indent, 0);

    private void WriteValue(JsValue value, string indent, int depth)
    {
        if (value.IsNull() || value.IsUndefined() || value.IsSymbol() || value.IsCallable())
        {
            Append("null");
            return;
        }

        if (value.IsBoolean())
        {
            Append(value.AsBoolean() ? "true" : "false");
            return;
        }

        if (value.IsNumber())
        {
            var number = value.AsNumber();
            Append(double.IsFinite(number) ? number.ToString("R", CultureInfo.InvariantCulture) : "null");
            return;
        }

        if (value.IsString())
        {
            WriteString(value.AsString());
            return;
        }

        if (value.IsBigInt())
        {
            WriteString(value.ToString() + "n");
            return;
        }

        var obj = value.AsObject();
        if (value.IsDate())
        {
            var date = value.AsDate();
            if (double.IsNaN(date.DateValue))
            {
                Append("null");
            }
            else
            {
                WriteString(date.ToDateTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            }

            return;
        }

        if (depth >= maxDepth)
        {
            WriteString("[too deep]");
            return;
        }

        if (_path.Contains(obj))
        {
            WriteString("[circular]");
            return;
        }

        _path.Add(obj);
        try
        {
            if (obj is JsArray array)
            {
                WriteArray(array, indent, depth);
            }
            else
            {
                WriteObject(obj, indent, depth);
            }
        }
        finally
        {
            _path.RemoveAt(_path.Count - 1);
        }
    }

    private void WriteArray(JsArray array, string indent, int depth)
    {
        var length = (uint)Math.Min(array.Length, uint.MaxValue);
        Append("[");
        for (uint i = 0; i < length; i++)
        {
            Append(i == 0 ? string.Empty : ",");
            NewLine(indent, depth + 1);
            WriteValue(array.Get(JsNumber.Create(i)), indent, depth + 1);
        }

        if (length > 0)
        {
            NewLine(indent, depth);
        }

        Append("]");
    }

    private void WriteObject(ObjectInstance obj, string indent, int depth)
    {
        if (obj is ErrorInstance)
        {
            WriteError(obj, indent, depth);
            return;
        }

        Append("{");
        var first = true;
        foreach (var key in obj.GetOwnPropertyKeys(Jint.Runtime.Types.String))
        {
            if (obj.GetOwnProperty(key) is not { Enumerable: true })
            {
                continue;
            }

            var member = obj.Get(key);
            if (member.IsUndefined() || member.IsSymbol() || member.IsCallable())
            {
                continue;
            }

            Append(first ? string.Empty : ",");
            first = false;
            NewLine(indent, depth + 1);
            WriteString(key.ToString());
            Append(indent.Length == 0 ? ":" : ": ");
            WriteValue(member, indent, depth + 1);
        }

        if (!first)
        {
            NewLine(indent, depth);
        }

        Append("}");
    }

    /// <summary>An error is <c>{}</c> to <c>JSON.stringify</c>; what a script returns from a caught error is its name and message.</summary>
    private void WriteError(ObjectInstance error, string indent, int depth)
    {
        Append("{");
        NewLine(indent, depth + 1);
        WriteString("name");
        Append(indent.Length == 0 ? ":" : ": ");
        WriteString(error.Get("name").ToString());
        Append(",");
        NewLine(indent, depth + 1);
        WriteString("message");
        Append(indent.Length == 0 ? ":" : ": ");
        WriteString(error.Get("message").ToString());
        NewLine(indent, depth);
        Append("}");
    }

    private void NewLine(string indent, int depth)
    {
        if (indent.Length == 0)
        {
            return;
        }

        Append("\n");
        for (var i = 0; i < depth; i++)
        {
            Append(indent);
        }
    }

    private void WriteString(string text)
    {
        Append("\"");
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            string? escape = c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\b' => "\\b",
                '\f' => "\\f",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => null,
            };
            if (escape is null)
            {
                continue;
            }

            Append(text.AsSpan(start, i - start));
            Append(escape);
            start = i + 1;
        }

        Append(text.AsSpan(start));
        Append("\"");
    }

    private void Append(string text)
    {
        Charge(text.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(text));
        _text.Append(text);
    }

    private void Append(ReadOnlySpan<char> text)
    {
        Charge(text.IsEmpty ? 0 : Encoding.UTF8.GetByteCount(text));
        _text.Append(text);
    }

    private void Charge(int bytes)
    {
        _bytes += bytes;
        if (_bytes > maxBytes)
        {
            throw new JsonBudgetExceededException();
        }
    }

    /// <summary>The text as far as it was written, cut so that no half of a character or escape is left.</summary>
    public string Prefix()
    {
        var text = _text.ToString();
        return text.Length > 0 && char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
    }
}
