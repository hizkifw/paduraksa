using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Data.Drafts;

namespace Paduraksa.Application.Drafts;

/// <summary>How a draft call ended.</summary>
public enum DraftStatus
{
    /// <summary>Done; see the result's draft and command.</summary>
    Succeeded,

    /// <summary>The caller may not change config, or is not a user or a user's token (a draft belongs to a user).</summary>
    Denied,

    /// <summary>The caller has no open draft, or not that one.</summary>
    NoDraft,

    /// <summary>The caller already has an open draft (one each).</summary>
    AlreadyOpen,

    /// <summary>
    /// What an apply would commit is not what the review it names showed (another change to the draft, to the config it touches, or to what the compiler
    /// says about it). Nothing was committed; <see cref="DraftResult.Command"/> is the fresh review and <see cref="DraftResult.ReviewToken"/> its token.
    /// </summary>
    ChangedSinceReview,

    /// <summary>
    /// Some entries are changed elsewhere (or depend on one that is), and a draft is applied whole or not at all: nothing was committed. The draft's
    /// entries say which, and why; <see cref="DraftResult.Command"/> is a dry run of the rest, for the diff.
    /// </summary>
    Blocked,

    /// <summary>The draft has no entries, so there is nothing to apply.</summary>
    NothingToApply,

    /// <summary>The call was refused; <see cref="DraftResult.Command"/> says why (an invalid entry, a refused review or apply, a store that is unavailable).</summary>
    Refused,
}

/// <summary>The outcome of a draft call.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Draft">The draft after the call (null when there is none, after a discard or an apply that took everything).</param>
/// <param name="Command">A review's or an apply's pipeline result, or what refused an entry.</param>
/// <param name="FailedCommand">For an add that was refused: the index of the command that was.</param>
/// <param name="ReviewToken">A review that passed, and could be applied: the token an apply must send (<see cref="DraftReviewToken"/>); null when the draft
/// has blocked entries, or the set doesn't pass.</param>
public sealed record DraftResult(DraftStatus Status, ConfigDraft? Draft = null, ConfigCommandResult? Command = null, int? FailedCommand = null, string? ReviewToken = null);

/// <summary>
/// Drafts (docs/design/dashboard.md "Drafts (M10)"): a user's change set across pages, kept in the config database until it is applied as one
/// revision. Nothing in a draft is live, committed or audited. An entry is an object command with the version it was made against; every
/// read replays the entries on the current head (<see cref="DraftReplay"/>), so what a draft shows is always the head with the draft applied
/// and a concurrent change by someone else is seen as it happens (an entry whose object moved is blocked, never overwritten).
/// </summary>
/// <remarks>
/// <para><b>One writer.</b> Every change to a draft runs under the config writer's lock, like a command, so a replay and the commit it leads to
/// see one head, and the draft's rows change in the commit's own transaction.</para>
/// <para><b>Secrets.</b> A password typed into an access list never reaches a row. It becomes a vault credential no config names yet
/// (<see cref="IPasswordCredentials"/>), the entry's user carries its ID, and the draft keeps the list of such IDs: they are deleted with the
/// entry, the draft, or its expiry, and the startup sweep leaves them alone while a draft holds them.</para>
/// <para><b>Lifetime.</b> A draft untouched for <see cref="Lifetime"/> is dropped (its credentials too) and its owner gets a
/// <see cref="DraftNotice"/> to be shown once; <see cref="DropExpiredAsync"/> does it, and so does any call that meets an expired draft.</para>
/// </remarks>
public sealed partial class DraftService
{
    /// <summary>How long a draft may sit untouched.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromDays(30);

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly ConfigService _config;
    private readonly IPasswordCredentials _passwords;
    private readonly IPermissionEvaluator _permissions;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    internal DraftService(
        IDbContextFactory<ConfigDbContext> contexts, ConfigService config, IPasswordCredentials passwords, IPermissionEvaluator permissions,
        TimeProvider time, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _contexts = contexts;
        _config = config;
        _passwords = passwords;
        _permissions = permissions;
        _time = time;
        _logger = loggerFactory.CreateLogger<DraftService>();
    }

    /// <summary>
    /// Raised, with the draft's owner, after every change to a draft (opened, added to, an entry removed, applied, discarded, expired): the dashboard's
    /// bar and pages in the owner's other tabs follow it. Raised on the writer's thread: a handler must not wait.
    /// </summary>
    public event Action<UserId>? Changed;

    /// <summary>The caller's own open draft, its entries marked against the head; <see cref="DraftStatus.NoDraft"/> when there is none.</summary>
    public async Task<DraftResult> GetAsync(CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        var view = await ViewAsync(origin, null, cancellationToken).ConfigureAwait(false);
        return view.Result ?? new DraftResult(DraftStatus.Succeeded, view.Draft);
    }

    /// <summary>The head with the caller's draft applied (for the dashboard's pages and pickers); null with no draft, or none by <paramref name="id"/>.</summary>
    public async Task<DraftConfig?> GetConfigAsync(CommandOrigin origin, DraftId? id = null, CancellationToken cancellationToken = default)
    {
        var view = await ViewAsync(origin, id, cancellationToken).ConfigureAwait(false);
        return view.Draft is { } draft && view.Document is { } document ? new DraftConfig(document, draft, draft.Head) : null;
    }

    /// <summary>Opens an empty draft on the head. <see cref="DraftStatus.AlreadyOpen"/> (with the open draft) when there is one.</summary>
    public async Task<DraftResult> CreateAsync(CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (await GateAsync(origin, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        return await _config.UnderWriterAsync(async _ =>
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                if (await OwnerAsync(context, origin, cancellationToken).ConfigureAwait(false) is not { } owner)
                {
                    return new DraftResult(DraftStatus.Denied);
                }

                var existing = await DropIfExpiredAsync(context, await FindAsync(context, owner, null, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                var (latest, document) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    return new DraftResult(DraftStatus.AlreadyOpen, ToDraft(existing, document, latest is null ? null : ConfigHead.Ref(latest.Info)).Draft);
                }

                var now = _time.GetUtcNow();
                DraftRow row = new()
                {
                    Id = DraftId.New(),
                    OwnerUserId = owner,
                    Version = 1,
                    StartedSeq = latest?.Info.Seq ?? 0,
                    StartedRevisionId = latest?.Info.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                context.ConfigDrafts.Add(row);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                Announce(owner);
                return new DraftResult(DraftStatus.Succeeded, ToDraft(row, document, latest is null ? null : ConfigHead.Ref(latest.Info)).Draft);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds <paramref name="commands"/> to the draft, in order. Each is checked as a dry run on the head with the draft's other entries
    /// applied (the errors a live command would give, relative to the object) and replaces the draft's earlier entry for its object; a command
    /// that leaves its object as the head has it takes the earlier entry out, and deleting an object the draft itself creates removes its
    /// entry. If one is refused none is added (<see cref="DraftResult.FailedCommand"/>).
    /// </summary>
    public async Task<DraftResult> AddAsync(CommandOrigin origin, DraftId id, IReadOnlyList<ConfigCommand> commands, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(commands);
        if (await GateAsync(origin, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        return await _config.UnderWriterAsync(async pipeline =>
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var (row, missing) = await LoadOwnAsync(context, origin, id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return missing!;
                }

                var (latest, head) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                var headRef = latest is null ? null : ConfigHead.Ref(latest.Info);
                var entries = Read(row);
                var pending = ReadCredentials(row);
                List<(PasswordCredentialId Id, string Password)> toSet = [];
                var now = _time.GetUtcNow();
                var next = row.NextNumber;

                for (var i = 0; i < commands.Count; i++)
                {
                    if (commands[i] is null || DraftEntries.Of(DraftEntries.Normalize(commands[i])) is not var (target, action))
                    {
                        return Refuse(i, DraftErrorCodes.EntryCommand, "A draft takes a change to one object (a host, upstream, profile, rate limit, access list, rule set or sign-in provider) or to the global settings, not a whole-config command.");
                    }

                    var command = DraftEntries.Normalize(commands[i]);
                    if (ConfigDrafts.Preflight(command) is { } malformed)
                    {
                        return new DraftResult(DraftStatus.Refused, Command: new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: [malformed]), FailedCommand: i);
                    }

                    var at = entries.FindIndex(e => e.Target == target);
                    var existing = at >= 0 ? entries[at] : null;
                    if (action == DraftEntryAction.Delete && existing is { Action: DraftEntryAction.Create })
                    {
                        entries.RemoveAt(at);
                        continue;
                    }

                    var working = DraftReplay.Replay(head, headRef, entries.Where((_, j) => j != at)).Document;
                    var attempt = await pipeline.AttemptAsync(command with { DryRun = true }, origin, ConfigCommandPlanner.Describe(command), null,
                        cancellationToken, simulation: true, new ConfigPipeline.DraftAttempt(Base: working)).ConfigureAwait(false);
                    if (attempt.Result.Status == ConfigCommandStatus.Unchanged)
                    {
                        if (at >= 0)
                        {
                            entries.RemoveAt(at);
                        }

                        continue;
                    }

                    if (attempt.Result.Status != ConfigCommandStatus.DryRun)
                    {
                        return new DraftResult(DraftStatus.Refused, Command: attempt.Result, FailedCommand: i);
                    }

                    var stored = Secrets(command, working, toSet);
                    if (existing is null)
                    {
                        entries.Add(new DraftEntry(next++, action, target, stored, now));
                    }
                    else
                    {
                        entries[at] = existing with { Action = action, Command = stored, AddedAt = now, State = DraftEntryState.Ready };
                    }
                }

                var referenced = Referenced(entries);
                var keep = pending.Concat(toSet.Select(static p => p.Id)).Where(referenced.Contains).Distinct().ToList();
                var drop = pending.Where(p => !keep.Contains(p)).ToList();
                // A password whose entry was replaced by a later command of this same call is nobody's: only the ones an entry still names are set.
                var changes = await SetAsync([.. toSet.Where(p => keep.Contains(p.Id))], cancellationToken).ConfigureAwait(false);
                try
                {
                    Write(row, entries, keep);
                    row.NextNumber = next;
                    row.Version++;
                    row.UpdatedAt = now;
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await _passwords.RevertAsync(changes, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                _passwords.Keep(changes);
                await DeleteCredentialsAsync(drop, head).ConfigureAwait(false);
                Announce(row.OwnerUserId);
                return new DraftResult(DraftStatus.Succeeded, ToDraft(row, head, headRef).Draft);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes entry <paramref name="number"/> out of the draft (an entry that is changed elsewhere is removed the same way; this is its "reload").</summary>
    public async Task<DraftResult> RemoveEntryAsync(CommandOrigin origin, DraftId id, int number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (await GateAsync(origin, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        return await _config.UnderWriterAsync(async _ =>
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var (row, missing) = await LoadOwnAsync(context, origin, id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return missing!;
                }

                var (latest, head) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                var headRef = latest is null ? null : ConfigHead.Ref(latest.Info);
                var entries = Read(row);
                if (entries.RemoveAll(e => e.Number == number) == 0)
                {
                    return new DraftResult(DraftStatus.NoDraft, ToDraft(row, head, headRef).Draft, FailedCommand: null);
                }

                var keep = ReadCredentials(row).Where(Referenced(entries).Contains).ToList();
                var drop = ReadCredentials(row).Where(p => !keep.Contains(p)).ToList();
                Write(row, entries, keep);
                row.Version++;
                row.UpdatedAt = _time.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await DeleteCredentialsAsync(drop, head).ConfigureAwait(false);
                Announce(row.OwnerUserId);
                return new DraftResult(DraftStatus.Succeeded, ToDraft(row, head, headRef).Draft);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replays the draft on the head and dry-runs it as one change: the diff, the compiler's warnings, the lock-out guard and the errors of the
    /// whole set, over the entries that are not blocked.
    /// </summary>
    public Task<DraftResult> ReviewAsync(CommandOrigin origin, DraftId id, bool force = false, CancellationToken cancellationToken = default) =>
        RunAsync(origin, id, reviewToken: null, message: null, force, dryRun: true, cancellationToken);

    /// <summary>
    /// Commits the draft as <b>one revision</b>, audited once with every entry listed, and takes it out of the database with it; or commits nothing.
    /// <paramref name="reviewToken"/> is the token of the review the person confirmed (<see cref="DraftResult.ReviewToken"/>): under the writer's lock the
    /// draft is replayed on the current head, and unless that is exactly what the review showed (<see cref="DraftReviewToken"/>) nothing is committed and the
    /// fresh review comes back (<see cref="DraftStatus.ChangedSinceReview"/>). A draft with any blocked entry is refused whole (<see cref="DraftStatus.Blocked"/>).
    /// The note defaults to a summary of the entries.
    /// </summary>
    public Task<DraftResult> ApplyAsync(CommandOrigin origin, DraftId id, string? reviewToken, string? message = null, bool force = false, CancellationToken cancellationToken = default) =>
        RunAsync(origin, id, reviewToken ?? string.Empty, message, force, dryRun: false, cancellationToken);

    /// <summary>Drops the draft and the vault values bound to it.</summary>
    public async Task<DraftResult> DiscardAsync(CommandOrigin origin, DraftId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (await GateAsync(origin, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        return await _config.UnderWriterAsync(async _ =>
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var (row, missing) = await LoadOwnAsync(context, origin, id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return missing!;
                }

                var pending = ReadCredentials(row);
                context.ConfigDrafts.Remove(row);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var (_, head) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                await DeleteCredentialsAsync(pending, head).ConfigureAwait(false);
                _views.TryRemove(row.OwnerUserId, out var gone);
                Announce(row.OwnerUserId);
                return new DraftResult(DraftStatus.Succeeded);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops every draft untouched for <see cref="Lifetime"/>, with its vault values, and leaves its owner a notice. Returns how many.</summary>
    public async Task<int> DropExpiredAsync(CancellationToken cancellationToken = default) =>
        (await DropExpiredIdsAsync(cancellationToken).ConfigureAwait(false)).Count;

    // Under the writer's lock, which is also where "untouched" is judged: a draft its owner refreshed since a reader saw it expire is not dropped.
    private Task<HashSet<DraftId>> DropExpiredIdsAsync(CancellationToken cancellationToken) =>
        _config.UnderWriterAsync(async _ =>
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                // The stamps first: an hourly pass over many drafts should not read every entry list to find the few that are past it.
                var cutoff = _time.GetUtcNow() - Lifetime;
                var stamps = await context.ConfigDrafts.AsNoTracking().Select(static d => new { d.Id, d.UpdatedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
                var ids = stamps.Where(d => d.UpdatedAt <= cutoff).Select(static d => d.Id).ToList();
                if (ids.Count == 0)
                {
                    return [];
                }

                var expired = await context.ConfigDrafts.Where(d => ids.Contains(d.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var row in expired)
                {
                    await DropAsync(context, row, cancellationToken).ConfigureAwait(false);
                }

                return expired.Select(static d => d.Id).ToHashSet();
            }
        }, cancellationToken);

    /// <summary>The caller's notices of drafts that were dropped, once: they are gone after this call.</summary>
    public async Task<IReadOnlyList<DraftNotice>> TakeNoticesAsync(CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (await OwnerAsync(context, origin, cancellationToken).ConfigureAwait(false) is not { } owner)
            {
                return [];
            }

            var rows = (await context.ConfigDraftNotices.Where(n => n.OwnerUserId == owner).ToListAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(static n => n.DroppedAt).ToList();
            if (rows.Count > 0)
            {
                context.ConfigDraftNotices.RemoveRange(rows);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return [.. rows.Select(static n => new DraftNotice(n.DraftId, n.DroppedAt, n.Entries))];
        }
    }

    /// <summary>The credentials open drafts hold, which the startup sweep of the vault must leave alone.</summary>
    internal static async Task<HashSet<PasswordCredentialId>> PendingOfAllAsync(ConfigDbContext context, CancellationToken cancellationToken)
    {
        var all = await context.ConfigDrafts.AsNoTracking().Select(static d => d.PendingCredentials).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.SelectMany(static json => JsonSerializer.Deserialize(json, ConfigJsonContext.Default.IReadOnlyListPasswordCredentialId) ?? [])];
    }

    private async Task<DraftResult> RunAsync(
        CommandOrigin origin, DraftId id, string? reviewToken, string? message, bool force, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (await GateAsync(origin, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var note = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (ConfigCommandPlanner.MessageProblem(note) is { } problem)
        {
            return Refuse(null, ConfigService.MessageInvalidCode, problem);
        }

        UserId? owner = null;
        var (result, execution) = await _config.UnderWriterAsync(async pipeline =>
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var (row, missing) = await LoadOwnAsync(context, origin, id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return (missing!, (Execution?)null);
                }

                owner = row.OwnerUserId;
                var (latest, head) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                var headRef = latest is null ? null : ConfigHead.Ref(latest.Info);
                var replayed = DraftReplay.Replay(head, headRef, Read(row));
                var (view, _) = ToDraft(row, head, headRef);
                var ready = replayed.Ready.ToList();
                if (replayed.Entries.Count == 0)
                {
                    return (new DraftResult(DraftStatus.NothingToApply, view), null);
                }

                var blocked = replayed.Blocked.Any();
                if (ready.Count == 0 || (blocked && !dryRun))
                {
                    return (new DraftResult(DraftStatus.Blocked, view), null);
                }

                var summary = Summary(ready.Select(e => DraftText.Title(e, head)).ToList());
                var command = new ReplaceConfigCommand(replayed.Document, headRef?.Id) { DryRun = dryRun, Force = force };
                string? fresh = null;
                ConfigPipeline.DraftAttempt attempt = new(
                    AuditDetails: w =>
                    {
                        w.WriteString("draft", row.Id.ToString());
                        w.WriteStartArray("entries");
                        foreach (var entry in ready)
                        {
                            var audited = ConfigCommandPlanner.Describe(entry.Command);
                            w.WriteStartObject();
                            w.WriteNumber("number", entry.Number);
                            w.WriteString("action", audited.Action);
                            w.WriteString("target", audited.Target);
                            w.WriteString("title", DraftText.Title(entry, head));
                            w.WriteEndObject();
                        }

                        w.WriteEndArray();
                    },
                    InCommit: dryRun ? null : (inCommit, _) => DeleteAppliedAsync(inCommit, row.Id, cancellationToken),
                    Gate: dryRun ? null : planned =>
                    {
                        fresh = DraftReviewToken.Compute(row.Id, row.Version, replayed, planned.Warnings, _config.Warnings);
                        return string.Equals(fresh, reviewToken, StringComparison.Ordinal);
                    });
                var run = await pipeline.AttemptAsync(command, origin, new ConfigCommandPlanner.Audited("config.draft.apply", row.Id.ToString(), force),
                    note ?? summary, cancellationToken, simulation: false, attempt).ConfigureAwait(false);
                if (!dryRun && run.Result.Status == ConfigCommandStatus.DryRun)
                {
                    // Everything passed, but it is not what was confirmed: the review as it is now goes back, and nothing was committed.
                    return (new DraftResult(DraftStatus.ChangedSinceReview, view, run.Result, ReviewToken: fresh), null);
                }

                if (run.Result.Status == ConfigCommandStatus.Unchanged && !dryRun)
                {
                    // What the entries ask for is already the head's: they are done, and nothing is committed.
                    await DeleteAppliedAsync(context, row.Id, cancellationToken).ConfigureAwait(false);
                }

                var token = dryRun && !blocked && run.Result.Status == ConfigCommandStatus.DryRun
                    ? DraftReviewToken.Compute(row.Id, row.Version, replayed, run.Result.Warnings, _config.Warnings)
                    : null;
                return (new DraftResult(DraftStatus.Succeeded, Command: run.Result, ReviewToken: token), (Execution?)run);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (execution is { Publish: not null } committed)
        {
            result = result with { Command = await _config.FinishAsync(committed, origin).ConfigureAwait(false) };
        }

        if (result.Status is not (DraftStatus.Succeeded or DraftStatus.ChangedSinceReview))
        {
            return result;
        }

        if (!dryRun && owner is { } applied && result.Command is { Status: ConfigCommandStatus.Committed or ConfigCommandStatus.PublishFailed or ConfigCommandStatus.Unchanged })
        {
            _views.TryRemove(applied, out var gone);
            Announce(applied);
        }

        var after = await ViewAsync(origin, id, cancellationToken).ConfigureAwait(false);
        var status = result.Status == DraftStatus.ChangedSinceReview
            ? DraftStatus.ChangedSinceReview
            : result.Command!.Status is ConfigCommandStatus.Committed or ConfigCommandStatus.PublishFailed or ConfigCommandStatus.DryRun or ConfigCommandStatus.Unchanged
                ? DraftStatus.Succeeded
                : DraftStatus.Refused;
        return result with { Status = status, Draft = after.Draft };
    }

    // In the commit's transaction: the draft leaves with the revision that took all of it, so a crash can't leave an applied entry to be applied again.
    private static async Task DeleteAppliedAsync(ConfigDbContext context, DraftId id, CancellationToken cancellationToken)
    {
        var row = await context.ConfigDrafts.SingleOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is not null)
        {
            context.ConfigDrafts.Remove(row);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(DraftResult? Result, ConfigDraft? Draft, ConfigDocument? Document)> ViewAsync(CommandOrigin origin, DraftId? id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (await GateAsync(origin, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return (refusal, null, null);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (await OwnerAsync(context, origin, cancellationToken).ConfigureAwait(false) is not { } owner)
            {
                return (new DraftResult(DraftStatus.Denied), null, null);
            }

            var row = await FindAsync(context, owner, id, cancellationToken).ConfigureAwait(false);
            if (row is not null && IsExpired(row))
            {
                if ((await DropExpiredIdsAsync(cancellationToken).ConfigureAwait(false)).Contains(row.Id))
                {
                    row = null;
                }
                else
                {
                    // Its owner touched it between our read and the lock: what is stored now is the draft.
                    await context.Entry(row).ReloadAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (row is null)
            {
                return (new DraftResult(DraftStatus.NoDraft), null, null);
            }

            var (latest, head) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
            var (draft, document) = ToDraft(row, head, latest is null ? null : ConfigHead.Ref(latest.Info));
            return (null, draft, document);
        }
    }

    private void Announce(UserId owner)
    {
        try
        {
            Changed?.Invoke(owner);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAnnounceFailed(ex);
        }
    }

    private async Task<DraftResult?> GateAsync(CommandOrigin origin, CancellationToken cancellationToken)
    {
        if (_config.Status is { AcceptsCommands: false } unusable)
        {
            return new DraftResult(DraftStatus.Refused, Command: new ConfigCommandResult(
                ConfigCommandStatus.Unavailable,
                Errors: [new ConfigError(string.Empty, ConfigService.StoreUnavailableCode, $"The config store is unavailable, so drafts are refused: {unusable.Problem}.")]));
        }

        return await _permissions.IsAllowedAsync(origin, OperationPermissions.ForConfigCommand, cancellationToken).ConfigureAwait(false)
            ? null
            : new DraftResult(DraftStatus.Denied);
    }

    /// <summary>The user a draft of this origin belongs to: the user themselves, or the owner of the token they act with.</summary>
    private static async Task<UserId?> OwnerAsync(ConfigDbContext context, CommandOrigin origin, CancellationToken cancellationToken) =>
        origin.Actor switch
        {
            { Kind: ActorKind.User, UserId: { } user } => user,
            { Kind: ActorKind.Token, TokenId: { } token } => await context.ApiTokens.AsNoTracking().Where(t => t.Id == token)
                .Select(static t => (UserId?)t.UserId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            _ => null,
        };

    private static Task<DraftRow?> FindAsync(ConfigDbContext context, UserId owner, DraftId? id, CancellationToken cancellationToken) =>
        context.ConfigDrafts.Where(d => d.OwnerUserId == owner && (id == null || d.Id == id)).SingleOrDefaultAsync(cancellationToken);

    /// <summary>The caller's draft <paramref name="id"/> for a change (tracked), or why there isn't one. An expired draft is dropped on the way.</summary>
    private async Task<(DraftRow? Row, DraftResult? Missing)> LoadOwnAsync(ConfigDbContext context, CommandOrigin origin, DraftId id, CancellationToken cancellationToken)
    {
        if (await OwnerAsync(context, origin, cancellationToken).ConfigureAwait(false) is not { } owner)
        {
            return (null, new DraftResult(DraftStatus.Denied));
        }

        var row = await DropIfExpiredAsync(context, await FindAsync(context, owner, id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return row is null ? (null, new DraftResult(DraftStatus.NoDraft)) : (row, null);
    }

    private bool IsExpired(DraftRow row) => row.UpdatedAt + Lifetime <= _time.GetUtcNow();

    private async Task<DraftRow?> DropIfExpiredAsync(ConfigDbContext context, DraftRow? row, CancellationToken cancellationToken)
    {
        if (row is null || !IsExpired(row))
        {
            return row;
        }

        await DropAsync(context, row, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task DropAsync(ConfigDbContext context, DraftRow row, CancellationToken cancellationToken)
    {
        var pending = ReadCredentials(row);
        var entries = Read(row).Count;
        context.ConfigDraftNotices.Add(new DraftNoticeRow { DraftId = row.Id, OwnerUserId = row.OwnerUserId, DroppedAt = _time.GetUtcNow(), Entries = entries });
        context.ConfigDrafts.Remove(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var (_, head) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        await DeleteCredentialsAsync(pending, head).ConfigureAwait(false);
        LogDropped(row.Id, entries);
        _views.TryRemove(row.OwnerUserId, out var gone);
        Announce(row.OwnerUserId);
    }

    // One replayed view per user, keyed by what it depends on: the latest revision, the draft and its version. A read that finds the same three
    // (every render of every page of a circuit, while nothing changed) costs no replay. Bounded: a user is one slot, and past the cap the table starts over.
    private const int MaxCachedViews = 256;

    private readonly ConcurrentDictionary<UserId, CachedView> _views = new();

    private sealed record CachedView(ConfigRevisionId? Head, DraftId Draft, long Version, ConfigDraft Result, ConfigDocument Document);

    private (ConfigDraft Draft, ConfigDocument Document) ToDraft(DraftRow row, ConfigDocument head, RevisionRef? headRef)
    {
        if (_views.TryGetValue(row.OwnerUserId, out var cached) && cached.Head == headRef?.Id && cached.Draft == row.Id && cached.Version == row.Version)
        {
            return (cached.Result, cached.Document);
        }

        var replayed = DraftReplay.Replay(head, headRef, Read(row));
        ConfigDraft draft = new(
            row.Id,
            row.Version,
            row.CreatedAt,
            row.UpdatedAt,
            row.UpdatedAt + Lifetime,
            replayed.Entries,
            row.StartedRevisionId is { } started ? new RevisionRef(started, row.StartedSeq) : null,
            headRef);
        if (_views.Count >= MaxCachedViews && !_views.ContainsKey(row.OwnerUserId))
        {
            _views.Clear();
        }

        _views[row.OwnerUserId] = new CachedView(headRef?.Id, row.Id, row.Version, draft, replayed.Document);
        return (draft, replayed.Document);
    }

    private static List<DraftEntry> Read(DraftRow row) =>
        [.. (JsonSerializer.Deserialize(row.Entries, ConfigJsonContext.Default.IReadOnlyListStoredDraftEntry) ?? [])
            .Select(static e => new DraftEntry(e.Number, e.Action, e.Target, e.Command, e.AddedAt))];

    private static List<PasswordCredentialId> ReadCredentials(DraftRow row) =>
        [.. JsonSerializer.Deserialize(row.PendingCredentials, ConfigJsonContext.Default.IReadOnlyListPasswordCredentialId) ?? []];

    private static void Write(DraftRow row, IReadOnlyList<DraftEntry> entries, IReadOnlyList<PasswordCredentialId> credentials)
    {
        row.Entries = JsonSerializer.Serialize<IReadOnlyList<StoredDraftEntry>>(
            [.. entries.Select(static e => new StoredDraftEntry(e.Number, e.Action, e.Target, e.Command, e.AddedAt))], ConfigJsonContext.Default.IReadOnlyListStoredDraftEntry);
        row.PendingCredentials = JsonSerializer.Serialize(credentials, ConfigJsonContext.Default.IReadOnlyListPasswordCredentialId);
    }

    private static DraftResult Refuse(int? index, string code, string message) =>
        new(DraftStatus.Refused, Command: new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: [new ConfigError(string.Empty, code, message)]), FailedCommand: index);

    /// <summary>The revision's note when the caller gave none: the entries in words, within the note's length.</summary>
    private static string Summary(List<string> titles)
    {
        var text = $"Draft ({titles.Count} {(titles.Count == 1 ? "change" : "changes")}): {string.Join(", ", titles)}";
        return text.Length <= ConfigRevisionStore.MaxMessageLength ? text : string.Concat(text.AsSpan(0, ConfigRevisionStore.MaxMessageLength - 3), "...");
    }

    private static HashSet<PasswordCredentialId> Referenced(IEnumerable<DraftEntry> entries) =>
    [
        .. entries.Select(static e => e.Command).OfType<UpsertAccessListCommand>()
            .SelectMany(static c => c.AccessList?.BasicAuth?.Users ?? [])
            .Select(static u => u?.CredentialId).OfType<PasswordCredentialId>().Where(static id => !id.IsEmpty),
    ];

    /// <summary>
    /// A password typed into an access list leaves the command here: each user with one gets a <b>new</b> credential (never the one the list has now:
    /// that password stays until the draft is applied) and the command keeps its ID; a user sent with neither a password nor an ID keeps the
    /// credential the list has for that name in the draft's own view of it.
    /// </summary>
    private static ConfigCommand Secrets(ConfigCommand command, ConfigDocument working, List<(PasswordCredentialId Id, string Password)> toSet)
    {
        if (command is not UpsertAccessListCommand { AccessList: { BasicAuth: { } auth } list } upsert)
        {
            return command;
        }

        var existing = working.AccessLists.FirstOrDefault(l => l.Id == list.Id)?.BasicAuth?.Users ?? [];
        var users = (auth.Users ?? []).Select(user =>
        {
            if (user is null)
            {
                return user!;
            }

            if (user.Password is { Length: > 0 } password)
            {
                var id = PasswordCredentialId.New();
                toSet.Add((id, password));
                return user with { CredentialId = id, Password = null };
            }

            return user.Password is null && user.CredentialId is null && existing.FirstOrDefault(u => u.Username == user.Username)?.CredentialId is { IsEmpty: false } kept
                ? user with { CredentialId = kept }
                : user;
        }).ToList();
        return upsert with { AccessList = list with { BasicAuth = auth with { Users = users } } };
    }

    private async Task<List<PasswordChange>> SetAsync(IReadOnlyList<(PasswordCredentialId Id, string Password)> passwords, CancellationToken cancellationToken)
    {
        List<PasswordChange> changes = [];
        try
        {
            foreach (var (id, password) in passwords)
            {
                changes.Add(await _passwords.SetAsync(id, password.AsMemory(), cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            await _passwords.RevertAsync(changes, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return changes;
    }

    // Never a credential the head names: whatever the draft's list says, a live password is not the draft's to delete.
    private async Task DeleteCredentialsAsync(List<PasswordCredentialId> ids, ConfigDocument head)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var live = AccessListPasswords.Referenced(head);
        foreach (var id in ids.Where(id => !live.Contains(id)))
        {
            try
            {
                await _passwords.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCredentialDeleteFailed(ex, id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped draft {Draft} ({Entries} change(s)): untouched for 30 days.")]
    private partial void LogDropped(DraftId draft, int entries);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A listener of a draft change threw; the change stands.")]
    private partial void LogAnnounceFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete the pending password credential {CredentialId} of a draft; the next start sweeps it.")]
    private partial void LogCredentialDeleteFailed(Exception exception, PasswordCredentialId credentialId);
}
