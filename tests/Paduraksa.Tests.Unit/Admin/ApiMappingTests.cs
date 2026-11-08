using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

using Paduraksa.Admin.Api;
using Paduraksa.Admin.Json;
using Paduraksa.Admin.OpenApi;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Admin;

/// <summary>The admin API's mapping logic: result statuses to HTTP, preconditions, cursors, typed-ID routing and schemas, the JSON contract.</summary>
public sealed class ApiMappingTests
{
    [Fact]
    public void EveryConfigCommandStatusMapsToAnIntendedStatus()
    {
        foreach (ConfigCommandStatus status in Enum.GetValues<ConfigCommandStatus>())
        {
            Assert.NotEqual(0, ApiStatusMapping.Of(status, MutationKind.Change));
            Assert.True(status == ConfigCommandStatus.PublishFailed || ApiStatusMapping.Of(status, MutationKind.Change) != 500, $"{status} must not fall through to 500");
        }

        Assert.Equal(201, ApiStatusMapping.Of(ConfigCommandStatus.Committed, MutationKind.Create));
        Assert.Equal(200, ApiStatusMapping.Of(ConfigCommandStatus.Committed, MutationKind.Change));
        Assert.Equal(200, ApiStatusMapping.Of(ConfigCommandStatus.DryRun, MutationKind.Change));
        Assert.Equal(422, ApiStatusMapping.Of(ConfigCommandStatus.Invalid, MutationKind.Change));
        Assert.Equal(409, ApiStatusMapping.Of(ConfigCommandStatus.Conflict, MutationKind.Create));
        Assert.Equal(412, ApiStatusMapping.Of(ConfigCommandStatus.Conflict, MutationKind.Change));
        Assert.Equal(404, ApiStatusMapping.Of(ConfigCommandStatus.NotFound, MutationKind.Change));
        Assert.Equal(403, ApiStatusMapping.Of(ConfigCommandStatus.Denied, MutationKind.Change));
        Assert.Equal(503, ApiStatusMapping.Of(ConfigCommandStatus.Unavailable, MutationKind.Change));
        Assert.Equal(500, ApiStatusMapping.Of(ConfigCommandStatus.PublishFailed, MutationKind.Change));
    }

    [Fact]
    public void EveryOtherResultStatusIsMapped()
    {
        foreach (CertificateOperationStatus status in Enum.GetValues<CertificateOperationStatus>())
        {
            Assert.NotEqual(500, ApiStatusMapping.Of(status, MutationKind.Create));
        }

        foreach (IssuanceOutcome outcome in Enum.GetValues<IssuanceOutcome>())
        {
            Assert.NotEqual(500, ApiStatusMapping.Of(outcome, MutationKind.Create));
        }

        foreach (IssuanceChangeStatus status in Enum.GetValues<IssuanceChangeStatus>())
        {
            Assert.NotEqual(500, ApiStatusMapping.Of(status, MutationKind.Create));
        }

        foreach (SimulationStatus status in Enum.GetValues<SimulationStatus>())
        {
            Assert.NotEqual(500, ApiStatusMapping.Of(status));
        }

        Assert.Equal(400, ApiStatusMapping.Of(SimulationStatus.BadRequest));
        Assert.Equal(422, ApiStatusMapping.Of(SimulationStatus.Invalid));
        Assert.Equal(503, ApiStatusMapping.Of(SimulationStatus.Unavailable));
        Assert.Equal(403, ApiStatusMapping.Of(SimulationStatus.Denied));
        Assert.Equal(502, ApiStatusMapping.Of(IssuanceOutcome.Failed, MutationKind.Create));
    }

    [Theory]
    [InlineData(null, "Missing", null)]
    [InlineData("", "Missing", null)]
    [InlineData("\"7\"", "Tag", "7")]
    [InlineData("7", "Unsupported", null)] // unquoted
    [InlineData("*", "Unsupported", null)]
    [InlineData("W/\"7\"", "Weak", null)]
    [InlineData("\"1\", \"2\"", "Unsupported", null)]
    [InlineData("\"\"", "Unsupported", null)]
    public void IfMatchParsesOneQuotedStrongTag(string? header, string state, string? tag)
    {
        (IfMatchState actual, string? parsed) = Preconditions.ParseIfMatch(header is null ? StringValues.Empty : new StringValues(header));

        Assert.Equal(state, actual.ToString());
        Assert.Equal(tag, parsed);
    }

    [Theory]
    [InlineData("\"0\"", true, 0)]
    [InlineData("\"12\"", true, 12)]
    [InlineData("\"012\"", false, 0)] // a leading zero is another tag
    [InlineData("\"-1\"", false, 0)]
    [InlineData("\"1.5\"", false, 0)]
    public void AVersionTagIsPlainDecimal(string header, bool ok, long version)
    {
        DefaultHttpContext http = new();
        http.Request.Headers.IfMatch = header;

        Assert.Equal(ok, Preconditions.TryGetVersion(http, out long parsed, out IResult? problem));
        Assert.Equal(version, parsed);
        Assert.Equal(ok, problem is null);
    }

    private static DefaultHttpContext Context() => new()
    {
        RequestServices = new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).BuildServiceProvider(),
        Response = { Body = new MemoryStream() },
    };

    [Fact]
    public async Task AWeakTagIs412AndAMissingOneIs428()
    {
        DefaultHttpContext weak = Context();
        weak.Request.Headers.IfMatch = "W/\"1\"";
        Preconditions.TryGetVersion(weak, out _, out IResult? weakProblem);
        await weakProblem!.ExecuteAsync(weak);
        Assert.Equal(412, weak.Response.StatusCode);

        DefaultHttpContext none = Context();
        Preconditions.TryGetVersion(none, out _, out IResult? noneProblem);
        await noneProblem!.ExecuteAsync(none);
        Assert.Equal(428, none.Response.StatusCode);
    }

    [Fact]
    public async Task APublishFailureIsA500ProblemThatNamesTheStoredRevisionAndTheNewVersion()
    {
        ConfigRevisionId revision = ConfigRevisionId.New();
        ConfigCommandResult result = new(
            ConfigCommandStatus.PublishFailed,
            Revision: new RevisionRef(revision, 7),
            Publish: new PublishOutcome(PublishOutcomeStatus.Rejected, 0, ["destination 'x' has no address"], revision),
            Version: 5);
        DefaultHttpContext http = Context();

        await ConfigCommandResponses.ToResult(http, result, MutationKind.Change).ExecuteAsync(http);

        Assert.Equal(500, http.Response.StatusCode);
        Assert.Equal("\"5\"", http.Response.Headers.ETag.ToString());
        http.Response.Body.Position = 0;
        ApiProblem problem = JsonSerializer.Deserialize(http.Response.Body, AdminJsonContext.Default.ApiProblem)!;
        Assert.Equal("config.publishFailed", problem.Code);
        Assert.Equal(revision, problem.Revision!.Id);
        Assert.Equal(5, problem.Version);
        Assert.Equal("config.publishFailed", Assert.Single(problem.Errors!).Code);
    }

    [Fact]
    public async Task DeletesAnswer204AndACreatingPutAnswers201WithLocation()
    {
        ConfigCommandResult committed = new(ConfigCommandStatus.Committed, Revision: new RevisionRef(ConfigRevisionId.New(), 1), Version: 2);
        DefaultHttpContext deleted = Context();
        await ConfigCommandResponses.ToResult(deleted, committed, MutationKind.Change, noContent: true).ExecuteAsync(deleted);
        Assert.Equal(204, deleted.Response.StatusCode);

        DefaultHttpContext dryRun = Context();
        await ConfigCommandResponses.ToResult(dryRun, new ConfigCommandResult(ConfigCommandStatus.DryRun), MutationKind.Change, noContent: true).ExecuteAsync(dryRun);
        Assert.Equal(200, dryRun.Response.StatusCode); // the plan is the point of a dry run

        DefaultHttpContext put = Context();
        await ConfigCommandResponses.ToResult(put, committed, MutationKind.Change, "/api/v1/hosts/x/routes/y", created: true).ExecuteAsync(put);
        Assert.Equal(201, put.Response.StatusCode);
        Assert.Equal("/api/v1/hosts/x/routes/y", put.Response.Headers.Location.ToString());
    }

    [Fact]
    public void CursorsRoundTripAndRejectAnythingElse()
    {
        ConfigRevisionId id = ConfigRevisionId.New();
        string cursor = Pagination.Encode(id);

        Assert.True(Pagination.TryDecode(cursor, out ConfigRevisionId decoded));
        Assert.Equal(id, decoded);
        Assert.False(Pagination.TryDecode<ConfigRevisionId>("not a cursor!", out _));
        Assert.False(Pagination.TryDecode<ConfigRevisionId>(null, out _));
        Assert.False(Pagination.TryDecode<AuditEventId>(cursor, out _)); // another resource's cursor
        Assert.False(Pagination.TryDecode<ConfigRevisionId>(new string('A', 200), out _));
    }

    [Fact]
    public void TheTypedIdConstraintMatchesOnlyItsOwnIds()
    {
        TypedIdRouteConstraint<HostId> constraint = new();
        static RouteValueDictionary Values(string value) => new() { ["id"] = value };

        Assert.True(constraint.Match(null, null, "id", Values(HostId.New().ToString()!), RouteDirection.IncomingRequest));
        Assert.False(constraint.Match(null, null, "id", Values(UpstreamId.New().ToString()!), RouteDirection.IncomingRequest));
        Assert.False(constraint.Match(null, null, "id", Values("nope"), RouteDirection.IncomingRequest));
        Assert.False(constraint.Match(null, null, "id", Values(HostId.Empty.ToString()!), RouteDirection.IncomingRequest));
        Assert.False(constraint.Match(null, null, "id", [], RouteDirection.IncomingRequest));
    }

    [Fact]
    public void TheTypedIdSchemaIsAPrefixedFixedLengthString()
    {
        Assert.True(TypedIdSchemas.TryDescribe(typeof(HostId), out TypedIdSchema host));
        Assert.True(TypedIdSchemas.TryDescribe(typeof(AuditEventId), out TypedIdSchema audit));
        Assert.False(TypedIdSchemas.TryDescribe(typeof(Guid), out _));

        Assert.Equal("^host_[0-9A-Za-z]{22}$", host.Pattern);
        Assert.Matches(host.Pattern, host.Example);
        Assert.Matches(audit.Pattern, audit.Example);
    }

    [Fact]
    public void HttpJsonOptionsWriteEnumsAndTypedIdsAsStringsAndRejectUnknownProperties()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        AdminJsonContext.AddTo(options);
        UserId user = UserId.New();
        AuditEventSummaryProbe probe = new(Channel.Api, user, CertificateSource.LocalCaRoot);

        string json = JsonSerializer.Serialize(probe, options);

        Assert.Contains($"\"{user}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{Channel.Api.ToName()}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{CertificateSource.LocalCaRoot.ToName()}\"", json, StringComparison.Ordinal);
        Assert.Equal(probe, JsonSerializer.Deserialize<AuditEventSummaryProbe>(json, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuditEventSummaryProbe>($$"""{"channel":"api","user":"{{user}}","source":"acme","extra":1}""", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuditEventSummaryProbe>($$"""{"channel":3,"user":"{{user}}","source":"acme"}""", options));
    }

    [Fact]
    public void TheApiContractRejectsUnknownPropertiesAndWritesNamesForEnums()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"direct":1,"thisIsATypo":2}""", AdminJsonContext.Default.RotateDnsCredentialBody));
        string json = JsonSerializer.Serialize(new RevisionDiff(ConfigRevisionId.New(), ConfigRevisionId.New(), []), AdminJsonContext.Default.RevisionDiff);
        Assert.Contains("\"from\":\"rev_", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProblemsCarryTheTypeAndTitleOfTheirStatus()
    {
        Assert.EndsWith("section-15.5.5", ApiProblems.TypeOf(StatusCodes.Status404NotFound), StringComparison.Ordinal);
        Assert.Equal("Precondition required", ApiProblems.TitleOf(428));
        Assert.True(ApiBody.IsJson("application/json; charset=utf-8"));
        Assert.False(ApiBody.IsJson("application/json; charset=latin1"));
        Assert.False(ApiBody.IsJson("text/plain"));
        Assert.False(ApiBody.IsJson(null));
    }

    internal sealed record AuditEventSummaryProbe(Channel Channel, UserId User, CertificateSource Source);
}
