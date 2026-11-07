using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault.Secrets;

/// <summary>
/// <see cref="ISecretStore"/>: seals each value under its own DEK (<see cref="EnvelopeCipher"/>) and keeps its metadata beside
/// it. There is no read here, and nothing else in the assembly reads through this class either: capabilities open secrets
/// through <see cref="SecretReader"/>.
/// </summary>
/// <remarks>
/// The value is sealed straight from the caller's buffer (an extra copy would only be one more thing to zero), and the
/// sealing's DEK and the encrypted value never share a buffer with it. Inputs are validated before anything is written, and
/// an error never carries a value.
/// </remarks>
internal sealed class SecretStore(VaultRuntime runtime, MacKeyCache macKeys) : ISecretStore
{
    /// <summary>The largest value accepted. API tokens, HMAC keys and TOTP seeds are tens of bytes; this is generous and bounds what a request can cost.</summary>
    public const int MaxValueBytes = 16 * 1024;

    public const int MaxLabelLength = 256;

    public const int MaxAllowedHosts = 32;

    public async ValueTask<SecretId> PutAsync(SecretKind kind, ReadOnlyMemory<byte> value, SecretOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var checkedOptions = Validate(kind, value.Length, options, existing: null);

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);

        var now = runtime.Time.GetUtcNow();
        var row = new SecretRow
        {
            Id = SecretId.New(),
            Kind = kind,
            Label = checkedOptions.Label,
            MacAlgorithm = checkedOptions.MacAlgorithm,
            AllowedHosts = [.. checkedOptions.AllowedHosts],
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        row.Sealed = EnvelopeCipher.Seal(runtime.Kek, value.Span, SecretReader.ContextOf(row, row.Version));

        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            context.Secrets.Add(row);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return row.Id;
    }

    public async ValueTask<SecretMetadata> ReplaceAsync(SecretId id, ReadOnlyMemory<byte> value, SecretOptions? options, CancellationToken cancellationToken)
    {
        if (value.Length is 0 or > MaxValueBytes)
        {
            throw ValueLength();
        }

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);

        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var row = await context.Secrets.SingleOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"There is no secret {id}.");

            var checkedOptions = options is null ? null : Validate(row.Kind, value.Length, options, row);
            var now = runtime.Time.GetUtcNow();
            var kek = runtime.Kek;

            if (SecretKinds.IsMac(row.Kind))
            {
                // The value being replaced stays verifiable for the grace window (IMacKey): keep its seal as it is.
                row.PreviousSealed = row.Sealed;
                row.PreviousVersion = row.Version;
                row.PreviousRetiredAt = now;
            }

            if (checkedOptions is not null)
            {
                row.Label = checkedOptions.Label;
                row.AllowedHosts = [.. checkedOptions.AllowedHosts];
            }

            // After the hosts are set: they are part of what the new value is sealed to (SecretReader.ContextOf).
            row.Version++;
            row.UpdatedAt = now;
            row.Sealed = EnvelopeCipher.Seal(kek, value.Span, SecretReader.ContextOf(row, row.Version));

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            macKeys.Invalidate(id);
            return SecretReader.ToMetadata(row);
        }
    }

    public async ValueTask<bool> DeleteAsync(SecretId id, CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);

        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var deleted = await context.Secrets.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
            macKeys.Invalidate(id);
            return deleted;
        }
    }

    public async ValueTask<SecretMetadata?> DescribeAsync(SecretId id, CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var row = await context.Secrets.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
            return row is null ? null : SecretReader.ToMetadata(row);
        }
    }

    /// <summary>The label and (normalised) hosts that fit the kind, or the <see cref="ArgumentException"/> that says why not.</summary>
    private static CheckedOptions Validate(SecretKind kind, int valueLength, SecretOptions options, SecretRow? existing)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Undefined secret kind.");
        }

        if (valueLength is 0 or > MaxValueBytes)
        {
            throw ValueLength();
        }

        if (options.Label is { Length: > MaxLabelLength })
        {
            throw new ArgumentException($"The label is at most {MaxLabelLength} characters.", nameof(options));
        }

        var isMac = SecretKinds.IsMac(kind);
        if (existing is null)
        {
            if (isMac != options.MacAlgorithm.HasValue)
            {
                throw new ArgumentException(
                    isMac ? $"A {kind.ToName()} secret needs a MAC algorithm." : $"A {kind.ToName()} secret has no MAC algorithm.", nameof(options));
            }
        }
        else if (options.MacAlgorithm is { } requested && requested != existing.MacAlgorithm)
        {
            throw new ArgumentException("The MAC algorithm is fixed when the secret is first stored.", nameof(options));
        }

        if (options.MacAlgorithm is { } algorithm && !Enum.IsDefined(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(options), algorithm, "Undefined MAC algorithm.");
        }

        var hosts = options.AllowedHosts is null
            ? []
            : options.AllowedHosts.Select(VaultHostNames.NormalizeAuthority).Distinct(StringComparer.Ordinal).ToList();
        if (kind == SecretKind.DnsApiToken)
        {
            if (hosts.Count == 0)
            {
                throw new ArgumentException("A DNS API token needs the hosts it may be sent to.", nameof(options));
            }
        }
        else if (hosts.Count != 0)
        {
            throw new ArgumentException($"Only a DNS API token has allowed hosts, not a {kind.ToName()} secret.", nameof(options));
        }

        if (hosts.Count > MaxAllowedHosts)
        {
            throw new ArgumentException($"At most {MaxAllowedHosts} allowed hosts.", nameof(options));
        }

        return new CheckedOptions(options.Label, options.MacAlgorithm, hosts);
    }

    private static ArgumentException ValueLength() =>
        new($"A secret value is 1 to {MaxValueBytes} bytes.", "value");

    private sealed record CheckedOptions(string? Label, MacAlgorithm? MacAlgorithm, IReadOnlyList<string> AllowedHosts);
}
