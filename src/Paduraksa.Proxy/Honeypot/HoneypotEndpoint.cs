using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// Answers a request the gate sent into a host's trap: resolves the URL with the site, puts the page behind the trap's own
/// proof-of-work challenge when the site says so, renders it into a pooled buffer, writes it and reports the hit. Generating an
/// answer allocates, which is fine because only trap traffic pays for it (docs/design/waf.md "Honeypot").
/// </summary>
/// <remarks>
/// The first await comes after everything that holds a span, so the method that decides is not async. A request over the cap of
/// answers generated at once is answered 503 and counted as shed; its hit is not recorded, because the site was never asked.
/// </remarks>
internal static class HoneypotEndpoint
{
    /// <summary>How long a solved trap challenge is good for. Short: a crawler that keeps its cookies shows as having passed, and a day is long enough to see it.</summary>
    internal const int PowTokenHours = 24;

    public static Task HandleAsync(HttpContext context, CompiledHost host, GateRequestState state)
    {
        var trap = host.Honeypot!;
        if (trap.Site is not { } site)
        {
            // The key all of the trap comes from isn't open: fail the way a protected route does.
            return ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable);
        }

        var request = context.Request;
        var viaRule = state.HoneypotViaRule;
        var path = request.Path.Value ?? "/";
        var canonical = RoutePath.IsCanonical(path) ? path : RoutePath.Canonicalize(path);

        // A rule's action answers any method with the landing page; the prefix answers reads, and takes the trap's one POST.
        if (!viaRule && !HoneypotTargets.IsRead(request))
        {
            if (HttpMethods.IsPost(request.Method) && string.Equals(canonical, trap.VerifyPath, StringComparison.Ordinal))
            {
                return VerifyAsync(context, host, trap, site, state, canonical);
            }

            Report(context, trap, site, state, site.Resolve(canonical), StatusCodes.Status405MethodNotAllowed, HoneypotPowOutcome.None, 0);
            return ResponseWriter.WriteAsync(context, HoneypotResponses.MethodNotAllowed);
        }

        var runtime = trap.Runtime;
        if (!runtime.TryEnter())
        {
            runtime.Stats.CountShed();
            return ResponseWriter.WriteAsync(context, HoneypotResponses.Busy, HoneypotResponses.RetryAfter);
        }

        try
        {
            // The landing sits at a path no cookie of the trap's reaches, so a challenge there could never be passed.
            var target = viaRule ? site.Landing(canonical) : site.Resolve(canonical);
            var pow = HoneypotPowOutcome.None;
            if (target.NeedsPow && !viaRule && trap.Page is not null)
            {
                if (trap.ProofOfWork.Key is not { } key)
                {
                    runtime.ReportKeyUnavailable();
                    return ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable);
                }

                if (HasValidCookie(request, host, trap, key, state))
                {
                    pow = HoneypotPowOutcome.Passed;
                }
                else
                {
                    return Challenge(context, host, trap, site, state, key, target, canonical);
                }
            }

            var canary = CanaryOf(site, state, request);
            PooledBufferWriter buffer = new();
            HoneypotRender render;
            try
            {
                render = site.Render(in target, canary, buffer);
            }
            catch
            {
                buffer.Dispose();
                throw;
            }

            Record(context, trap, state, canary, target, render.Status, pow, 0);
            runtime.Stats.CountServed();
            return WriteAsync(context, trap, render, buffer);
        }
        finally
        {
            runtime.Exit();
        }
    }

    private static Task WriteAsync(HttpContext context, CompiledHoneypot trap, HoneypotRender render, PooledBufferWriter buffer)
    {
        try
        {
            var response = context.Response;
            response.StatusCode = render.Status;
            var headers = response.Headers;
            headers.ContentType = render.ContentType;
            if (render.LastModified is { } modified)
            {
                headers.LastModified = modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
            }

            if (trap.NoIndex)
            {
                headers["X-Robots-Tag"] = "noindex, nofollow";
            }

            headers.ContentLength = buffer.Length;
            if (HttpMethods.IsHead(context.Request.Method) || buffer.Length == 0)
            {
                buffer.Dispose();
                return Task.CompletedTask;
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        return WriteBodyAsync(context, buffer);

        static async Task WriteBodyAsync(HttpContext http, PooledBufferWriter body)
        {
            using (body)
            {
                await http.Response.BodyWriter.WriteAsync(body.WrittenMemory, http.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The challenge page for a page behind proof of work, or the plain 403 a client that isn't a browser gets, as the browser check gives it.</summary>
    private static Task Challenge(
        HttpContext context, CompiledHost host, CompiledHoneypot trap, IHoneypotSite site, GateRequestState state, IMacKey key, HoneypotTarget target, string canonical)
    {
        var request = context.Request;
        PrecomputedResponse answer;
        if (ProofOfWorkTargets.IsBrowserRequest(request))
        {
            Span<char> challenge = stackalloc char[ProofOfWorkChallenge.EncodedLength];
            Span<byte> nonce = stackalloc byte[ProofOfWorkChallenge.NonceLength];
            RandomNumberGenerator.Fill(nonce);
            var now = trap.ProofOfWork.Time.GetUtcNow().ToUnixTimeSeconds();
            if (now is < 0 or > uint.MaxValue
                || !ProofOfWorkChallenge.TryCreate(
                    ProofOfWorkDomain.Honeypot, key, host.ProofOfWorkHostScope, state.ClientAddress, (uint)now, trap.PowDifficulty, nonce, challenge, out var length))
            {
                return ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable);
            }

            answer = trap.Page!.Build(challenge[..length], ProofOfWorkTargets.OriginalTarget(context));
        }
        else
        {
            answer = ProofOfWorkResponses.NonBrowser;
        }

        Report(context, trap, site, state, target, answer.StatusCode, HoneypotPowOutcome.Shown, 0);
        trap.Runtime.Stats.CountServed();
        return ResponseWriter.WriteAsync(context, answer);
    }

    private static bool HasValidCookie(HttpRequest request, CompiledHost host, CompiledHoneypot trap, IMacKey key, GateRequestState state)
    {
        Span<char> cookie = stackalloc char[ProofOfWorkToken.MaxCookieLength + 1];
        if (!ProofOfWorkToken.TryReadCookie(request, ProofOfWorkDomain.Honeypot.CookieName, cookie, out var length, out _))
        {
            return false;
        }

        var now = trap.ProofOfWork.Time.GetUtcNow().ToUnixTimeSeconds();
        return ProofOfWorkToken.TryValidate(
            ProofOfWorkDomain.Honeypot, cookie[..length], key, host.ProofOfWorkHostScope, state.ClientAddress, now, trap.PowDifficulty, out _, out _, out _);
    }

    private static async Task VerifyAsync(
        HttpContext context, CompiledHost host, CompiledHoneypot trap, IHoneypotSite site, GateRequestState state, string canonical)
    {
        if (trap.ProofOfWork.Key is not { } key)
        {
            trap.Runtime.ReportKeyUnavailable();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable).ConfigureAwait(false);
            return;
        }

        var settings = new ProofOfWorkVerification(
            ProofOfWorkDomain.Honeypot, host.ProofOfWorkHostScope, trap.PowDifficulty, PowTokenHours, trap.Prefix, trap.Prefix, Stats: null);
        var time = trap.ProofOfWork.Time;
        var result = await ProofOfWorkEndpoint.VerifyAsync(context, key, time, settings).ConfigureAwait(false);

        // A solved challenge belongs to the page the visitor was sent back to; anything else is a request for a URL that is no page.
        var solved = result.Outcome == ProofOfWorkVerifyOutcome.Solved;
        var returnPath = solved ? PathOf(result.Return!) : canonical;
        var solveMilliseconds = 0;
        if (solved)
        {
            solveMilliseconds = (int)Math.Clamp((time.GetUtcNow().ToUnixTimeSeconds() - result.IssuedAt) * 1000, 0, int.MaxValue);
        }

        Report(
            context, trap, site, state, site.Resolve(returnPath), context.Response.StatusCode,
            solved ? HoneypotPowOutcome.Solved : HoneypotPowOutcome.None, solveMilliseconds);
        trap.Runtime.Stats.CountServed();
    }

    private static string PathOf(string target)
    {
        var query = target.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? target : target[..query];
        return RoutePath.IsCanonical(path) ? path : RoutePath.Canonicalize(path);
    }

    private static void Report(
        HttpContext context, CompiledHoneypot trap, IHoneypotSite site, GateRequestState state, HoneypotTarget target, int status, HoneypotPowOutcome pow, int solveMilliseconds) =>
        Record(context, trap, state, CanaryOf(site, state, context.Request), target, status, pow, solveMilliseconds);

    private static string CanaryOf(IHoneypotSite site, GateRequestState state, HttpRequest request)
    {
        Span<byte> address = stackalloc byte[16];
        state.ClientAddress.WriteBytes(address);
        return site.Canary(address, request.Headers.UserAgent.ToString());
    }

    private static void Record(
        HttpContext context, CompiledHoneypot trap, GateRequestState state, string canary, HoneypotTarget target, int status, HoneypotPowOutcome pow, int solveMilliseconds)
    {
        var request = context.Request;
        var headers = request.Headers;
        trap.Runtime.Sink.Record(new HoneypotHit(
            trap.Runtime.Time.GetUtcNow(),
            state.Host!.Id,
            state.ClientAddress,
            headers.UserAgent.ToString(),
            canary,
            state.HoneypotViaRule ? HoneypotEntry.Rule : trap.EntryByPrefix,
            target.Kind,
            target.Via,
            target.PageId,
            target.ParentId,
            status,
            pow,
            solveMilliseconds,
            request.Protocol,
            context.Features.Get<ITlsHandshakeFeature>()?.Protocol ?? SslProtocols.None,
            Header(headers.Accept),
            Header(headers.AcceptLanguage),
            Header(headers["Sec-Fetch-Site"]),
            Header(headers["Sec-Fetch-Mode"]),
            Header(headers["Sec-Fetch-Dest"]),
            Header(headers["Sec-Fetch-User"]),
            Header(headers.Referer)));
    }

    private static string? Header(StringValues value) => value.Count == 0 ? null : value.ToString();
}
