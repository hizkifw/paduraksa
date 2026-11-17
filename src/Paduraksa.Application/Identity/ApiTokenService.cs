using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Core.Options;
using Paduraksa.Data;
using Paduraksa.Data.Identity;

namespace Paduraksa.Application.Identity;

/// <summary>The outcome of issuing, changing or revoking an API token.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Issued">The new token with its secret, shown once, for an issue.</param>
/// <param name="Token">The token's public facts, for a change or a revoke.</param>
/// <param name="Problem">Why it was refused (for <see cref="IdentityStatus.Invalid"/>), safe to show.</param>
public sealed record ApiTokenResult(IdentityStatus Status, IssuedApiToken? Issued = null, ApiTokenSummary? Token = null, string? Problem = null)
{
    public bool Succeeded => Status == IdentityStatus.Succeeded;
}

/// <param name="Status">How it ended.</param>
/// <param name="Tokens">The tokens, newest first; never a secret.</param>
public sealed record ApiTokenListResult(IdentityStatus Status, IReadOnlyList<ApiTokenSummary> Tokens);

/// <summary>The identity a valid token carries.</summary>
public sealed record ApiTokenPrincipal(ApiTokenId TokenId, UserId UserId, IReadOnlySet<Permission> Permissions);

/// <summary>The outcome of <see cref="ApiTokenService.VerifyAsync"/>.</summary>
/// <param name="Principal">The identity of a valid token; null for anything else (the reasons are not told apart).</param>
public sealed record ApiTokenVerification(ApiTokenPrincipal? Principal)
{
    public bool IsValid => Principal is not null;
}

/// <summary>
/// API tokens: <c>pdk_&lt;id&gt;_&lt;secret&gt;_&lt;checksum&gt;</c>, issued to a signed-in user with scopes and an optional expiry,
/// shown once, stored as <c>SHA-256(secret)</c>. A token acts as its owner with <i>at most</i> its scopes: what it may do is the
/// intersection of its scopes and the owner's current permissions, evaluated on every use, so demoting, disabling or deleting
/// the owner takes effect at once.
/// </summary>
/// <remarks>
/// Verification parses the string (a wrong prefix, length or checksum never reaches the database), looks the token up by ID,
/// compares the hash in constant time (also when the ID is unknown, so the time does not tell), then checks revoked, disabled,
/// expiry and owner. The last-used time is written at most once per <see cref="IdentityOptions.TouchInterval"/>. A token cannot issue tokens:
/// persistence of access is a decision for a signed-in person.
/// </remarks>
public sealed class ApiTokenService
{
    /// <summary>The most live (not revoked) tokens a user may hold; a disabled one counts, since it can come back.</summary>
    public const int MaxTokensPerUser = 100;

    /// <summary>
    /// Whether a token may carry <paramref name="permission"/> as a scope. Managing tokens and sessions is for a person at a session
    /// (the API refuses a token caller on those endpoints too): a token that could carry the scope would one day be handed a way to
    /// keep itself alive, so the scope is refused as unknown, not left to the picker and the endpoint gates.
    /// </summary>
    public static bool IsAssignableScope(Permission permission) =>
        Enum.IsDefined(permission) && permission is not (Permission.TokensOwn or Permission.TokensAll or Permission.SessionsAll);

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly TimeProvider _time;
    private readonly LiveOptions<IdentityOptions> _live;

    // Read on every use: the server settings may replace them while the process runs.
    private IdentityOptions Options => _live.Current;
    private readonly IdentityAudit _audit;
    private readonly IPermissionEvaluator _permissions;

    internal ApiTokenService(
        IDbContextFactory<ConfigDbContext> contexts, TimeProvider time, LiveOptions<IdentityOptions> options, IdentityAudit audit, IPermissionEvaluator permissions)
    {
        _contexts = contexts;
        _time = time;
        _live = options;
        _audit = audit;
        _permissions = permissions;
    }

    /// <summary>
    /// Issues a token for the calling user. Needs <see cref="Permission.TokensOwn"/>, and every scope must be a permission the user
    /// holds. The result carries the secret once.
    /// </summary>
    public async Task<ApiTokenResult> IssueAsync(CommandOrigin origin, IssueApiTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(request);
        var held = await _permissions.GetPermissionsAsync(origin.Actor, cancellationToken).ConfigureAwait(false);
        if (origin.Actor.UserId is not { } owner || !held.Contains(Permission.TokensOwn))
        {
            await _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(origin, "token.issue", AuditOutcome.Denied)).ConfigureAwait(false);
            return new ApiTokenResult(IdentityStatus.Denied);
        }

        var name = IdentityMapping.Clean(request.Name, IdentityConfiguration.MaxTokenNameLength);
        var scopes = (request.Scopes ?? []).Distinct().Order().ToList();
        var now = _time.GetUtcNow();
        var problem = name is null ? "A token needs a name."
            : scopes.Count == 0 ? "A token needs at least one scope."
            : scopes.Exists(s => !IsAssignableScope(s)) ? "Unknown scope."
            : scopes.Exists(s => !held.Contains(s)) ? "A token cannot have a scope its owner lacks."
            : request.ExpiresAt is { } expiry && expiry <= now ? "The expiry must be in the future."
            : null;
        if (problem is not null)
        {
            return new ApiTokenResult(IdentityStatus.Invalid, Problem: problem);
        }

        var secret = RandomNumberGenerator.GetBytes(ApiTokenFormat.SecretBytes);
        var row = new ApiTokenRow
        {
            Id = ApiTokenId.New(),
            UserId = owner,
            Name = name!,
            Scopes = IdentityMapping.FormatScopes(scopes),
            SecretHash = SHA256.HashData(secret),
            ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
            CreatedAt = now,
        };

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                if (await context.ApiTokens.CountAsync(t => t.UserId == owner && t.RevokedAt == null, cancellationToken).ConfigureAwait(false) >= MaxTokensPerUser)
                {
                    return new ApiTokenResult(IdentityStatus.Invalid, Problem: $"At most {MaxTokensPerUser} live tokens per user: revoke some first.");
                }

                context.ApiTokens.Add(row);
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(origin, "token.issue", AuditOutcome.Success, row.Id.ToString(), w =>
                    {
                        w.WriteString("name", row.Name);
                        w.WriteString("scopes", row.Scopes);
                    }),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new ApiTokenResult(IdentityStatus.Succeeded, new IssuedApiToken(IdentityMapping.ToSummary(row), ApiTokenFormat.Format(row.Id, secret)));
    }

    /// <summary>
    /// The tokens of <paramref name="owner"/> (the caller when null), newest first, revoked ones included, never with a secret.
    /// One's own need <see cref="Permission.TokensOwn"/>; anyone else's need <see cref="Permission.TokensAll"/>.
    /// </summary>
    public async Task<ApiTokenListResult> ListAsync(CommandOrigin origin, UserId? owner = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var target = owner ?? origin.Actor.UserId;
        if (target is not { } user)
        {
            return new ApiTokenListResult(IdentityStatus.Denied, []);
        }

        var needed = user == origin.Actor.UserId ? Permission.TokensOwn : Permission.TokensAll;
        if (!await _permissions.IsAllowedAsync(origin, needed, cancellationToken).ConfigureAwait(false))
        {
            return new ApiTokenListResult(IdentityStatus.Denied, []);
        }

        return await _contexts.UseAsync(async context =>
        {
            var rows = await context.ApiTokens.AsNoTracking().Where(t => t.UserId == user).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new ApiTokenListResult(IdentityStatus.Succeeded, [.. rows.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id).Select(IdentityMapping.ToSummary)]);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every user's tokens in one read, newest first, revoked ones included, never with a secret.</summary>
    public async Task<ApiTokenListResult> ListAllAsync(CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await _permissions.IsAllowedAsync(origin, Permission.TokensAll, cancellationToken).ConfigureAwait(false))
        {
            return new ApiTokenListResult(IdentityStatus.Denied, []);
        }

        return await _contexts.UseAsync(async context =>
        {
            var rows = await context.ApiTokens.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return new ApiTokenListResult(IdentityStatus.Succeeded, [.. rows.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id).Select(IdentityMapping.ToSummary)]);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes a token (idempotent): the caller's own with <see cref="Permission.TokensOwn"/>, anyone's with <see cref="Permission.TokensAll"/>.</summary>
    public async Task<ApiTokenResult> RevokeAsync(CommandOrigin origin, ApiTokenId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return await _contexts.UseAsync(async context =>
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var row = await context.ApiTokens.SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return new ApiTokenResult(IdentityStatus.NotFound);
                }

                if (!await MayManageAsync(origin, row, cancellationToken).ConfigureAwait(false))
                {
                    // Audited, but answered like a token that does not exist: ids are not a way to find out whose tokens there are.
                    await _audit.AppendAsync(context, IdentityAudit.Entry(origin, "token.revoke", AuditOutcome.Denied, id.ToString()), cancellationToken).ConfigureAwait(false);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new ApiTokenResult(IdentityStatus.NotFound);
                }

                row.RevokedAt ??= _time.GetUtcNow();
                await _audit.AppendAsync(context, IdentityAudit.Entry(origin, "token.revoke", AuditOutcome.Success, id.ToString(), w => w.WriteString("user", row.UserId.ToString())), cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ApiTokenResult(IdentityStatus.Succeeded, Token: IdentityMapping.ToSummary(row));
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames a token, replaces its scopes and/or switches it off or on, in one transaction. Who may is who may revoke it. The scopes are held to what
    /// the token's <i>owner</i> holds now, not the caller (an Owner can't hand another user's token anything its owner lacks). A revoked token is
    /// final: it is a conflict. The secret is never shown or rotated. Takes effect on the token's next use: verification reads the row each time.
    /// </summary>
    public async Task<ApiTokenResult> UpdateAsync(CommandOrigin origin, ApiTokenId id, UpdateApiTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(request);
        return await _contexts.UseAsync(async context =>
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var row = await context.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return new ApiTokenResult(IdentityStatus.NotFound);
                }

                if (!await MayManageAsync(origin, row, cancellationToken).ConfigureAwait(false))
                {
                    await _audit.AppendAsync(context, IdentityAudit.Entry(origin, "token.update", AuditOutcome.Denied, id.ToString()), cancellationToken).ConfigureAwait(false);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new ApiTokenResult(IdentityStatus.NotFound);
                }

                if (row.RevokedAt is not null)
                {
                    return new ApiTokenResult(IdentityStatus.Conflict, Problem: "A revoked token can't be changed.");
                }

                if (request is { Name: null, Scopes: null, Disabled: null })
                {
                    return new ApiTokenResult(IdentityStatus.Invalid, Problem: "Nothing to change: give a name, scopes or disabled.");
                }

                string? name = null;
                if (request.Name is not null && (name = IdentityMapping.Clean(request.Name, IdentityConfiguration.MaxTokenNameLength)) is null)
                {
                    return new ApiTokenResult(IdentityStatus.Invalid, Problem: "A token needs a name.");
                }

                List<Permission>? scopes = null;
                if (request.Scopes is not null)
                {
                    scopes = [.. request.Scopes.Distinct().Order()];
                    var owner = await PermissionEvaluator.OfUserAsync(context, row.UserId, cancellationToken).ConfigureAwait(false);
                    var problem = scopes.Count == 0 ? "A token needs at least one scope."
                        : scopes.Exists(s => !IsAssignableScope(s)) ? "Unknown scope."
                        : scopes.Exists(s => !owner.Contains(s)) ? "A token cannot have a scope its owner lacks."
                        : null;
                    if (problem is not null)
                    {
                        return new ApiTokenResult(IdentityStatus.Invalid, Problem: problem);
                    }
                }

                var renamed = name is not null && name != row.Name;
                var previousName = row.Name;
                var rescoped = scopes is not null && IdentityMapping.FormatScopes(scopes) != row.Scopes;
                var previousScopes = row.Scopes;
                var switched = request.Disabled is { } disabled && disabled != (row.DisabledAt is not null);
                var newName = renamed ? name! : row.Name;
                var newScopes = rescoped ? IdentityMapping.FormatScopes(scopes!) : row.Scopes;
                var newDisabledAt = switched ? (request.Disabled == true ? _time.GetUtcNow() : (DateTimeOffset?)null) : row.DisabledAt;

                // One conditional statement, so a revoke that lands between the read and this write can't be followed by a change or an audit
                // event for a token that is already final. (The transaction's write lock already keeps two writers apart; this does not rest on it.)
                var applied = await context.ApiTokens.Where(t => t.Id == id && t.RevokedAt == null).ExecuteUpdateAsync(
                    u => u.SetProperty(t => t.Name, newName).SetProperty(t => t.Scopes, newScopes).SetProperty(t => t.DisabledAt, newDisabledAt),
                    cancellationToken).ConfigureAwait(false);
                if (applied == 0)
                {
                    return new ApiTokenResult(IdentityStatus.Conflict, Problem: "A revoked token can't be changed.");
                }

                if (renamed || rescoped)
                {
                    await _audit.AppendAsync(
                        context,
                        IdentityAudit.Entry(origin, "token.update", AuditOutcome.Success, id.ToString(), w =>
                        {
                            w.WriteString("user", row.UserId.ToString());
                            if (renamed)
                            {
                                w.WriteString("name", newName);
                                w.WriteString("previousName", previousName);
                            }

                            if (rescoped)
                            {
                                w.WriteString("scopes", newScopes);
                                w.WriteString("previousScopes", previousScopes);
                            }
                        }),
                        cancellationToken).ConfigureAwait(false);
                }

                if (switched)
                {
                    await _audit.AppendAsync(
                        context,
                        IdentityAudit.Entry(origin, newDisabledAt is null ? "token.enable" : "token.disable", AuditOutcome.Success, id.ToString(), w => w.WriteString("user", row.UserId.ToString())),
                        cancellationToken).ConfigureAwait(false);
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                var updated = await context.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
                return new ApiTokenResult(IdentityStatus.Succeeded, Token: IdentityMapping.ToSummary(updated));
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the caller may change or revoke <paramref name="row"/>: anyone's with <see cref="Permission.TokensAll"/>, their own with <see cref="Permission.TokensOwn"/>.</summary>
    private async Task<bool> MayManageAsync(CommandOrigin origin, ApiTokenRow row, CancellationToken cancellationToken)
    {
        var held = await _permissions.GetPermissionsAsync(origin.Actor, cancellationToken).ConfigureAwait(false);
        return held.Contains(Permission.TokensAll) || (row.UserId == origin.Actor.UserId && held.Contains(Permission.TokensOwn));
    }

    /// <summary>Authenticates a presented token (the Bearer value). Anything wrong is simply not valid.</summary>
    public async Task<ApiTokenVerification> VerifyAsync(string? presented, CancellationToken cancellationToken = default)
    {
        if (!ApiTokenFormat.TryParse(presented, out var id, out var secret))
        {
            return new ApiTokenVerification(null);
        }

        return await _contexts.UseAsync(async context =>
        {
            var row = await context.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
            // Compared even when there is no such token, so that the time taken does not tell an unknown ID from a wrong secret.
            var matches = CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret), row?.SecretHash ?? new byte[32]);
            var now = _time.GetUtcNow();
            if (row is null || !matches || row.RevokedAt is not null || row.DisabledAt is not null || (row.ExpiresAt is { } expires && expires <= now))
            {
                return new ApiTokenVerification(null);
            }

            var owner = await PermissionEvaluator.OfUserAsync(context, row.UserId, cancellationToken).ConfigureAwait(false);
            if (owner.Count == 0)
            {
                // Disabled, deleted, or stripped of every role: the token goes with the user.
                return new ApiTokenVerification(null);
            }

            if (row.LastUsedAt is not { } last || now - last >= Options.TouchInterval)
            {
                await context.ApiTokens.Where(t => t.Id == id)
                    .ExecuteUpdateAsync(u => u.SetProperty(t => t.LastUsedAt, now), cancellationToken).ConfigureAwait(false);
            }

            return new ApiTokenVerification(new ApiTokenPrincipal(row.Id, row.UserId, RolePermissions.Narrow(owner, IdentityMapping.ParseScopes(row.Scopes))));
        }, cancellationToken).ConfigureAwait(false);
    }
}
