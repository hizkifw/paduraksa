using Jint;
using Jint.Native;
using Jint.Native.Error;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

namespace Paduraksa.Mcp;

/// <summary>
/// What the sandbox takes away from the engine's standard library, and the natives it replaces by bounded ones. The engine's own limits stop a script
/// between statements; a native that runs for a long time, allocates a gigabyte or recurses over the script's data in one step is not stopped by
/// them, and a stack overflow or an out-of-memory ends the whole server process. So: no binary buffers, no <c>Proxy</c>, no <c>Intl</c>, and the natives
/// that build a string from their arguments or walk nested data (<c>join</c>, <c>toString</c>, <c>repeat</c>, <c>padStart</c>, <c>replace</c>, <c>JSON.stringify</c>)
/// are replaced by versions that refuse what they cannot bound. The fuzz target <c>mcp-sandbox</c> looks for the ones this list misses.
/// </summary>
internal sealed class SandboxGuards
{
    private const int MaxNesting = 32;

    private static readonly string[] RemovedGlobals =
    [
        "ArrayBuffer", "SharedArrayBuffer", "DataView", "Atomics", "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array", "Uint16Array", "Int32Array",
        "Uint32Array", "Float16Array", "Float32Array", "Float64Array", "BigInt64Array", "BigUint64Array", "Intl", "Temporal", "WebAssembly", "Proxy",
        "WeakRef", "FinalizationRegistry", "escape", "unescape", "BigInt",
    ];

    private readonly Engine _engine;
    private readonly SandboxLimits _limits;
    private readonly SandboxBudget _budget;
    private readonly ErrorConstructor _rangeError;
    private readonly ErrorConstructor _typeError;
    private int _depth;

    private SandboxGuards(Engine engine, SandboxLimits limits, SandboxBudget budget)
    {
        _engine = engine;
        _limits = limits;
        _budget = budget;
        _rangeError = (ErrorConstructor)engine.Evaluate("RangeError").AsObject();
        _typeError = (ErrorConstructor)engine.Evaluate("TypeError").AsObject();
    }

    public static void Install(Engine engine, SandboxLimits limits, SandboxBudget budget)
    {
        SandboxGuards guards = new(engine, limits, budget);
        var global = engine.Global;
        foreach (var name in RemovedGlobals)
        {
            global.Delete(name);
        }

        var array = engine.Evaluate("Array.prototype").AsObject();
        Replace(array, "join", guards.Join);
        Replace(array, "toString", (thisObject, _) => guards.Join(thisObject, []));
        array.Delete("toLocaleString");

        var text = engine.Evaluate("String.prototype").AsObject();
        guards.Coerce(text, "repeat", guards.Repeat);
        guards.Coerce(text, "padStart", guards.Pad);
        guards.Coerce(text, "padEnd", guards.Pad);
        guards.Coerce(text, "replace", guards.Replace);
        guards.Coerce(text, "replaceAll", guards.Replace);
        guards.Coerce(text, "concat", guards.Concat);

        // The engine's own replace calls this for a regular expression, and a script can call it directly.
        var symbolReplace = engine.Evaluate("Symbol.replace");
        var regExp = engine.Evaluate("RegExp.prototype").AsObject();
        var regExpReplace = regExp.Get(symbolReplace);
        regExp.FastSetProperty(symbolReplace, new PropertyDescriptor(
            new ClrFunction(engine, "[Symbol.replace]", (thisObject, args) =>
            {
                if (args.Length == 0)
                {
                    return engine.Call(regExpReplace, thisObject, args);
                }

                var input = TypeConverter.ToString(args[0]);
                var judged = guards.Replace(input, [JsValue.Undefined, .. args.Skip(1)]);
                return engine.Call(regExpReplace, thisObject, [input, .. judged.Skip(1)]);
            }),
            PropertyFlag.Configurable | PropertyFlag.Writable));

        Replace(engine.Evaluate("JSON").AsObject(), "stringify", guards.Stringify);
    }

    private static void Replace(ObjectInstance target, string name, Func<JsValue, JsValue[], JsValue> body) =>
        target.FastSetProperty(name, new PropertyDescriptor(new ClrFunction(target.Engine, name, body), PropertyFlag.Configurable | PropertyFlag.Writable));

    /// <summary>
    /// Replaces <paramref name="name"/> by a function that coerces the receiver and the arguments itself (<c>ToString</c>, <c>ToIntegerOrInfinity</c>), judges
    /// the coerced values, and hands those same values to the engine's own function: judging what the engine would then coerce again would let an object or a
    /// numeric string past the check, and would run a script's <c>toString</c> twice.
    /// </summary>
    private void Coerce(ObjectInstance target, string name, Func<string, JsValue[], JsValue[]> judge)
    {
        var original = target.Get(name);
        Replace(target, name, (thisObject, args) =>
        {
            if (thisObject.IsNull() || thisObject.IsUndefined())
            {
                return _engine.Call(original, thisObject, args);
            }

            var self = TypeConverter.ToString(thisObject);
            return _engine.Call(original, self, judge(self, args));
        });
    }

    private JsValue[] Concat(string self, JsValue[] args)
    {
        long total = self.Length;
        JsValue[] coerced = new JsValue[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            var piece = TypeConverter.ToString(args[i]);
            total += piece.Length;
            if (total > _limits.MaxStringChars)
            {
                throw TooLong();
            }

            coerced[i] = piece;
        }

        return coerced;
    }

    private JsValue[] Repeat(string self, JsValue[] args)
    {
        var count = Integer(args, 0);
        if (count is > 0 and < double.PositiveInfinity && self.Length * count > _limits.MaxStringChars)
        {
            throw TooLong();
        }

        return [count];
    }

    private JsValue[] Pad(string self, JsValue[] args)
    {
        var length = Integer(args, 0);
        if (length > _limits.MaxStringChars)
        {
            throw TooLong();
        }

        return [length, .. args.Skip(1)];
    }

    /// <summary>
    /// <c>$'</c> and <c>$`</c> in a replacement expand to the text after or before each match, so a replacement of two characters can build a string as long as
    /// the input squared in one native call. A model has no use for them; <c>$&amp;</c>, <c>$1</c> and <c>$&lt;name&gt;</c> only ever partition the input and stay.
    /// A replacement function's results are summed as they come, because the engine appends them in native code that the memory limit does not interrupt.
    /// </summary>
    private JsValue[] Replace(string self, JsValue[] args)
    {
        if (args.Length < 2)
        {
            return args;
        }

        if (args[1].IsCallable())
        {
            return [args[0], Counting(args[1]), .. args.Skip(2)];
        }

        var replacement = TypeConverter.ToString(args[1]);
        for (var i = 0; i + 1 < replacement.Length; i++)
        {
            if (replacement[i] != '$')
            {
                continue;
            }

            if (replacement[i + 1] is '\'' or '`')
            {
                throw new JavaScriptException(_typeError, "A replacement may not use $' or $`: they expand to the text around each match. Use a function to build it.");
            }

            i++;
        }

        if ((long)(self.Length + 1) * replacement.Length > _limits.MaxStringChars)
        {
            throw TooLong();
        }

        return [args[0], replacement, .. args.Skip(2)];
    }

    /// <summary>The replacement function, wrapped so that what it returns over the whole call is judged against the string limit as it is returned.</summary>
    private ClrFunction Counting(JsValue replacer)
    {
        long total = 0;
        return new ClrFunction(_engine, "replacer", (thisObject, args) =>
        {
            var piece = TypeConverter.ToString(_engine.Call(replacer, thisObject, args));
            total += piece.Length;
            if (total > _limits.MaxStringChars)
            {
                throw TooLong();
            }

            return piece;
        });
    }

    private static double Integer(JsValue[] args, int index) => args.Length > index ? TypeConverter.ToIntegerOrInfinity(args[index]) : 0;

    private JavaScriptException TooLong() => new(_rangeError, "Invalid string length");

    private JsValue Join(JsValue thisObject, JsValue[] args)
    {
        if (!thisObject.IsObject())
        {
            throw new JavaScriptException(_typeError, "Array.prototype.join called on a value that is not an object");
        }

        if (_depth >= MaxNesting)
        {
            throw new JavaScriptException(_rangeError, "Maximum nesting depth exceeded");
        }

        var source = thisObject.AsObject();
        var separator = args.Length > 0 && !args[0].IsUndefined() ? TypeConverter.ToString(args[0]) : ",";
        var length = (long)Math.Min(TypeConverter.ToLength(source.Get("length")), int.MaxValue);
        if (length > _limits.MaxArraySize)
        {
            throw new JavaScriptException(_rangeError, "Invalid string length");
        }

        // Charged as it grows: an element that is itself a long string or a nested array counts what it adds.
        var chars = Math.Max(0, length - 1) * (long)separator.Length;
        if (chars > _limits.MaxStringChars)
        {
            throw new JavaScriptException(_rangeError, "Invalid string length");
        }

        _depth++;
        try
        {
            System.Text.StringBuilder builder = new();
            for (long i = 0; i < length; i++)
            {
                if ((i & 0x3FF) == 0)
                {
                    _budget.Enforce();
                }

                if (i > 0)
                {
                    builder.Append(separator);
                }

                var element = source.Get(JsNumber.Create(i));
                if (element.IsNull() || element.IsUndefined())
                {
                    continue;
                }

                var piece = element.IsString() ? element.AsString() : TypeConverter.ToString(element);
                chars += piece.Length;
                if (chars > _limits.MaxStringChars)
                {
                    throw new JavaScriptException(_rangeError, "Invalid string length");
                }

                builder.Append(piece);
            }

            return builder.ToString();
        }
        finally
        {
            _depth--;
        }
    }

    private JsValue Stringify(JsValue thisObject, JsValue[] args)
    {
        var value = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (args.Length > 1 && !args[1].IsNull() && !args[1].IsUndefined())
        {
            throw new JavaScriptException(_typeError, "JSON.stringify: a replacer is not supported here; map the value first");
        }

        if (value.IsUndefined() || value.IsSymbol() || value.IsCallable())
        {
            return JsValue.Undefined;
        }

        var indent = string.Empty;
        if (args.Length > 2)
        {
            indent = args[2].IsNumber() ? new string(' ', (int)Math.Clamp(args[2].AsNumber() is var n && double.IsNaN(n) ? 0 : n, 0, 10))
                : args[2].IsString() ? args[2].AsString()[..Math.Min(10, args[2].AsString().Length)] : string.Empty;
        }

        CappedJson json = new(_limits.MaxStringChars);
        try
        {
            json.Write(value, indent);
        }
        catch (JsonBudgetExceededException)
        {
            throw new JavaScriptException(_rangeError, "Invalid string length");
        }

        return json.Text;
    }
}
