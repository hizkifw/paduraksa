using Paduraksa.Acme.Dns;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Issuance;

/// <summary>Wraps the fake signer to tell which account keys were created and deleted: a failed registration must leave no key behind.</summary>
internal sealed class TrackingSigner(IJwsSigner inner) : IJwsSigner
{
    public List<AcmeAccountId> Created { get; } = [];

    public List<AcmeAccountId> Deleted { get; } = [];

    public async ValueTask<AcmeAccountKeyInfo> CreateAccountKeyAsync(AcmeAccountId id, JwsAlgorithm algorithm, CancellationToken cancellationToken)
    {
        Created.Add(id);
        return await inner.CreateAccountKeyAsync(id, algorithm, cancellationToken);
    }

    public ValueTask<AcmeAccountKeyInfo?> GetAccountKeyAsync(AcmeAccountId id, CancellationToken cancellationToken) =>
        inner.GetAccountKeyAsync(id, cancellationToken);

    public ValueTask<JwsFlattened> SignAsync(AcmeAccountId id, ReadOnlyMemory<byte> protectedHeader, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        inner.SignAsync(id, protectedHeader, payload, cancellationToken);

    public async ValueTask<bool> DeleteAccountKeyAsync(AcmeAccountId id, CancellationToken cancellationToken)
    {
        Deleted.Add(id);
        return await inner.DeleteAccountKeyAsync(id, cancellationToken);
    }
}

/// <summary>A propagation check that answers as told and remembers what it was asked to look for.</summary>
internal sealed class FakePropagationCheck(DnsPropagationOutcome outcome = DnsPropagationOutcome.Propagated) : IDnsPropagationCheck
{
    public List<IReadOnlyList<DnsChallengeRecord>> Asked { get; } = [];

    public DnsPropagationOutcome Outcome { get; set; } = outcome;

    public Exception? Failure { get; set; }

    public bool AnyServerHadRecords { get; set; } = true;

    public ValueTask<DnsPropagationResult> WaitAsync(IReadOnlyList<DnsChallengeRecord> records, CancellationToken cancellationToken)
    {
        Asked.Add(records);
        return Failure is { } failure ? throw failure : ValueTask.FromResult(new DnsPropagationResult(Outcome, "fake", AnyServerHadRecords));
    }
}

/// <summary>A DNS provider that keeps its records in memory and can be told to fail.</summary>
internal sealed class FakeDnsProvider : IDnsProvider
{
    private readonly Lock _gate = new();
    private readonly List<(string Name, string Value)> _records = [];

    /// <summary>What was asked, in order: <c>create name value</c> or <c>delete name value</c>.</summary>
    public List<string> Calls { get; } = [];

    public Exception? CreateFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    /// <summary>How many deletes fail with <see cref="DeleteFailure"/> before they work again.</summary>
    public int DeleteFailureTimes { get; set; } = int.MaxValue;

    public DnsCredentialCheck Check { get; set; } = new(true);

    public IReadOnlyList<(string Name, string Value)> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    public ValueTask CreateTxtAsync(string recordName, string value, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Calls.Add($"create {recordName} {value}");
            if (CreateFailure is { } failure)
            {
                throw failure;
            }

            if (!_records.Contains((recordName, value)))
            {
                _records.Add((recordName, value));
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteTxtAsync(string recordName, string value, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Calls.Add($"delete {recordName} {value}");
            if (DeleteFailure is { } failure && DeleteFailureTimes-- > 0)
            {
                throw failure;
            }

            _records.Remove((recordName, value));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<DnsCredentialCheck> ValidateCredentialsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Check);
}

internal sealed class FakeDnsProviderFactory : IDnsProviderFactory
{
    public FakeDnsProvider Provider { get; } = new();

    public List<(DnsProviderKind Kind, SecretId Credential)> Created { get; } = [];

    public IDnsProvider Create(DnsProviderKind kind, SecretId credential)
    {
        Created.Add((kind, credential));
        return Provider;
    }
}

/// <summary>An <see cref="ISecretStore"/> that only remembers metadata: nothing here reads a value back, as with the vault.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<SecretId, SecretMetadata> _secrets = [];
    private readonly Dictionary<SecretId, byte[]> _values = [];

    /// <summary>The value a secret holds now (the fake keeps them so a test can see what was stored; the real vault has no such call).</summary>
    public byte[]? ValueOf(SecretId id) => _values.GetValueOrDefault(id);

    /// <summary>The secrets it holds now.</summary>
    public int Count => _secrets.Count;

    /// <summary>How many it was ever given.</summary>
    public int Puts { get; private set; }

    public ValueTask<SecretId> PutAsync(SecretKind kind, ReadOnlyMemory<byte> value, SecretOptions options, CancellationToken cancellationToken)
    {
        Puts++;
        var id = SecretId.New();
        var now = DateTimeOffset.UtcNow;
        _secrets[id] = new SecretMetadata(id, kind, options.Label, options.MacAlgorithm, options.AllowedHosts ?? [], now, now, 1);
        _values[id] = value.ToArray();
        return ValueTask.FromResult(id);
    }

    public ValueTask<SecretMetadata> EnsureAsync(SecretId id, SecretKind kind, ReadOnlyMemory<byte> value, SecretOptions options, CancellationToken cancellationToken)
    {
        if (!_secrets.TryGetValue(id, out var existing))
        {
            Puts++;
            var now = DateTimeOffset.UtcNow;
            existing = new SecretMetadata(id, kind, options.Label, options.MacAlgorithm, options.AllowedHosts ?? [], now, now, 1);
            _secrets[id] = existing;
        }

        return ValueTask.FromResult(existing);
    }

    public ValueTask<SecretMetadata> ReplaceAsync(SecretId id, ReadOnlyMemory<byte> value, SecretOptions? options, CancellationToken cancellationToken)
    {
        if (!_secrets.TryGetValue(id, out var existing))
        {
            throw new KeyNotFoundException($"There is no secret {id}.");
        }

        _values[id] = value.ToArray();
        _secrets[id] = existing with { Label = options?.Label ?? existing.Label, UpdatedAt = DateTimeOffset.UtcNow, Version = existing.Version + 1 };
        return ValueTask.FromResult(_secrets[id]);
    }

    public ValueTask<bool> DeleteAsync(SecretId id, CancellationToken cancellationToken)
    {
        _values.Remove(id);
        return ValueTask.FromResult(_secrets.Remove(id));
    }

    public ValueTask<SecretMetadata?> DescribeAsync(SecretId id, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_secrets.TryGetValue(id, out var metadata) ? metadata : null);
}

/// <summary>A handler in front of the fake CA, to act at an exact request: stall one, observe one, or "crash" after the CA has answered it.</summary>
internal sealed class HookHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>Runs before the CA sees the request.</summary>
    public Func<string, HttpRequestMessage, Task>? Before { get; set; }

    /// <summary>Runs after the CA answered, before the client sees it.</summary>
    public Func<string, HttpResponseMessage, Task>? After { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        if (Before is { } before)
        {
            await before(path, request);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (After is { } after)
        {
            await after(path, response);
        }

        return response;
    }
}
