using Microsoft.AspNetCore.Http;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Simulation;

namespace Paduraksa.Admin.Api;

/// <summary>Whether a mutation creates the resource it names (<c>POST</c> to a collection) or changes an existing one.</summary>
public enum MutationKind
{
    /// <summary>A new resource: committed is 201, a version conflict means it exists already (409).</summary>
    Create,

    /// <summary>An existing resource: committed is 200, a version conflict is 412.</summary>
    Change,
}

/// <summary>
/// How the services' result statuses become HTTP statuses. One table per result type, in one place, so every endpoint of a kind answers
/// alike and the mapping is testable without a server. A status that is not a success becomes a problem (<see cref="ApiProblems"/>) carrying
/// the service's errors.
/// </summary>
public static class ApiStatusMapping
{
    /// <summary>A config command's status.</summary>
    public static int Of(ConfigCommandStatus status, MutationKind kind) => status switch
    {
        ConfigCommandStatus.Committed => kind == MutationKind.Create ? StatusCodes.Status201Created : StatusCodes.Status200OK,
        ConfigCommandStatus.DryRun or ConfigCommandStatus.Unchanged => StatusCodes.Status200OK,
        // The change is stored but not live (the previous config still serves). Nothing retries it, so it is not a success: a 500 problem (code publish_failed)
        // that names the stored revision and the new version, so a client knows what happened and a blind retry meets a 412, not a duplicate.
        ConfigCommandStatus.PublishFailed => StatusCodes.Status500InternalServerError,
        ConfigCommandStatus.Invalid => StatusCodes.Status422UnprocessableEntity,
        ConfigCommandStatus.Conflict => kind == MutationKind.Create ? StatusCodes.Status409Conflict : StatusCodes.Status412PreconditionFailed,
        ConfigCommandStatus.NotFound => StatusCodes.Status404NotFound,
        ConfigCommandStatus.Denied => StatusCodes.Status403Forbidden,
        ConfigCommandStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>A certificate operation's status (<see cref="MutationKind.Create"/> for the ones that make a certificate).</summary>
    public static int Of(CertificateOperationStatus status, MutationKind kind) => status switch
    {
        CertificateOperationStatus.Completed => kind == MutationKind.Create ? StatusCodes.Status201Created : StatusCodes.Status200OK,
        CertificateOperationStatus.Unchanged => StatusCodes.Status200OK,
        CertificateOperationStatus.NotFound => StatusCodes.Status404NotFound,
        CertificateOperationStatus.Invalid => StatusCodes.Status422UnprocessableEntity,
        CertificateOperationStatus.Forbidden => StatusCodes.Status403Forbidden,
        CertificateOperationStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>An ACME issuance, renewal or request.</summary>
    public static int Of(IssuanceOutcome outcome, MutationKind kind) => outcome switch
    {
        IssuanceOutcome.Issued => kind == MutationKind.Create ? StatusCodes.Status201Created : StatusCodes.Status200OK,
        // The CA refused or could not be reached: not this request's fault, and the scheduler retries.
        IssuanceOutcome.Failed => StatusCodes.Status502BadGateway,
        IssuanceOutcome.InProgress => StatusCodes.Status409Conflict,
        IssuanceOutcome.Invalid => StatusCodes.Status422UnprocessableEntity,
        IssuanceOutcome.NotFound => StatusCodes.Status404NotFound,
        IssuanceOutcome.Forbidden => StatusCodes.Status403Forbidden,
        IssuanceOutcome.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>A change to an ACME account or a DNS provider.</summary>
    public static int Of(IssuanceChangeStatus status, MutationKind kind) => status switch
    {
        IssuanceChangeStatus.Committed => kind == MutationKind.Create ? StatusCodes.Status201Created : StatusCodes.Status200OK,
        IssuanceChangeStatus.NotFound => StatusCodes.Status404NotFound,
        IssuanceChangeStatus.Invalid => StatusCodes.Status422UnprocessableEntity,
        IssuanceChangeStatus.Failed => StatusCodes.Status502BadGateway,
        IssuanceChangeStatus.Denied => StatusCodes.Status403Forbidden,
        IssuanceChangeStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>A simulation. A draft command that can't be applied as it stands (a stale version) is a conflict with the current config.</summary>
    public static int Of(SimulationStatus status) => status switch
    {
        SimulationStatus.Simulated => StatusCodes.Status200OK,
        SimulationStatus.BadRequest => StatusCodes.Status400BadRequest,
        SimulationStatus.Invalid => StatusCodes.Status422UnprocessableEntity,
        SimulationStatus.RevisionNotFound => StatusCodes.Status404NotFound,
        SimulationStatus.DraftRejected or SimulationStatus.DraftBlocked => StatusCodes.Status409Conflict,
        SimulationStatus.DraftNotFound => StatusCodes.Status404NotFound,
        SimulationStatus.DraftExpired => StatusCodes.Status410Gone,
        SimulationStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
        SimulationStatus.Denied => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>True for 2xx.</summary>
    public static bool IsSuccess(int status) => status is >= 200 and < 300;
}
