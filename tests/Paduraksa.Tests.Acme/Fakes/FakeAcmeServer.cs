using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paduraksa.Tests.Acme.Fakes;

/// <summary>One request the fake received: what the client sent, decoded, and the HTTP headers that matter.</summary>
internal sealed record RecordedRequest(
    string Method, string Path, JsonElement? Header, string? Payload, string? Accept = null, string? UserAgent = null, string? AcceptLanguage = null)
{
    public string? HeaderString(string name) =>
        Header is { } h && h.TryGetProperty(name, out var v) ? v.GetString() : null;

    public bool HasHeader(string name) => Header is { } h && h.TryGetProperty(name, out _);

    public JsonNode? PayloadJson => string.IsNullOrEmpty(Payload) ? null : JsonNode.Parse(Payload);
}

/// <summary>
/// A scripted in-process ACME CA. It verifies every JWS the client sends (signature against the account's JWK, nonce
/// freshness and single use, <c>url</c> equal to the raw request URL, <c>jwk</c> xor <c>kid</c>, an empty payload for
/// POST-as-GET, EAB), then answers like a minimal CA: accounts, orders with one authorization per identifier, challenges,
/// finalize, certificate download with an alternate chain, ARI, revocation. Knobs on the instance make it slow, picky or
/// failing. All state is behind one lock, so parallel requests are safe.
/// </summary>
internal sealed class FakeAcmeServer : HttpMessageHandler
{
    public static readonly Uri Base = new("https://acme.test/");

    // The chains are the shape of Let's Encrypt's: the default one ends in a cross-signed root, the alternate stops at E5.
    private static readonly (string Default, string Alternate) Chains = MakeChains();

    private readonly Lock _gate = new();
    private readonly HashSet<string> _nonces = [];
    private readonly Dictionary<string, JsonElement> _accounts = [];
    private readonly Dictionary<string, Queue<Func<HttpRequestMessage, HttpResponseMessage>>> _injected = [];
    private readonly Dictionary<string, FakeOrder> _orders = [];
    private readonly Dictionary<string, FakeAuthorization> _authorizations = [];
    private readonly List<RecordedRequest> _requests = [];
    private int _counter;
    private int _nonceRequests;

    // ---- Knobs ----
    public bool ExternalAccountRequired { get; set; }

    public string EabKeyId { get; set; } = "kid-1";

    public byte[] EabSecret { get; set; } = RandomNumberGenerator.GetBytes(32);

    public bool AdvertiseRenewalInfo { get; set; } = true;

    /// <summary>How many polls of an authorization answer pending (after its challenge was answered) before it settles.</summary>
    public int AuthorizationPollsBeforePass { get; set; } = 2;

    /// <summary>When set, the authorization settles as invalid (with a problem on its challenge) instead of valid, and so does its order.</summary>
    public bool FailValidation { get; set; }

    public int? RetryAfterSecondsWhilePending { get; set; }

    public int OrderPollsWhileProcessing { get; set; } = 1;

    public Dictionary<string, string> RenewalInfoBodies { get; } = [];

    public int? RenewalInfoRetryAfterSeconds { get; set; }

    /// <summary>
    /// A CA that reuses valid authorizations (Let's Encrypt does for 30 days): a new order for an identifier that already has a valid
    /// authorization gets that one, so it comes back already valid and needs no challenge.
    /// </summary>
    public bool ReuseValidAuthorizations { get; set; }

    /// <summary>The first finalize finds the order already finalized by someone else (a race): the CA answers <c>orderNotReady</c> while the order is processing.</summary>
    public bool RaceFinalizeOnce { get; set; }

    /// <summary>When set, <c>Link: rel="alternate"</c> points the default chain at the other one.</summary>
    public bool OfferAlternateChain { get; set; } = true;

    // ---- Observations ----
    public List<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public int NonceRequests
    {
        get
        {
            lock (_gate)
            {
                return _nonceRequests;
            }
        }
    }

    public static string DefaultChainPem => Chains.Default;

    public static string AlternateChainPem => Chains.Alternate;

    public static Uri Url(string path) => new(Base, path);

    public IEnumerable<RecordedRequest> To(string path) => Requests.Where(r => r.Path == path);

    /// <summary>Makes every nonce the server handed out so far stale, as a CA that restarted would.</summary>
    public void ForgetNonces()
    {
        lock (_gate)
        {
            _nonces.Clear();
        }
    }

    /// <summary>The next request to <paramref name="path"/> gets this response instead of the normal one.</summary>
    public void Inject(string path, Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        lock (_gate)
        {
            if (!_injected.TryGetValue(path, out var queue))
            {
                _injected[path] = queue = new();
            }

            queue.Enqueue(response);
        }
    }

    public byte[]? CsrOf(int orderNumber)
    {
        lock (_gate)
        {
            return _orders.Values.ElementAtOrDefault(orderNumber)?.Csr;
        }
    }

    public IReadOnlyList<string> TokensOf(int orderNumber)
    {
        lock (_gate)
        {
            return [.. _orders.Values.ElementAt(orderNumber).AuthorizationIds.Select(a => _authorizations[a].Token)];
        }
    }

    public string? ReplacesOf(int orderNumber)
    {
        lock (_gate)
        {
            return _orders.Values.ElementAt(orderNumber).Replaces;
        }
    }

    /// <summary>The path of the <paramref name="orderNumber"/>th order (<c>order/7</c>), for injecting a response to it.</summary>
    public string OrderPath(int orderNumber)
    {
        lock (_gate)
        {
            return "order/" + _orders.Values.ElementAt(orderNumber).Id;
        }
    }

    /// <summary>Makes the CA report the order as invalid from now on.</summary>
    public void InvalidateOrder(int orderNumber)
    {
        lock (_gate)
        {
            _orders.Values.ElementAt(orderNumber).ForcedStatus = "invalid";
        }
    }

    /// <summary>Deactivates every authorization of the order (as an expiry or a deactivation would).</summary>
    public void DeactivateAuthorizationsOf(int orderNumber)
    {
        lock (_gate)
        {
            foreach (var id in _orders.Values.ElementAt(orderNumber).AuthorizationIds)
            {
                _authorizations[id].Deactivate();
            }
        }
    }

    public int OrderCount
    {
        get
        {
            lock (_gate)
            {
                return _orders.Count;
            }
        }
    }

    public static HttpResponseMessage Problem(HttpStatusCode status, string type, string detail, string? retryAfter = null, JsonArray? subproblems = null)
    {
        var body = new JsonObject { ["type"] = type, ["detail"] = detail, ["status"] = (int)status };
        if (subproblems is not null)
        {
            body["subproblems"] = subproblems;
        }

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/problem+json"),
        };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            HttpResponseMessage response;
            if (_injected.TryGetValue(path, out var queue) && queue.TryDequeue(out var injected))
            {
                _requests.Add(Record(request, path, null, null));
                response = injected(request);
            }
            else
            {
                response = Route(request, path);
            }

            // The directory carries no nonce, so the first signed request has to ask newNonce, as with CAs that do the same.
            if (path != "directory" && !response.Headers.Contains("Replay-Nonce"))
            {
                var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12));
                _nonces.Add(nonce);
                response.Headers.Add("Replay-Nonce", nonce);
            }

            return Task.FromResult(response);
        }
    }

    private static RecordedRequest Record(HttpRequestMessage request, string path, JsonElement? header, string? payload) => new(
        request.Method.Method,
        path,
        header,
        payload,
        request.Headers.Accept.ToString(),
        request.Headers.UserAgent.ToString(),
        request.Headers.AcceptLanguage.ToString());

    private HttpResponseMessage Route(HttpRequestMessage request, string path)
    {
        if (request.Method == HttpMethod.Get && path == "directory")
        {
            _requests.Add(Record(request, path, null, null));
            return Json(HttpStatusCode.OK, DirectoryJson());
        }

        if (path == "new-nonce")
        {
            _nonceRequests++;
            _requests.Add(Record(request, path, null, null));
            return new HttpResponseMessage(request.Method == HttpMethod.Head ? HttpStatusCode.OK : HttpStatusCode.NoContent);
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("renewal-info/", StringComparison.Ordinal))
        {
            _requests.Add(Record(request, path, null, null));
            if (!RenewalInfoBodies.TryGetValue(path["renewal-info/".Length..], out var body))
            {
                return Problem(HttpStatusCode.NotFound, "urn:ietf:params:acme:error:malformed", "no such certificate");
            }

            var ok = Json(HttpStatusCode.OK, body);
            if (RenewalInfoRetryAfterSeconds is { } seconds)
            {
                ok.Headers.TryAddWithoutValidation("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
            }

            return ok;
        }

        if (request.Method != HttpMethod.Post)
        {
            return Problem(HttpStatusCode.MethodNotAllowed, "urn:ietf:params:acme:error:malformed", "POST only");
        }

        var (jws, failure) = VerifyJws(request, path);
        if (failure is not null)
        {
            return failure;
        }

        var payload = jws!.Payload;
        return path switch
        {
            "new-account" => NewAccount(jws),
            "new-order" => NewOrder(payload),
            "revoke-cert" => Json(HttpStatusCode.OK, "{}"),
            _ when path.StartsWith("acct/", StringComparison.Ordinal) => Account(payload),
            _ when path.StartsWith("authz/", StringComparison.Ordinal) => Authorization(path, payload),
            _ when path.StartsWith("chall/", StringComparison.Ordinal) => Challenge(path, payload),
            _ when path.StartsWith("order/", StringComparison.Ordinal) => RequirePostAsGet(payload) ?? OrderStatus(path),
            _ when path.StartsWith("finalize/", StringComparison.Ordinal) => Finalize(path, payload),
            _ when path.StartsWith("cert/", StringComparison.Ordinal) => RequirePostAsGet(payload) ?? Certificate(path),
            _ => Problem(HttpStatusCode.NotFound, "urn:ietf:params:acme:error:malformed", "unknown resource"),
        };
    }

    private static HttpResponseMessage? RequirePostAsGet(string payload) =>
        payload.Length == 0
            ? null
            : Problem(HttpStatusCode.BadRequest, "urn:ietf:params:acme:error:malformed", "POST-as-GET needs an empty payload");

    private sealed record Jws(JsonElement Header, string Payload);

    private (Jws? Jws, HttpResponseMessage? Failure) VerifyJws(HttpRequestMessage request, string path)
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        var protectedPart = body.RootElement.GetProperty("protected").GetString()!;
        var payloadPart = body.RootElement.GetProperty("payload").GetString()!;
        var signature = Base64Url.DecodeFromChars(body.RootElement.GetProperty("signature").GetString()!);
        var header = JsonDocument.Parse(Base64Url.DecodeFromChars(protectedPart)).RootElement.Clone();
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(payloadPart));
        _requests.Add(Record(request, path, header, payload));

        static HttpResponseMessage Reject(string type, string detail) =>
            Problem(type.EndsWith("badNonce", StringComparison.Ordinal) ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized, type, detail);

        if (request.Content.Headers.ContentType?.MediaType != "application/jose+json")
        {
            return (null, Reject("urn:ietf:params:acme:error:malformed", "content type"));
        }

        if (header.GetProperty("url").GetString() != request.RequestUri!.OriginalString)
        {
            return (null, Reject("urn:ietf:params:acme:error:unauthorized", "url header does not match the request URL"));
        }

        if (!header.TryGetProperty("nonce", out var nonce) || !_nonces.Remove(nonce.GetString()!))
        {
            return (null, Reject("urn:ietf:params:acme:error:badNonce", "stale or reused nonce"));
        }

        var hasJwk = header.TryGetProperty("jwk", out var jwk);
        var hasKid = header.TryGetProperty("kid", out var kid);
        if (hasJwk == hasKid)
        {
            return (null, Reject("urn:ietf:params:acme:error:malformed", "exactly one of jwk and kid is required"));
        }

        if (hasKid && !_accounts.TryGetValue(kid.GetString()!, out jwk))
        {
            return (null, Reject("urn:ietf:params:acme:error:accountDoesNotExist", "unknown kid"));
        }

        if (hasJwk && path != "new-account")
        {
            return (null, Reject("urn:ietf:params:acme:error:malformed", "jwk is only for newAccount"));
        }

        var input = Encoding.ASCII.GetBytes($"{protectedPart}.{payloadPart}");
        return Verify(header.GetProperty("alg").GetString()!, jwk, input, signature)
            ? (new Jws(header, payload), null)
            : (null, Reject("urn:ietf:params:acme:error:malformed", "bad signature"));
    }

    private static bool Verify(string alg, JsonElement jwk, byte[] input, byte[] signature)
    {
        switch (alg)
        {
            case "ES256":
            case "ES384":
                var (curve, hash, crv) = alg == "ES256"
                    ? (ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256, "P-256")
                    : (ECCurve.NamedCurves.nistP384, HashAlgorithmName.SHA384, "P-384");
                if (jwk.GetProperty("kty").GetString() != "EC" || jwk.GetProperty("crv").GetString() != crv)
                {
                    return false;
                }

                using (var ecdsa = ECDsa.Create(new ECParameters
                {
                    Curve = curve,
                    Q = new ECPoint
                    {
                        X = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!),
                        Y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!),
                    },
                }))
                {
                    return ecdsa.VerifyData(input, signature, hash);
                }

            case "RS256":
                using (var rsa = RSA.Create(new RSAParameters
                {
                    Modulus = Base64Url.DecodeFromChars(jwk.GetProperty("n").GetString()!),
                    Exponent = Base64Url.DecodeFromChars(jwk.GetProperty("e").GetString()!),
                }))
                {
                    return jwk.GetProperty("kty").GetString() == "RSA"
                        && rsa.VerifyData(input, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }

            default:
                return false;
        }
    }

    /// <summary>The members RFC 7638 puts in a thumbprint, in order: identifies a key whatever its type.</summary>
    private static string KeyId(JsonElement jwk) => jwk.GetProperty("kty").GetString() == "RSA"
        ? $"RSA.{jwk.GetProperty("e").GetString()}.{jwk.GetProperty("n").GetString()}"
        : $"EC.{jwk.GetProperty("crv").GetString()}.{jwk.GetProperty("x").GetString()}.{jwk.GetProperty("y").GetString()}";

    private string DirectoryJson()
    {
        var directory = new JsonObject
        {
            ["newNonce"] = Url("new-nonce").AbsoluteUri,
            ["newAccount"] = Url("new-account").AbsoluteUri,
            ["newOrder"] = Url("new-order").AbsoluteUri,
            ["revokeCert"] = Url("revoke-cert").AbsoluteUri,
            ["keyChange"] = Url("key-change").AbsoluteUri,
            ["meta"] = new JsonObject
            {
                ["termsOfService"] = Url("terms").AbsoluteUri,
                ["externalAccountRequired"] = ExternalAccountRequired,
                ["profiles"] = new JsonObject { ["classic"] = "The default", ["shortlived"] = "Six days" },
            },
        };
        if (AdvertiseRenewalInfo)
        {
            directory["renewalInfo"] = Url("renewal-info").AbsoluteUri;
        }

        return directory.ToJsonString();
    }

    private HttpResponseMessage NewAccount(Jws jws)
    {
        var payload = JsonNode.Parse(jws.Payload)!.AsObject();
        var jwk = jws.Header.GetProperty("jwk");
        var existing = _accounts.FirstOrDefault(a => KeyId(a.Value) == KeyId(jwk));
        if (existing.Key is not null)
        {
            return AccountResponse(HttpStatusCode.OK, existing.Key, null);
        }

        if (payload["onlyReturnExisting"]?.GetValue<bool>() == true)
        {
            return Problem(HttpStatusCode.BadRequest, "urn:ietf:params:acme:error:accountDoesNotExist", "no account for this key");
        }

        if (ExternalAccountRequired && !VerifyEab(payload["externalAccountBinding"], jwk, out var eabFailure))
        {
            return Problem(HttpStatusCode.Unauthorized, "urn:ietf:params:acme:error:externalAccountRequired", eabFailure);
        }

        var url = Url($"acct/{++_counter}").AbsoluteUri;
        _accounts[url] = jwk.Clone();
        return AccountResponse(HttpStatusCode.Created, url, payload["contact"]?.DeepClone());
    }

    private bool VerifyEab(JsonNode? binding, JsonElement accountJwk, out string failure)
    {
        failure = "missing or invalid externalAccountBinding";
        if (binding is null)
        {
            return false;
        }

        var protectedPart = binding["protected"]!.GetValue<string>();
        var payloadPart = binding["payload"]!.GetValue<string>();
        using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(protectedPart));
        using var inner = JsonDocument.Parse(Base64Url.DecodeFromChars(payloadPart));
        var tag = Base64Url.DecodeFromChars(binding["signature"]!.GetValue<string>());
        var expected = HMACSHA256.HashData(EabSecret, Encoding.ASCII.GetBytes($"{protectedPart}.{payloadPart}"));
        return header.RootElement.GetProperty("alg").GetString() == "HS256"
            && header.RootElement.GetProperty("kid").GetString() == EabKeyId
            && header.RootElement.GetProperty("url").GetString() == Url("new-account").OriginalString
            && !header.RootElement.TryGetProperty("nonce", out _)
            && KeyId(inner.RootElement) == KeyId(accountJwk)
            && CryptographicOperations.FixedTimeEquals(expected, tag);
    }

    private static HttpResponseMessage AccountResponse(HttpStatusCode status, string url, JsonNode? contact)
    {
        var body = new JsonObject { ["status"] = "valid", ["orders"] = url + "/orders" };
        if (contact is not null)
        {
            body["contact"] = contact;
        }

        var response = Json(status, body.ToJsonString());
        response.Headers.Location = new Uri(url);
        return response;
    }

    private static HttpResponseMessage Account(string payload)
    {
        var body = new JsonObject { ["status"] = "valid" };
        if (payload.Length > 0)
        {
            var request = JsonNode.Parse(payload)!.AsObject();
            if (request["contact"] is { } contact)
            {
                body["contact"] = contact.DeepClone();
            }

            if (request["status"]?.GetValue<string>() == "deactivated")
            {
                body["status"] = "deactivated";
            }
        }

        return Json(HttpStatusCode.OK, body.ToJsonString());
    }

    private HttpResponseMessage NewOrder(string payload)
    {
        var request = JsonNode.Parse(payload)!.AsObject();
        var id = (++_counter).ToString(CultureInfo.InvariantCulture);
        var order = new FakeOrder(id, request["identifiers"]!.AsArray().Select(i => i!.DeepClone()).ToList(), request["profile"]?.GetValue<string>(), request["replaces"]?.GetValue<string>());
        foreach (var identifier in order.Identifiers)
        {
            var reusable = ReuseValidAuthorizations
                ? _authorizations.Values.FirstOrDefault(a => a.Status == "valid" && a.Identifier.ToJsonString() == identifier.ToJsonString())
                : null;
            if (reusable is not null)
            {
                order.AuthorizationIds.Add(reusable.Id);
                continue;
            }

            var authzId = (++_counter).ToString(CultureInfo.InvariantCulture);
            _authorizations[authzId] = new FakeAuthorization(authzId, identifier, AuthorizationPollsBeforePass, FailValidation);
            order.AuthorizationIds.Add(authzId);
        }

        _orders[id] = order;
        var response = Json(HttpStatusCode.Created, OrderJson(order));
        response.Headers.Location = Url($"order/{id}");
        return response;
    }

    private HttpResponseMessage OrderStatus(string path)
    {
        var order = _orders[path["order/".Length..]];
        var response = Json(HttpStatusCode.OK, OrderJson(order));
        if (order.Status(_authorizations) == "processing" && RetryAfterSecondsWhilePending is { } seconds)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
        }

        if (order.Finalized && order.ProcessingPollsLeft > 0)
        {
            order.ProcessingPollsLeft--;
        }

        return response;
    }

    private string OrderJson(FakeOrder order)
    {
        var status = order.Status(_authorizations);
        var body = new JsonObject
        {
            ["status"] = status,
            ["identifiers"] = new JsonArray(order.Identifiers.Select(i => i.DeepClone()).ToArray()),
            ["authorizations"] = new JsonArray(order.AuthorizationIds.Select(a => (JsonNode)Url($"authz/{a}").AbsoluteUri).ToArray()),
            ["finalize"] = Url($"finalize/{order.Id}").AbsoluteUri,
        };
        if (order.Profile is not null)
        {
            body["profile"] = order.Profile;
        }

        if (order.Replaces is not null)
        {
            body["replaces"] = order.Replaces;
        }

        if (status == "valid")
        {
            body["certificate"] = Url($"cert/{order.Id}").AbsoluteUri;
        }

        if (status == "invalid")
        {
            body["error"] = new JsonObject
            {
                ["type"] = "urn:ietf:params:acme:error:unauthorized",
                ["detail"] = "One or more authorizations failed",
            };
        }

        return body.ToJsonString();
    }

    private HttpResponseMessage Authorization(string path, string payload)
    {
        var authz = _authorizations[path["authz/".Length..]];
        if (payload.Length > 0)
        {
            if (JsonNode.Parse(payload)!["status"]?.GetValue<string>() != "deactivated")
            {
                return Problem(HttpStatusCode.BadRequest, "urn:ietf:params:acme:error:malformed", "only deactivation may be posted");
            }

            authz.Deactivate();
            return Json(HttpStatusCode.OK, AuthorizationJson(authz));
        }

        authz.Observe();
        var response = Json(HttpStatusCode.OK, AuthorizationJson(authz));
        if (authz.Status == "pending" && authz.Responded && RetryAfterSecondsWhilePending is { } seconds)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
        }

        return response;
    }

    private static string AuthorizationJson(FakeAuthorization authz)
    {
        var identifier = authz.Identifier.AsObject();
        var value = identifier["value"]!.GetValue<string>();
        var wildcard = value.StartsWith("*.", StringComparison.Ordinal);
        var body = new JsonObject
        {
            ["identifier"] = new JsonObject { ["type"] = identifier["type"]!.GetValue<string>(), ["value"] = wildcard ? value[2..] : value },
            ["status"] = authz.Status,
            ["challenges"] = new JsonArray(
                ChallengeJson("http-01", authz, "http"),
                ChallengeJson("dns-01", authz, "dns")),
        };
        if (wildcard)
        {
            body["wildcard"] = true;
        }

        return body.ToJsonString();
    }

    private static JsonObject ChallengeJson(string type, FakeAuthorization authz, string suffix)
    {
        var status = !authz.Responded ? "pending" : authz.Status == "pending" ? "processing" : authz.Status;
        var challenge = new JsonObject { ["type"] = type, ["url"] = Url($"chall/{authz.Id}-{suffix}").AbsoluteUri, ["status"] = status, ["token"] = authz.Token };
        if (authz.Status == "invalid")
        {
            challenge["status"] = "invalid";
            challenge["error"] = new JsonObject
            {
                ["type"] = "urn:ietf:params:acme:error:unauthorized",
                ["detail"] = "Invalid response from http://example.com/.well-known/acme-challenge/x",
                ["status"] = 403,
            };
        }

        return challenge;
    }

    private HttpResponseMessage Challenge(string path, string payload)
    {
        if (payload != "{}")
        {
            return Problem(HttpStatusCode.BadRequest, "urn:ietf:params:acme:error:malformed", "challenge payload must be {}");
        }

        var name = path["chall/".Length..];
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        var authz = _authorizations[name[..dash]];
        authz.Responded = true;
        var suffix = name[(dash + 1)..];
        return Json(HttpStatusCode.OK, ChallengeJson(suffix == "dns" ? "dns-01" : "http-01", authz, suffix).ToJsonString());
    }

    private HttpResponseMessage Finalize(string path, string payload)
    {
        var order = _orders[path["finalize/".Length..]];
        if (RaceFinalizeOnce && order.Status(_authorizations) == "ready")
        {
            RaceFinalizeOnce = false;
            order.Finalized = true;
            order.ProcessingPollsLeft = OrderPollsWhileProcessing;
        }

        if (order.Status(_authorizations) != "ready")
        {
            return Problem(HttpStatusCode.Forbidden, "urn:ietf:params:acme:error:orderNotReady", "the order is not ready");
        }

        var csr = JsonNode.Parse(payload)!["csr"]!.GetValue<string>();
        order.Csr = Base64Url.DecodeFromChars(csr);
        order.Finalized = true;
        order.ProcessingPollsLeft = OrderPollsWhileProcessing;
        var response = Json(HttpStatusCode.OK, OrderJson(order));
        if (RetryAfterSecondsWhilePending is { } seconds)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
        }

        return response;
    }

    private HttpResponseMessage Certificate(string path)
    {
        var alternate = path.EndsWith("-alt", StringComparison.Ordinal);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(alternate ? Chains.Alternate : Chains.Default, Encoding.ASCII, "application/pem-certificate-chain"),
        };
        if (!alternate && OfferAlternateChain)
        {
            response.Headers.TryAddWithoutValidation("Link", $"<{Url(path + "-alt").AbsoluteUri}>;rel=\"alternate\", <{Url("directory").AbsoluteUri}>;rel=\"index\"");
        }

        return response;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Default: [leaf, E5 (issuer "ISRG Root X2"), "ISRG Root X2" cross-signed by "ISRG Root X1"]; alternate: [leaf, E5].
    /// The top issuer is "ISRG Root X1" for the first and "ISRG Root X2" for the second, while "ISRG Root X2" also appears
    /// as E5's issuer in the first: a matcher that looks at any certificate's issuer cannot tell them apart.
    /// </summary>
    private static (string Default, string Alternate) MakeChains()
    {
        var now = DateTimeOffset.UtcNow;
        using var x1Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var x2Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var e5Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        using var x1 = CaRequest("ISRG Root X1", x1Key).CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        using var x2 = CaRequest("ISRG Root X2", x2Key).CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        using var x2Cross = CaRequest("ISRG Root X2", x2Key).Create(x1, now.AddDays(-1), now.AddDays(30), RandomNumberGenerator.GetBytes(8));
        using var e5Public = CaRequest("E5", e5Key).Create(x2, now.AddDays(-1), now.AddDays(30), RandomNumberGenerator.GetBytes(8));
        using var e5 = e5Public.CopyWithPrivateKey(e5Key);
        using var leaf = new CertificateRequest("CN=leaf.example", leafKey, HashAlgorithmName.SHA256)
            .Create(e5, now.AddDays(-1), now.AddDays(10), RandomNumberGenerator.GetBytes(8));

        var leafPem = leaf.ExportCertificatePem();
        var e5Pem = e5.ExportCertificatePem();
        return ($"{leafPem}\n{e5Pem}\n{x2Cross.ExportCertificatePem()}\n", $"{leafPem}\n{e5Pem}\n");
    }

    private static CertificateRequest CaRequest(string commonName, ECDsa key)
    {
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request;
    }

    private sealed class FakeOrder(string id, List<JsonNode> identifiers, string? profile, string? replaces)
    {
        public string Id { get; } = id;

        public List<JsonNode> Identifiers { get; } = identifiers;

        public string? Profile { get; } = profile;

        public string? Replaces { get; } = replaces;

        public List<string> AuthorizationIds { get; } = [];

        public bool Finalized { get; set; }

        public int ProcessingPollsLeft { get; set; }

        public byte[]? Csr { get; set; }

        public string? ForcedStatus { get; set; }

        public string Status(Dictionary<string, FakeAuthorization> authorizations) =>
            ForcedStatus
            ?? (AuthorizationIds.Any(a => authorizations[a].Status == "invalid") ? "invalid"
                : !Finalized ? AuthorizationIds.All(a => authorizations[a].Status == "valid") ? "ready" : "pending"
                : ProcessingPollsLeft > 0 ? "processing" : "valid");
    }

    private sealed class FakeAuthorization(string id, JsonNode identifier, int pollsBeforePass, bool failValidation)
    {
        private int _pollsLeft = pollsBeforePass;

        public string Id { get; } = id;

        public JsonNode Identifier { get; } = identifier;

        public string Token { get; } = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

        public bool Responded { get; set; }

        public string Status { get; private set; } = "pending";

        public void Observe()
        {
            if (Responded && Status == "pending" && _pollsLeft-- <= 0)
            {
                Status = failValidation ? "invalid" : "valid";
            }
        }

        public void Deactivate() => Status = "deactivated";
    }
}
