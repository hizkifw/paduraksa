using System.Text.RegularExpressions;

using Jint;
using Jint.Native;
using Jint.Native.Error;
using Jint.Native.Object;
using Jint.Native.Promise;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Paduraksa.Mcp;

/// <summary>
/// The <c>execute</c> tool's global: <c>paduraksa.request({ method, path, query?, body?, confirm?, ifMatch? })</c>. It validates what the script asked
/// for, applies <see cref="RequestPolicy"/> (refusals, and the dry run or the 428 for a write without <c>confirm</c>), and dispatches through the
/// <see cref="IApiDispatcher"/> one request at a time in the order they were made: a script that starts several without awaiting still has them run
/// in order, so a write never races the read before it. The token is the dispatcher's; no object of this process is given to the script.
/// </summary>
internal sealed partial class ExecuteHost(RequestPolicy policy, IApiDispatcher dispatcher, Action<Exception>? unexpected) : SandboxHost
{
    private static readonly HashSet<string> Options = new(["method", "path", "query", "body", "confirm", "ifMatch"], StringComparer.Ordinal);

    private readonly Queue<Pending> _queue = new();
    private Engine _engine = null!;
    private SandboxLimits _limits = null!;
    private SandboxBudget _budget = null!;
    private JsValue _parse = JsValue.Undefined;
    private ErrorConstructor _typeError = null!;
    private ErrorConstructor _rangeError = null!;
    private ErrorConstructor _error = null!;
    private int _requests;
    private int _unawaited;

    public override int Requests => _requests;

    public override string? Note => _unawaited == 0
        ? null
        : $"{_unawaited} request(s) were still pending when the function returned and were completed; await each request you depend on.";

    public override void Install(Engine engine, SandboxLimits limits, SandboxBudget budget)
    {
        _engine = engine;
        _limits = limits;
        _budget = budget;
        _parse = engine.Evaluate("JSON.parse");
        _typeError = (ErrorConstructor)engine.Evaluate("TypeError").AsObject();
        _rangeError = (ErrorConstructor)engine.Evaluate("RangeError").AsObject();
        _error = (ErrorConstructor)engine.Evaluate("Error").AsObject();

        var paduraksa = new JsObject(engine);
        paduraksa.Set("request", new ClrFunction(engine, "request", Request), true);
        engine.SetValue("paduraksa", paduraksa);
    }

    public override void FunctionReturned() => _unawaited = _queue.Count;

    public override bool ServeNext()
    {
        if (!_queue.TryDequeue(out var pending))
        {
            return false;
        }

        ApiReply reply;
        _budget.Pause();
        using var wait = _budget.Link();
        try
        {
            reply = dispatcher.SendAsync(pending.Call, wait.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw _budget.CancelledWait();
        }
        catch (Exception ex)
        {
            unexpected?.Invoke(ex);
            pending.Promise.Reject(new JavaScriptException(_error, "The request failed inside the server; nothing more is known.").Error);
            return true;
        }
        finally
        {
            _budget.Resume();
        }

        pending.Promise.Resolve(_budget.Untracked(() => Reply(reply, pending.DryRun)));
        return true;
    }

    private JsValue Request(JsValue thisObject, JsValue[] args)
    {
        var promise = _engine.Advanced.RegisterPromise();
        if (++_requests > _limits.MaxRequests)
        {
            promise.Reject(new JavaScriptException(_rangeError, $"At most {_limits.MaxRequests} requests per call. Fetch less: list endpoints take limit, and one read often answers what several would.").Error);
            return promise.Promise;
        }

        try
        {
            var (call, confirm) = Parse(args.Length > 0 ? args[0] : JsValue.Undefined);
            var decision = policy.Decide(call.Method, call.Path);
            switch (decision.Kind)
            {
                case RequestKind.Refused or RequestKind.Unknown:
                    promise.Reject(new JavaScriptException(_error, decision.Reason).Error);
                    break;
                case RequestKind.Write when !confirm && !decision.SupportsDryRun:
                    promise.Resolve(RequiresConfirm(call));
                    break;
                case RequestKind.Write when !confirm:
                    _queue.Enqueue(new Pending(call with { Query = [.. call.Query, new("dryRun", "true")] }, promise, DryRun: true));
                    break;
                default:
                    _queue.Enqueue(new Pending(call, promise, DryRun: false));
                    break;
            }
        }
        catch (JavaScriptException ex)
        {
            promise.Reject(ex.Error);
        }

        return promise.Promise;
    }

    private (ApiCall Call, bool Confirm) Parse(JsValue options)
    {
        if (!options.IsObject())
        {
            throw Invalid("request takes one object: { method, path, query?, body?, confirm?, ifMatch? }.");
        }

        var obj = options.AsObject();
        foreach (var key in obj.GetOwnPropertyKeys(Types.String))
        {
            if (!Options.Contains(key.ToString()))
            {
                throw Invalid($"Unknown option '{key}'. The options are method, path, query, body, confirm and ifMatch.");
            }
        }

        var method = obj.Get("method");
        var path = obj.Get("path");
        if (!method.IsString() || !path.IsString())
        {
            throw Invalid("method and path are required strings, e.g. { method: \"GET\", path: \"/api/v1/hosts\" }.");
        }

        var verb = method.AsString().ToUpperInvariant();
        var confirm = obj.Get("confirm");
        if (!confirm.IsUndefined() && !confirm.IsBoolean())
        {
            throw Invalid("confirm must be true or false.");
        }

        var ifMatch = obj.Get("ifMatch");
        if (!ifMatch.IsUndefined() && !(ifMatch.IsString() && EntityTag().IsMatch(ifMatch.AsString())))
        {
            throw Invalid("ifMatch must be the quoted etag a read returned, e.g. \"3\".");
        }

        var body = obj.Get("body");
        string? bodyJson = null;
        if (!body.IsUndefined())
        {
            if (verb == "GET")
            {
                throw Invalid("A GET takes no body; put filters in query.");
            }

            CappedJson json = new(_limits.MaxRequestBodyBytes);
            try
            {
                json.Write(body);
                bodyJson = json.Text;
            }
            catch (JsonBudgetExceededException)
            {
                throw Invalid($"The body is larger than {_limits.MaxRequestBodyBytes / 1024} KiB.");
            }
        }

        var call = new ApiCall(verb, path.AsString(), Query(obj.Get("query")), bodyJson, ifMatch.IsString() ? ifMatch.AsString() : null);
        return (call, confirm.IsBoolean() && confirm.AsBoolean());
    }

    private List<KeyValuePair<string, string>> Query(JsValue query)
    {
        List<KeyValuePair<string, string>> pairs = [];
        if (query.IsUndefined() || query.IsNull())
        {
            return pairs;
        }

        if (!query.IsObject() || query.IsArray())
        {
            throw Invalid("query must be an object such as { limit: 20 }.");
        }

        var obj = query.AsObject();
        foreach (var key in obj.GetOwnPropertyKeys(Types.String))
        {
            var name = key.ToString();
            if (name.Length is 0 or > 64 || string.Equals(name, "dryRun", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(name.Length == 0 || name.Length > 64 ? "A query name is empty or longer than 64 characters." : "dryRun is the client's: leave out confirm for a dry run, send confirm: true to apply.");
            }

            var value = obj.Get(key);
            if (value.IsArray())
            {
                var list = value.AsArray();
                for (uint i = 0; i < list.Length && pairs.Count < 64; i++)
                {
                    Add(pairs, name, list.Get(i));
                }
            }
            else
            {
                Add(pairs, name, value);
            }

            if (pairs.Count > 64)
            {
                throw Invalid("A query has at most 64 values.");
            }
        }

        return pairs;
    }

    private void Add(List<KeyValuePair<string, string>> pairs, string name, JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return;
        }

        if (!(value.IsString() || value.IsNumber() || value.IsBoolean()))
        {
            throw Invalid($"The query value of '{name}' must be a string, number or boolean (or an array of them).");
        }

        var text = value.ToString();
        if (text.Length > 1024)
        {
            throw Invalid($"The query value of '{name}' is longer than 1024 characters.");
        }

        pairs.Add(new(name, text));
    }

    private JavaScriptException Invalid(string message) => new(_typeError, message);

    private JsObject RequiresConfirm(ApiCall call)
    {
        var body = new JsObject(_engine);
        body.Set("requiresConfirm", JsBoolean.True, true);
        body.Set("method", call.Method, true);
        body.Set("path", call.Path, true);
        body.Set("note", "Nothing was sent: this request changes things and the operation has no dry run. Repeat it with confirm: true to apply it.", true);
        var reply = new JsObject(_engine);
        reply.Set("status", 428, true);
        reply.Set("body", body, true);
        return reply;
    }

    private JsObject Reply(ApiReply reply, bool dryRun)
    {
        var result = new JsObject(_engine);
        result.Set("status", reply.Status, true);
        result.Set("body", reply.Truncated ? JsValue.Null : Body(reply), true);
        if (reply.ETag is { Length: > 0 } etag)
        {
            result.Set("etag", etag, true);
        }

        if (dryRun)
        {
            result.Set("dryRun", JsBoolean.True, true);
            result.Set("note", "Dry run: nothing was changed. Repeat the request with confirm: true to apply it.", true);
        }
        else if (reply.Truncated)
        {
            result.Set("note", $"The answer is larger than {_limits.MaxResponseBytes / 1024} KiB and was not read; narrow the request (limit, filters).", true);
        }

        return result;
    }

    private JsValue Body(ApiReply reply)
    {
        if (string.IsNullOrEmpty(reply.Body))
        {
            return JsValue.Null;
        }

        if (reply.ContentType is { } type && type.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return _engine.Call(_parse, JsValue.Undefined, [reply.Body]);
            }
            catch (JavaScriptException)
            {
                // Not JSON after all (or deeper than the parser allows): the text is still the answer.
            }
        }

        return reply.Body;
    }

    [GeneratedRegex("^\"[\\x21\\x23-\\x7E]{1,200}\"$")]
    private static partial Regex EntityTag();

    private sealed record Pending(ApiCall Call, ManualPromise Promise, bool DryRun);
}
