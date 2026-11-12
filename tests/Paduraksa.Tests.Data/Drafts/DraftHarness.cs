using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Drafts;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using Paduraksa.Data.Identity;
using Paduraksa.Tests.Data.Commands;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Drafts;

/// <summary>
/// The draft service over a <see cref="ConfigServiceHarness"/> (real database, the stand-in compiler) with a clock of its own, and two users with
/// rows in the database (a draft belongs to a user, and the table has the foreign key).
/// </summary>
internal sealed class DraftHarness : IAsyncDisposable
{
    private DraftHarness(ConfigServiceHarness config, ManualTimeProvider time)
    {
        Config = config;
        Time = time;
        Drafts = Create(config, time);
    }

    public ConfigServiceHarness Config { get; }

    public ManualTimeProvider Time { get; }

    public DraftService Drafts { get; }

    public CommandOrigin Alice { get; } = Origin();

    public CommandOrigin Bob { get; } = Origin();

    public static async Task<DraftHarness> CreateAsync()
    {
        var config = await ConfigServiceHarness.CreateAsync();
        DraftHarness harness = new(config, new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)));
        await harness.AddUserAsync(harness.Alice);
        await harness.AddUserAsync(harness.Bob);
        return harness;
    }

    /// <summary>A second service over the same database: the process after a restart.</summary>
    public DraftService Restarted() => Create(Config, Time);

    public async Task<DraftResult> OpenAsync(CommandOrigin? who = null)
    {
        var result = await Drafts.CreateAsync(who ?? Alice, Ct);
        Assert.Equal(DraftStatus.Succeeded, result.Status);
        return result;
    }

    public async Task<DraftResult> AddAsync(DraftId id, ConfigCommand command, CommandOrigin? who = null) =>
        await Drafts.AddAsync(who ?? Alice, id, [command], Ct);

    /// <summary>A review of the draft, and the token an apply of exactly it sends.</summary>
    public async Task<string> TokenAsync(DraftId id, CommandOrigin? who = null)
    {
        var review = await Drafts.ReviewAsync(who ?? Alice, id, cancellationToken: Ct);
        Assert.Equal(DraftStatus.Succeeded, review.Status);
        return Assert.IsType<string>(review.ReviewToken);
    }

    /// <summary>Reviews the draft and applies what the review showed: what a person does who confirms it at once.</summary>
    public async Task<DraftResult> ApplyAsync(DraftId id, string? message = null, CommandOrigin? who = null) =>
        await Drafts.ApplyAsync(who ?? Alice, id, await TokenAsync(id, who), message, cancellationToken: Ct);

    public async Task<long> RowsAsync(string table) => await Config.CountAsync(table);

    public async ValueTask DisposeAsync() => await Config.DisposeAsync();

    private static DraftService Create(ConfigServiceHarness config, TimeProvider time) =>
        new(config.Db.Factory, config.Service, config.Passwords, TestPermissions.AllowAll, time, NullLoggerFactory.Instance);

    private static CommandOrigin Origin() => new(Actor.ForUser(UserId.New()), Channel.Dashboard, System.Net.IPAddress.Loopback);

    public async Task AddUserAsync(CommandOrigin origin)
    {
        await using var context = Config.Db.NewContext();
        context.Users.Add(new UserRow
        {
            Id = origin.Actor.UserId!.Value,
            Username = "user-" + origin.Actor.UserId.Value,
            DisplayName = "Test",
            PasswordHash = "pbkdf2-sha512$v1$...",
            CreatedAt = Time.GetUtcNow(),
            UpdatedAt = Time.GetUtcNow(),
        });
        await context.SaveChangesAsync(Ct);
    }
}
