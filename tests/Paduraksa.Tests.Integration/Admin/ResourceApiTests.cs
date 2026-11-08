using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Audit;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Config;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>Certificates, ACME accounts, DNS providers, the audit log and the simulator through the real host.</summary>
public sealed class ResourceApiTests
{
    private const string DnsToken = "SUPER-SECRET-DNS-TOKEN-12345";
    private const string RotatedToken = "ROTATED-SECRET-DNS-TOKEN-67890";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string json) => AdminApiHarness.JsonBody(json);

    // The summaries of the certificate DTOs have nullable members without defaults, which the strict contract demands on read: the tests look at the JSON.
    private static async Task<JsonNode> NodeAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static async Task<JsonNode> CertificateAsync(HttpResponseMessage response) => (await NodeAsync(response))["certificate"]!;

    private static CertificateId IdOf(JsonNode certificate) => CertificateId.Parse(certificate["id"]!.GetValue<string>());

    internal static (byte[] Pem, string Name) MakeCertificatePem(string name)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
#pragma warning disable RS0030 // Test fixture: a throwaway key generated just above, written into the upload body, never persisted.
        string pem = certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem();
#pragma warning restore RS0030
        return (Encoding.ASCII.GetBytes(pem), name);
    }

    internal static MultipartFormDataContent Upload(byte[] file, string? label = null, string? password = null, bool fileFirst = false)
    {
        MultipartFormDataContent form = [];
        ByteArrayContent part = new(file);
        if (fileFirst)
        {
            form.Add(part, "file", "bundle.pem");
        }

        if (password is not null)
        {
            form.Add(new StringContent(password), "password");
        }

        if (label is not null)
        {
            form.Add(new StringContent(label), "label");
        }

        if (!fileFirst)
        {
            form.Add(part, "file", "bundle.pem");
        }

        return form;
    }

    [Fact]
    public async Task ACertificateIsUploadedListedReadWithoutItsKeyAndDeleted()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        (byte[] pem, string name) = MakeCertificatePem("upload.test");

        using HttpResponseMessage created = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", content: Upload(pem, "my cert"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonNode summary = await CertificateAsync(created);
        CertificateId id = IdOf(summary);
        Assert.Equal(CertificateSource.Uploaded.ToName(), summary["source"]!.GetValue<string>());
        Assert.Equal("my cert", summary["label"]!.GetValue<string>());
        Assert.Contains(name, summary["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal($"/api/v1/certificates/{id}", created.Headers.Location!.ToString());

        using HttpResponseMessage list = await api.GetAsync("/api/v1/certificates?source=uploaded");
        Assert.Single((await NodeAsync(list)).AsArray());
        using HttpResponseMessage other = await api.GetAsync("/api/v1/certificates?source=acme");
        Assert.Empty((await NodeAsync(other)).AsArray());
        using HttpResponseMessage badFilter = await api.GetAsync("/api/v1/certificates?source=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);

        using HttpResponseMessage detail = await api.GetAsync($"/api/v1/certificates/{id}");
        string body = await detail.Content.ReadAsStringAsync(Ct);
        Assert.Contains("BEGIN CERTIFICATE", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", await created.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using HttpResponseMessage replaced = await api.SendAsync(HttpMethod.Put, $"/api/v1/certificates/{id}", content: Upload(MakeCertificatePem("upload.test").Pem));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        using HttpResponseMessage deleted = await api.SendAsync(HttpMethod.Delete, $"/api/v1/certificates/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using HttpResponseMessage gone = await api.GetAsync($"/api/v1/certificates/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task BadAndOversizeUploadsAreRejectedAsProblems()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage huge = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", content: Upload(new byte[2 * 1024 * 1024]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        Assert.Equal(413, (await AdminApiHarness.ProblemAsync(huge, Ct)).Status);

        using HttpResponseMessage overVaultLimit = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", content: Upload(new byte[1_100_000]));
        ApiProblem tooLarge = await AdminApiHarness.ProblemAsync(overVaultLimit, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overVaultLimit.StatusCode);
        Assert.Contains(tooLarge.Errors!, e => e.Code == "certificate.import.tooLarge");

        using HttpResponseMessage garbage = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", content: Upload(Encoding.ASCII.GetBytes("not a certificate")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, garbage.StatusCode);

        using HttpResponseMessage json = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", content: Json("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);

        using MultipartFormDataContent noFile = [];
        noFile.Add(new StringContent("pw"), "password");
        using HttpResponseMessage missing = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", content: noFile);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using MultipartFormDataContent unknownPart = [];
        unknownPart.Add(new StringContent("x"), "surprise");
        using HttpResponseMessage unknown = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", content: unknownPart);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using MultipartFormDataContent twice = [];
        twice.Add(new StringContent("one"), "password");
        twice.Add(new StringContent("two"), "password");
        twice.Add(new ByteArrayContent(MakeCertificatePem("dup.test").Pem), "file", "bundle.pem");
        using HttpResponseMessage duplicate = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", content: twice);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using MultipartFormDataContent twoFiles = [];
        twoFiles.Add(new ByteArrayContent(MakeCertificatePem("a.test").Pem), "file", "a.pem");
        twoFiles.Add(new ByteArrayContent(MakeCertificatePem("b.test").Pem), "file", "b.pem");
        using HttpResponseMessage secondFile = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", content: twoFiles);
        Assert.Equal(HttpStatusCode.Created, secondFile.StatusCode); // the first file is the upload; nothing after it is read

        using HttpResponseMessage viewer = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates", "viewer", content: Upload(MakeCertificatePem("v.test").Pem));
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
    }

    [Fact]
    public async Task SelfSignedAndLocalCaCertificatesAreIssuedReissuedAndDeleted()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage selfSigned = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates/self-signed", content: Json("""{"names":["ss.test"],"keySpec":"ecdsa-p256","label":"ss"}"""));
        JsonNode created = await CertificateAsync(selfSigned);
        CertificateId createdId = IdOf(created);
        Assert.Equal(HttpStatusCode.Created, selfSigned.StatusCode);
        Assert.Equal(CertificateSource.SelfSigned.ToName(), created["source"]!.GetValue<string>());

        using HttpResponseMessage reissued = await api.SendAsync(HttpMethod.Post, $"/api/v1/certificates/{createdId}/reissue");
        JsonNode again = await CertificateAsync(reissued);
        Assert.Equal(createdId, IdOf(again));
        Assert.NotEqual(created["sha256Fingerprint"]!.GetValue<string>(), again["sha256Fingerprint"]!.GetValue<string>());

        using HttpResponseMessage noNames = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates/self-signed", content: Json("""{"names":[]}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noNames.StatusCode);
        Assert.NotEmpty((await AdminApiHarness.ProblemAsync(noNames, Ct)).Errors!);

        // The local CA root exists from the first start (the admin listener's own certificate is issued from it, PLAN §3), so a
        // leaf can always be issued and the "no local CA yet" answer (certificate.localCa.missing) is no longer reachable
        // through the API; CertificateOperationsTests asserts it where it can still arise.
        using HttpResponseMessage ca = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates/local-ca");
        Assert.Equal(CertificateSource.LocalCaRoot.ToName(), (await CertificateAsync(ca))["source"]!.GetValue<string>());
        using HttpResponseMessage leaf = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates/local-ca/leaf", content: Json("""{"names":["leaf.test"]}"""));
        Assert.Equal(CertificateSource.LocalCa.ToName(), (await CertificateAsync(leaf))["source"]!.GetValue<string>());

        using HttpResponseMessage deleted = await api.SendAsync(HttpMethod.Delete, $"/api/v1/certificates/{createdId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using HttpResponseMessage missing = await api.SendAsync(HttpMethod.Delete, $"/api/v1/certificates/{createdId}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task AcmeRequestsAreValidatedAndOrdersReported()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        CertificateIssueRequest request = new(["a.example.com"], AcmeAccountId.New());

        using HttpResponseMessage unknownAccount = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/certificates/acme", request, AdminJsonContext.Default.CertificateIssueRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownAccount.StatusCode);
        Assert.NotEmpty((await AdminApiHarness.ProblemAsync(unknownAccount, Ct)).Errors!);

        using HttpResponseMessage orders = await api.GetAsync("/api/v1/acme-orders");
        Assert.Empty(await AdminApiHarness.ReadAsync(orders, AdminJsonContext.Default.IReadOnlyListAcmeOrderSummary, Ct));
        using HttpResponseMessage noOrder = await api.GetAsync($"/api/v1/certificates/{CertificateId.New()}/order");
        Assert.Equal(HttpStatusCode.NotFound, noOrder.StatusCode);
        using HttpResponseMessage renew = await api.SendAsync(HttpMethod.Post, $"/api/v1/certificates/{CertificateId.New()}/renew");
        Assert.Equal(HttpStatusCode.NotFound, renew.StatusCode);
        using HttpResponseMessage viewer = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/certificates/acme", request, AdminJsonContext.Default.CertificateIssueRequest, "viewer");
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
    }

    [Fact]
    public async Task AcmeAccountRequestsAreValidatedBeforeAnySecretOrCaIsTouched()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage http = await api.SendAsync(HttpMethod.Post, "/api/v1/acme-accounts", content: Json("""{"directoryUrl":"http://ca.test/dir","termsOfServiceAgreed":true}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, http.StatusCode);
        using HttpResponseMessage half = await api.SendAsync(HttpMethod.Post, "/api/v1/acme-accounts", content: Json("""{"directoryUrl":"https://ca.test/dir","termsOfServiceAgreed":true,"eabKid":"kid"}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, half.StatusCode);
        string halfBody = await half.Content.ReadAsStringAsync(Ct);
        Assert.Contains("acme.account.eab.incomplete", halfBody, StringComparison.Ordinal);
        using HttpResponseMessage badKey = await api.SendAsync(HttpMethod.Post, "/api/v1/acme-accounts", content: Json("""{"directoryUrl":"https://ca.test/dir","termsOfServiceAgreed":true,"eabKid":"kid","eabHmacKey":"%%not base64%%"}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badKey.StatusCode);
        Assert.DoesNotContain("not base64", await badKey.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using HttpResponseMessage list = await api.GetAsync("/api/v1/acme-accounts");
        Assert.Empty(await AdminApiHarness.ReadAsync(list, AdminJsonContext.Default.IReadOnlyListAcmeAccountSummary, Ct));
        using HttpResponseMessage unknown = await api.GetAsync($"/api/v1/acme-accounts/{AcmeAccountId.New()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using HttpResponseMessage deactivate = await api.SendAsync(HttpMethod.Delete, $"/api/v1/acme-accounts/{AcmeAccountId.New()}");
        Assert.Equal(HttpStatusCode.NotFound, deactivate.StatusCode);
    }

    [Fact]
    public async Task DnsCredentialsGoIntoTheVaultAndNeverComeBackInAnyResponseOrAuditEvent()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        string kind = DnsProviderKind.Cloudflare.ToName();
        List<string> bodies = [];

        using HttpResponseMessage created = await api.SendAsync(HttpMethod.Post, "/api/v1/dns-providers", content: Json($$"""{"kind":"{{kind}}","label":"cf","credential":"{{DnsToken}}"}"""));
        bodies.Add(await created.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DnsProviderSummary provider = System.Text.Json.JsonSerializer.Deserialize(bodies[0], AdminJsonContext.Default.DnsProviderSummary)!;
        Assert.Equal(DnsProviderKind.Cloudflare, provider.Kind);
        Assert.False(provider.CredentialSecretId.IsEmpty);

        using HttpResponseMessage list = await api.GetAsync("/api/v1/dns-providers");
        bodies.Add(await list.Content.ReadAsStringAsync(Ct));
        using HttpResponseMessage one = await api.GetAsync($"/api/v1/dns-providers/{provider.Id}");
        bodies.Add(await one.Content.ReadAsStringAsync(Ct));
        using HttpResponseMessage rotated = await api.SendAsync(HttpMethod.Put, $"/api/v1/dns-providers/{provider.Id}", content: Json($$"""{"credential":"{{RotatedToken}}"}"""));
        bodies.Add(await rotated.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Equal(provider.CredentialSecretId, System.Text.Json.JsonSerializer.Deserialize(bodies[^1], AdminJsonContext.Default.DnsProviderSummary)!.CredentialSecretId);

        using HttpResponseMessage audit = await api.GetAsync("/api/v1/audit?action=acme.*");
        string auditBody = await audit.Content.ReadAsStringAsync(Ct);
        bodies.Add(auditBody);
        Assert.Contains("acme.dns_provider.create", auditBody, StringComparison.Ordinal);
        Assert.Contains("acme.dns_provider.rotate", auditBody, StringComparison.Ordinal);
        using HttpResponseMessage openApi = await api.GetAsync("/api/v1/openapi.json", null);
        bodies.Add(await openApi.Content.ReadAsStringAsync(Ct));
        foreach (string body in bodies)
        {
            Assert.DoesNotContain(DnsToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain(RotatedToken, body, StringComparison.Ordinal);
        }

        using HttpResponseMessage bad = await api.SendAsync(HttpMethod.Post, "/api/v1/dns-providers", content: Json($$"""{"kind":"{{kind}}","label":"  ","credential":"{{DnsToken}}"}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.DoesNotContain(DnsToken, await bad.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        using HttpResponseMessage unknownKind = await api.SendAsync(HttpMethod.Post, "/api/v1/dns-providers", content: Json($$"""{"kind":"bogus","label":"x","credential":"{{DnsToken}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
        Assert.DoesNotContain(DnsToken, await unknownKind.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using HttpResponseMessage missingTest = await api.SendAsync(HttpMethod.Post, $"/api/v1/dns-providers/{DnsProviderId.New()}/test");
        Assert.Equal(HttpStatusCode.NotFound, missingTest.StatusCode);
        using HttpResponseMessage viewerTest = await api.SendAsync(HttpMethod.Post, $"/api/v1/dns-providers/{provider.Id}/test", "viewer");
        Assert.Equal(HttpStatusCode.Forbidden, viewerTest.StatusCode);
        using HttpResponseMessage operatorTest = await api.SendAsync(HttpMethod.Post, $"/api/v1/dns-providers/{provider.Id}/test", "operator");
        Assert.Equal(HttpStatusCode.Forbidden, operatorTest.StatusCode);
        using HttpResponseMessage deleted = await api.SendAsync(HttpMethod.Delete, $"/api/v1/dns-providers/{provider.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using HttpResponseMessage gone = await api.GetAsync($"/api/v1/dns-providers/{provider.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task TheAuditLogIsQueriedByFilterAndPagedAndItsChainVerifies()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        foreach (string label in new[] { "x1", "x2", "x3" })
        {
            using HttpResponseMessage created = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/hosts", ConfigTestSupport.RespondHost(label, $"{label}.test", "ok"), AdminJsonContext.Default.HostConfig, "admin");
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        async Task<Page<AuditEventSummary>> QueryAsync(string query) =>
            await AdminApiHarness.ReadAsync(await api.GetAsync("/api/v1/audit" + query, "auditor"), AdminJsonContext.Default.PageAuditEventSummary, Ct);

        Page<AuditEventSummary> all = await QueryAsync(string.Empty);
        Assert.True(all.Items.Count >= 3);
        ApiTokenId admin = api.TokenOf("admin");
        Page<AuditEventSummary> byAdmin = await QueryAsync($"?actor={admin}&action=config.*");
        Assert.Equal(3, byAdmin.Items.Count);
        Assert.All(byAdmin.Items, e => Assert.Equal(admin, e.Actor.Token));
        Assert.All(byAdmin.Items, e => Assert.Equal(Channel.Api, e.Channel));
        Assert.Empty((await QueryAsync($"?actor={api.TokenOf("viewer")}")).Items);
        Assert.Empty((await QueryAsync($"?actor={api.UserOf("admin")}&action=config.*")).Items); // the actor is the token that made the change, not its owner
        Assert.Empty((await QueryAsync("?to=2000-01-01T00:00:00Z")).Items);
        Assert.Equal(all.Items.Count, (await QueryAsync("?from=2000-01-01T00:00:00Z")).Items.Count);
        AuditEventSummary one = byAdmin.Items[0];
        Assert.Contains(one.Id, (await QueryAsync($"?target={one.Target}")).Items.Select(e => e.Id));

        List<AuditEventId> seen = [];
        string? cursor = null;
        do
        {
            Page<AuditEventSummary> page = await QueryAsync("?limit=2" + (cursor is null ? string.Empty : "&cursor=" + cursor));
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(e => e.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        Assert.Equal(all.Items.Select(e => e.Id), seen);
        Assert.Equal(seen.OrderByDescending(i => i), seen);

        foreach (string bad in new[] { "?from=yesterday", "?actor=nobody", "?action=A.B", "?action=a*b", "?target=%27%3B--", "?cursor=zzz", "?actor=" + new string('a', 200), "?action=" + new string('a', 200), "?target=" + new string('a', 200), "?from=2026-11-08", "?from=2026-11-09T00:00:00Z&to=2026-11-08T00:00:00Z" })
        {
            using HttpResponseMessage rejected = await api.GetAsync("/api/v1/audit" + bad, "auditor");
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        using HttpResponseMessage verify = await api.GetAsync("/api/v1/audit/verify", "auditor");
        AuditVerification verification = await AdminApiHarness.ReadAsync(verify, AdminJsonContext.Default.AuditVerification, Ct);
        Assert.True(verification.Intact);
        Assert.True(verification.VerifiedEvents >= all.Items.Count);
        Assert.Equal(64, verification.HeadHash!.Length);
    }

    [Fact]
    public async Task ASimulationRunsAgainstTheLiveConfigAndAgainstADraftWithoutChangingIt()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        using HttpResponseMessage live = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/hosts", ConfigTestSupport.RespondHost("sim", "sim.test", "hello"), AdminJsonContext.Default.HostConfig);
        ConfigRevisionId revision = (await AdminApiHarness.ReadAsync(live, AdminJsonContext.Default.ConfigCommandResult, Ct)).Revision!.Id;
        SimulationRequest request = new("http://draft.test/", "203.0.113.9");
        SimulationRequest known = new("http://sim.test/", "203.0.113.9");

        async Task<HttpResponseMessage> SimulateAsync(SimulationRequest r, SimulationTarget? target, string caller = "viewer") =>
            await api.SendJsonAsync(HttpMethod.Post, "/api/v1/simulate", new SimulateBody(r, target), AdminJsonContext.Default.SimulateBody, caller);

        using HttpResponseMessage liveKnown = await SimulateAsync(known, null);
        SimulationResult knownResult = await AdminApiHarness.ReadAsync(liveKnown, AdminJsonContext.Default.SimulationResult, Ct);
        Assert.Equal(SimulationStatus.Simulated, knownResult.Status);
        Assert.Equal(SimulationOutcomeKind.FixedAnswer, knownResult.Outcome!.Kind);

        using HttpResponseMessage liveUnknown = await SimulateAsync(request, SimulationTarget.Live);
        SimulationOutcomeKind unknownKind = (await AdminApiHarness.ReadAsync(liveUnknown, AdminJsonContext.Default.SimulationResult, Ct)).Outcome!.Kind;
        Assert.NotEqual(SimulationOutcomeKind.FixedAnswer, unknownKind);

        HostConfig draft = ConfigTestSupport.RespondHost("draft", "draft.test", "drafted");
        using HttpResponseMessage drafted = await SimulateAsync(request, new DraftCommandSimulationTarget(new UpsertHostCommand(draft)));
        Assert.Equal(SimulationOutcomeKind.FixedAnswer, (await AdminApiHarness.ReadAsync(drafted, AdminJsonContext.Default.SimulationResult, Ct)).Outcome!.Kind);
        using HttpResponseMessage hosts = await api.GetAsync("/api/v1/hosts");
        Assert.Single(await AdminApiHarness.ReadAsync(hosts, AdminJsonContext.Default.IReadOnlyListHostConfig, Ct));

        using HttpResponseMessage atRevision = await SimulateAsync(known, new RevisionSimulationTarget(revision));
        Assert.Equal(SimulationStatus.Simulated, (await AdminApiHarness.ReadAsync(atRevision, AdminJsonContext.Default.SimulationResult, Ct)).Status);
        using HttpResponseMessage noRevision = await SimulateAsync(known, new RevisionSimulationTarget(ConfigRevisionId.New()));
        Assert.Equal(HttpStatusCode.NotFound, noRevision.StatusCode);

        using HttpResponseMessage badRequest = await SimulateAsync(new SimulationRequest("not a url", "203.0.113.9"), null);
        ApiProblem bad = await AdminApiHarness.ProblemAsync(badRequest, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        Assert.NotEmpty(bad.Errors!);

        using HttpResponseMessage invalidDraft = await SimulateAsync(request, new DraftCommandSimulationTarget(new UpsertHostCommand(ConfigTestSupport.ProxyHost("bad", "bad.test", "nowhere"))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidDraft.StatusCode);
        Assert.NotEmpty((await AdminApiHarness.ProblemAsync(invalidDraft, Ct)).Errors!);

        using HttpResponseMessage auditor = await SimulateAsync(request, null, "auditor");
        Assert.Equal(HttpStatusCode.Forbidden, auditor.StatusCode);
    }
}
