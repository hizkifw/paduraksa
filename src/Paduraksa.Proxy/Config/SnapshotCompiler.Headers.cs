using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Config;

/// <summary>A host's response headers, compiled into the one list the gate and YARP's transform share.</summary>
public static partial class SnapshotCompiler
{
    /// <summary>
    /// The host's response headers, compiled into the one list the gate, the reapply after a forward and YARP's transform
    /// loop over (PLAN §7.2 C): <c>Server</c> first, because it goes on every answer (a 421 included), then
    /// <c>Alt-Svc</c> and HSTS for the answers after the TLS check, then the response <c>X-Request-Id</c>.
    /// <para>A header the host doesn't send is still an operation (<see cref="ResponseHeaderKind.NotSent"/>), so the trace
    /// can say why, and the <c>Server</c> and request-ID entries are never absent: every answer has them.</para>
    /// <para>The validator reports these rules too; the compiler is the backstop for a model built elsewhere, so a value
    /// that can't be sent is never sent.</para>
    /// </summary>
    /// <param name="serverHeader">The host's resolved <c>Server</c> header.</param>
    /// <param name="hsts">The host's resolved HSTS, or null for the global defaults, which send none.</param>
    /// <param name="altSvc">The precomputed <c>Alt-Svc</c> value, or empty when the host isn't served over HTTP/3.</param>
    /// <param name="altSvcReason">Why <paramref name="altSvc"/> is empty, for the trace.</param>
    /// <param name="hstsReason">Why the host has no HSTS, for the trace.</param>
    /// <param name="propagation">Where the request ID goes.</param>
    /// <param name="where">The object these belong to.</param>
    /// <param name="errors">The compile errors to add to.</param>
    private static ResponseHeaderOperation[] CompileResponseHeaders(
        HostServerHeaderSpec serverHeader,
        HostHstsSpec? hsts,
        StringValues altSvc,
        string altSvcReason,
        string hstsReason,
        RequestIdPropagation propagation,
        CompileErrorSource where,
        List<CompileError> errors)
    {
        ResponseHeaderOperationBuilder operations = new(where, errors);
        operations.Add(CompileServerHeader(serverHeader, where, errors));
        operations.Add(StringValues.IsNullOrEmpty(altSvc)
            ? new ResponseHeaderOperation(AltSvcHeader.HeaderName, StringValues.Empty, ResponseHeaderKind.NotSent, ResponseHeaderStage.AfterTlsCheck, ResponseHeaderConditions.None, Reason: altSvcReason)
            : new ResponseHeaderOperation(AltSvcHeader.HeaderName, altSvc, ResponseHeaderKind.Set, ResponseHeaderStage.AfterTlsCheck, ResponseHeaderConditions.Tls | ResponseHeaderConditions.NotHttp3));
        operations.Add(hsts is { Enabled: true }
            ? new ResponseHeaderOperation(HstsHeader.HeaderName, CompileHsts(hsts, where, errors), ResponseHeaderKind.Set, ResponseHeaderStage.AfterTlsCheck, ResponseHeaderConditions.TrustedHttps | ResponseHeaderConditions.NotExemptName)
            : new ResponseHeaderOperation(HstsHeader.HeaderName, StringValues.Empty, ResponseHeaderKind.NotSent, ResponseHeaderStage.AfterTlsCheck, ResponseHeaderConditions.None, Reason: hstsReason));
        operations.Add(propagation.SendsResponse()
            ? new ResponseHeaderOperation(Logging.RequestIds.HeaderName, StringValues.Empty, ResponseHeaderKind.ReplaceUpstream, ResponseHeaderStage.AfterDecision, ResponseHeaderConditions.None, ResponseHeaderValue.RequestId)
            : new ResponseHeaderOperation(Logging.RequestIds.HeaderName, StringValues.Empty, ResponseHeaderKind.Remove, ResponseHeaderStage.AfterDecision, ResponseHeaderConditions.None, Reason: "the host doesn't send the ID to the client"));
        return operations.Build();
    }

    /// <summary>
    /// The <c>Server</c> operation: ours for the name or the custom value, a removal for <see cref="ServerHeaderMode.Remove"/>,
    /// and nothing for <see cref="ServerHeaderMode.Upstream"/>, whose header is the upstream's own.
    /// </summary>
    private static ResponseHeaderOperation CompileServerHeader(HostServerHeaderSpec spec, CompileErrorSource where, List<CompileError> errors)
    {
        if (!Enum.IsDefined(spec.Mode))
        {
            errors.Add(new(where, $"unknown Server header mode {spec.Mode}"));
            return new(ServerHeader.HeaderName, StringValues.Empty, ResponseHeaderKind.LeaveToUpstream, ResponseHeaderStage.AfterHostLookup, ResponseHeaderConditions.None, Reason: "the host's Server header mode isn't one this build knows");
        }

        if (spec.Mode == ServerHeaderMode.Custom && !ServerHeader.IsValidValue(spec.Value))
        {
            errors.Add(new(
                where,
                $"the custom Server header must be 1-{ServerHeader.MaxValueLength} visible ASCII characters or spaces, with no leading or trailing space"));
            return new(ServerHeader.HeaderName, StringValues.Empty, ResponseHeaderKind.Remove, ResponseHeaderStage.AfterHostLookup, ResponseHeaderConditions.None);
        }

        return spec.Mode switch
        {
            // YARP appends an upstream's Server to what is already there, so ours has to replace that copy after the forward.
            ServerHeaderMode.Remove => new(ServerHeader.HeaderName, StringValues.Empty, ResponseHeaderKind.Remove, ResponseHeaderStage.AfterHostLookup, ResponseHeaderConditions.None),
            ServerHeaderMode.Upstream => new(ServerHeader.HeaderName, StringValues.Empty, ResponseHeaderKind.LeaveToUpstream, ResponseHeaderStage.AfterHostLookup, ResponseHeaderConditions.None, Reason: "the upstream's own Server header passes through, and a gate answer carries none"),
            _ => new(ServerHeader.HeaderName, ServerHeader.ValueOf(spec), ResponseHeaderKind.ReplaceUpstream, ResponseHeaderStage.AfterHostLookup, ResponseHeaderConditions.None),
        };
    }

    /// <summary>
    /// The host's one <c>Strict-Transport-Security</c> value. The validator reports these rules too; the compiler is the
    /// backstop for a model built elsewhere, so a value that can't be sent is never sent.
    /// </summary>
    private static StringValues CompileHsts(HostHstsSpec spec, CompileErrorSource where, List<CompileError> errors)
    {
        if (spec.MaxAgeSeconds is < 0 or > HstsHeader.MaxMaxAgeSeconds)
        {
            errors.Add(new(where, $"HSTS max-age must be 0-{HstsHeader.MaxMaxAgeSeconds} seconds, got {spec.MaxAgeSeconds}"));
            return StringValues.Empty;
        }

        if (spec.Preload && !spec.IncludeSubDomains)
        {
            errors.Add(new(where, "HSTS preload needs includeSubDomains"));
            return StringValues.Empty;
        }

        if (spec.Preload && spec.MaxAgeSeconds < HstsHeader.PreloadMinMaxAgeSeconds)
        {
            errors.Add(new(where, $"HSTS preload needs a max-age of at least {HstsHeader.PreloadMinMaxAgeSeconds} seconds (a year), got {spec.MaxAgeSeconds}"));
            return StringValues.Empty;
        }

        return HstsHeader.ValueOf(spec);
    }
}
