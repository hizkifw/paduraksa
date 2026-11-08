using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Audit;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Identity;

namespace Paduraksa.Application.Identity;

/// <summary>How first-run setup ended.</summary>
public enum SetupOutcome
{
    /// <summary>The first Owner was created; setup is complete and the token is gone.</summary>
    Succeeded = 1,

    /// <summary>An Owner already exists: setup is over and its token no longer works.</summary>
    AlreadyComplete = 2,

    /// <summary>The setup token is wrong or missing. Audited.</summary>
    InvalidToken = 3,

    /// <summary>The username, display name or password was refused; <c>Problem</c> says why.</summary>
    Invalid = 4,
}

/// <summary>The outcome of <see cref="SetupService.CompleteSetupAsync"/>.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Owner">The new Owner, for <see cref="SetupOutcome.Succeeded"/>.</param>
/// <param name="Problem">Why the input was refused (for <see cref="SetupOutcome.Invalid"/>), safe to show.</param>
public sealed record SetupResult(SetupOutcome Outcome, UserSummary? Owner = null, string? Problem = null);

/// <summary>
/// First-run setup (PLAN §12.1). Setup is <i>incomplete</i> while no Owner exists. Until then the admin app serves only the setup
/// flow, and that flow needs a one-time <b>setup token</b>: 256 random bits, kept in <c>&lt;data dir&gt;/setup-token</c> (owner-only,
/// like <c>master.key</c>), logged at Warning when the server starts, and reprinted by <c>paduraksa setup-token</c>. Whoever can
/// read the log, the file or the shell on the host can claim the instance; nobody on the network can.
/// </summary>
/// <remarks>
/// Creating the Owner is atomic: the check that no Owner exists and the insert are one write transaction, so two simultaneous
/// attempts cannot make two. The token is compared in constant time and is deleted when setup completes. The last-Owner guard
/// (<see cref="UserService"/>) means an instance that is complete stays complete.
/// </remarks>
public sealed partial class SetupService
{
    /// <summary>The token's file name in the data directory.</summary>
    public const string TokenFileName = "setup-token";

    private const int TokenBytes = 32;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly TimeProvider _time;
    private readonly PasswordHasher _hasher;
    private readonly IdentityAudit _audit;
    private readonly UserService _users;
    private readonly string _tokenPath;
    private readonly ILogger _logger;

    private volatile bool _complete;

    internal SetupService(
        IDbContextFactory<ConfigDbContext> contexts,
        TimeProvider time,
        PasswordHasher hasher,
        IdentityAudit audit,
        UserService users,
        string dataDirectory,
        ILogger<SetupService> logger)
    {
        _contexts = contexts;
        _time = time;
        _hasher = hasher;
        _audit = audit;
        _users = users;
        _tokenPath = Path.Combine(dataDirectory, TokenFileName);
        _logger = logger;
    }

    /// <summary>The path of the setup-token file.</summary>
    public string TokenPath => _tokenPath;

    /// <summary>Whether an Owner exists. Once true it stays true, and this stops asking the database.</summary>
    public async Task<bool> IsCompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_complete)
        {
            return true;
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            _complete = await context.RoleAssignments.AsNoTracking().AnyAsync(r => r.Role == Role.Owner, cancellationToken).ConfigureAwait(false);
            return _complete;
        }
    }

    /// <summary>The setup status for the admin app.</summary>
    public async Task<SetupStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        new(await IsCompleteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The setup token while setup is incomplete (creating the file if there is none), or null once it is complete (a stale file is
    /// removed then). A file that other users can read is not trusted: it is replaced.
    /// </summary>
    public async Task<string?> EnsureTokenAsync(CancellationToken cancellationToken = default)
    {
        if (await IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            DeleteTokenFile();
            return null;
        }

        return ReadToken() ?? CreateToken();
    }

    /// <summary>
    /// <see cref="EnsureTokenAsync"/> for server startup: also logs the token at Warning, so it appears where the operator is
    /// looking, with how to show it again.
    /// </summary>
    public async Task AnnounceAsync(CancellationToken cancellationToken = default)
    {
        if (await EnsureTokenAsync(cancellationToken).ConfigureAwait(false) is { } token)
        {
            // The one deliberate exception to "never log a credential": whoever runs `docker logs` has no other way to get the token,
            // it is single-use, and it is worthless once an Owner exists (and then deleted).
            LogSetupToken(token, _tokenPath);
        }
    }

    /// <summary>
    /// Creates the first Owner. Needs the setup token; refused once an Owner exists. The new user is not signed in: the admin app
    /// sends them to the sign-in page.
    /// </summary>
    public async Task<SetupResult> CompleteSetupAsync(
        string setupToken, string username, string password, string? displayName, ClientContext client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var system = client.ToOrigin(Actor.System);
        if (await IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SetupResult(SetupOutcome.AlreadyComplete);
        }

        if (!TokenMatches(setupToken))
        {
            await _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(system, "setup.complete", AuditOutcome.Denied, null, w => w.WriteString("reason", "invalid_token"))).ConfigureAwait(false);
            return new SetupResult(SetupOutcome.InvalidToken);
        }

        var request = new CreateUserRequest(username, displayName, password, [Role.Owner]);
        if (_users.CheckNewUser(request, out var name, out var display, out _) is { } problem)
        {
            return new SetupResult(SetupOutcome.Invalid, Problem: problem);
        }

        var now = _time.GetUtcNow();
        var row = new UserRow { Id = UserId.New(), Username = name, DisplayName = display, PasswordHash = _hasher.Hash(password), CreatedAt = now, UpdatedAt = now };
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                // Inside the write lock: of two simultaneous attempts, the second sees the first's Owner.
                if (await context.RoleAssignments.AnyAsync(r => r.Role == Role.Owner, cancellationToken).ConfigureAwait(false))
                {
                    _complete = true;
                    return new SetupResult(SetupOutcome.AlreadyComplete);
                }

                context.Users.Add(row);
                context.RoleAssignments.Add(new RoleAssignmentRow { UserId = row.Id, Role = Role.Owner });
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(system, "setup.complete", AuditOutcome.Success, row.Id.ToString(), w => w.WriteString("username", name)),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _complete = true;
        DeleteTokenFile();
        return new SetupResult(SetupOutcome.Succeeded, IdentityMapping.ToSummary(row, [Role.Owner], now));
    }

    private bool TokenMatches(string? presented)
    {
        var expected = ReadToken();
        if (expected is null || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented.Trim()), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>The stored token, or null if there is no usable file (missing, empty, or readable by others).</summary>
    private string? ReadToken()
    {
        try
        {
            if (!File.Exists(_tokenPath))
            {
                return null;
            }

            if (!OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(_tokenPath) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
            {
                LogTokenFileTooOpen(_tokenPath);
                File.Delete(_tokenPath);
                return null;
            }

            var token = File.ReadAllText(_tokenPath).Trim();
            return token.Length == 0 ? null : token;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogTokenFileUnreadable(ex, _tokenPath);
            return null;
        }
    }

    private string CreateToken()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using var stream = new FileStream(_tokenPath, options);
            stream.Write(Encoding.UTF8.GetBytes(token + "\n"));
        }
        catch (IOException) when (File.Exists(_tokenPath))
        {
            // Another process created it first (the CLI beside the server): theirs is the token.
            return ReadToken() ?? throw new IOException($"The setup token file {_tokenPath} is unusable.");
        }

        return token;
    }

    private void DeleteTokenFile()
    {
        try
        {
            File.Delete(_tokenPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogTokenFileUnreadable(ex, _tokenPath);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup is not complete: no Owner exists. Open the admin app and use this setup token to create one: {Token} (kept in {Path}; `paduraksa setup-token` shows it again).")]
    private partial void LogSetupToken(string token, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The setup token file {Path} was readable by other users, so it was discarded.")]
    private partial void LogTokenFileTooOpen(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The setup token file {Path} could not be read or removed.")]
    private partial void LogTokenFileUnreadable(Exception exception, string path);
}
