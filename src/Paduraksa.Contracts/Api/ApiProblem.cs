using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Contracts.Api;

/// <summary>
/// An error answer of the REST API: an RFC 9457 problem details object (<c>application/problem+json</c>). Every error
/// the admin app answers under <c>/api</c> has this shape, whoever produced it: <c>type</c>, <c>title</c>, <c>status</c>,
/// <c>detail</c> and <c>traceId</c> are the standard members, the rest are extension members that appear when they apply.
/// </summary>
/// <param name="Type">A URI naming the kind of problem (the HTTP status's RFC 9110 section).</param>
/// <param name="Title">A short, fixed summary of the kind of problem.</param>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Detail">What went wrong this time. Never an exception message or a stack trace.</param>
/// <param name="Instance">The resource the problem is about, when there is one (the certificate a failed issuance kept).</param>
/// <param name="Trace">(wire name <c>traceId</c>) Identifies the request in the server's log; an unexpected failure's details are only there.</param>
/// <param name="Errors">Validation errors: what is wrong with what the caller sent (<see cref="ConfigError.Path"/> is a JSON
/// pointer into the request body).</param>
/// <param name="RelatedErrors">Errors the change would cause elsewhere in the config.</param>
/// <param name="Warnings">What compiles but probably isn't intended.</param>
/// <param name="Conflict">For a version mismatch: what is current now.</param>
/// <param name="Code">A stable machine-readable name for the kind of failure (<c>csrf</c>, <c>setup_required</c>, <c>publish_failed</c>), when there is one.</param>
/// <param name="Revision">For <c>publish_failed</c>: the revision that was stored but is not live.</param>
/// <param name="Version">For <c>publish_failed</c>: the object's new version (the <c>ETag</c> to send with the next change).</param>
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string? Detail = null,
    string? Instance = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("traceId")] string? Trace = null,
    IReadOnlyList<ConfigError>? Errors = null,
    IReadOnlyList<ConfigError>? RelatedErrors = null,
    IReadOnlyList<ConfigWarning>? Warnings = null,
    ConfigConflict? Conflict = null,
    string? Code = null,
    RevisionRef? Revision = null,
    long? Version = null);
