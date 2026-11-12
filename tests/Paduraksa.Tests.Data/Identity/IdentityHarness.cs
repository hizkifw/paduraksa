using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data.Audit;
using Paduraksa.Tests.Data.Config;

namespace Paduraksa.Tests.Data.Identity;

/// <summary>
/// The identity services over a real config database, a manual clock and a fake vault TOTP (the real one is tested in
/// <c>Paduraksa.Tests.Vault</c>), wired by the real registration (<c>AddPaduraksaIdentity</c>). Passwords use a low iteration count.
/// </summary>
internal sealed class IdentityHarness : IAsyncDisposable
{
    public const string Password = "correct horse battery staple";

    private readonly ServiceProvider _services;

    private readonly bool _ownsDatabase;

    private IdentityHarness(TestConfigDatabase db, IdentityOptions options, bool ownsDatabase)
    {
        Db = db;
        _ownsDatabase = ownsDatabase;
        Options = options;
        DataDirectory = Directory.CreateTempSubdirectory("paduraksa-identity-").FullName;
        _services = new ServiceCollection()
            .AddSingleton(db.Factory)
            .AddSingleton<TimeProvider>(Time)
            .AddSingleton<ITotpAuthenticator>(Totp)
            .AddSingleton<INotificationEvents>(Events)
            .AddSingleton(new NotificationOptions())
            .AddSingleton(sp => new SignInWatch(sp.GetRequiredService<INotificationEvents>(), sp.GetRequiredService<NotificationOptions>(), sp.GetRequiredService<TimeProvider>()))
            .AddPaduraksaIdentity(DataDirectory, options)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TestConfigDatabase Db { get; }

    public IdentityOptions Options { get; }

    public string DataDirectory { get; }

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    public FakeTotp Totp { get; } = new();

    /// <summary>The sign-in anomalies the services published.</summary>
    public RecordingEvents Events { get; } = new();

    public ClientContext Client { get; } = new(Channel.Dashboard, IPAddress.Parse("203.0.113.9"), "test-agent/1.0");

    public UserService Users => Get<UserService>();

    public SessionService Sessions => Get<SessionService>();

    public LoginService Login => Get<LoginService>();

    public TwoFactorService TwoFactor => Get<TwoFactorService>();

    public ApiTokenService Tokens => Get<ApiTokenService>();

    public SetupService Setup => Get<SetupService>();

    public IPermissionEvaluator Permissions => Get<IPermissionEvaluator>();

    public static async Task<IdentityHarness> CreateAsync(IdentityOptions? options = null) =>
        new(await TestConfigDatabase.CreateAsync(), options ?? Fast(), ownsDatabase: true);

    /// <summary>The identity services over a database someone else owns (and disposes).</summary>
    public static IdentityHarness Over(TestConfigDatabase db, IdentityOptions? options = null) => new(db, options ?? Fast(), ownsDatabase: false);

    /// <summary>The defaults with a cheap password hash, so tests do not spend seconds in PBKDF2.</summary>
    public static IdentityOptions Fast() => new() { PasswordIterations = 1_000 };

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    /// <summary>Creates a user as the system would (the CLI path) and returns it.</summary>
    public async Task<UserSummary> CreateUserAsync(string username, Role role = Role.Viewer, string password = Password)
    {
        var result = await Users.CreateAsync(CommandOrigin.System, new CreateUserRequest(username, null, password, [role]), Ct);
        Assert.True(result.Succeeded, result.Problem);
        return result.User!;
    }

    /// <summary>A user's origin as a signed-in surface would build it.</summary>
    public CommandOrigin As(UserSummary user) => Client.ToOrigin(Actor.ForUser(user.Id));

    /// <summary>Signs in with the right password and returns the full session (the user must have no second factor).</summary>
    public async Task<SessionGrant> SignInAsync(string username, string password = Password)
    {
        var result = await Login.LoginAsync(username, password, Client, Ct);
        Assert.Equal(LoginStatus.Succeeded, result.Status);
        return result.Session!;
    }

    public async Task<List<AuditEvent>> AuditAsync()
    {
        await using var context = Db.NewContext();
        return await context.AuditEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync(Ct);
    }

    public async Task<List<AuditEvent>> AuditAsync(string action) => [.. (await AuditAsync()).Where(e => e.Action == action)];

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (_ownsDatabase)
        {
            await Db.DisposeAsync();
        }

        Directory.Delete(DataDirectory, recursive: true);
    }
}

/// <summary>The notification dispatcher's front door, recording what was published.</summary>
internal sealed class RecordingEvents : INotificationEvents
{
    private readonly List<NotificationEvent> _published = [];

    public IReadOnlyList<NotificationEvent> Published
    {
        get
        {
            lock (_published)
            {
                return [.. _published];
            }
        }
    }

    public void Publish(NotificationEvent notification)
    {
        lock (_published)
        {
            _published.Add(notification);
        }
    }

    public Task<bool> HasSubscribersAsync(NotificationEventKind kind, CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>A vault stand-in: one valid code per secret, usable once. <see cref="Rotate"/> moves to the next.</summary>
internal sealed class FakeTotp : ITotpAuthenticator
{
    private readonly Dictionary<SecretId, State> _secrets = [];

    public int Count => _secrets.Count;

    /// <summary>The secrets in the order they were created.</summary>
    public IReadOnlyList<SecretId> SecretIds => [.. _secrets.Keys.OrderBy(k => _secrets[k].Order)];

    /// <summary>Run at the start of <see cref="VerifyAsync"/>: a test moves its clock here, between the service's two reads.</summary>
    public Action? OnVerify { get; set; }

    public bool Exists(SecretId id) => _secrets.ContainsKey(id);

    /// <summary>The code that would be accepted for <paramref name="id"/> now.</summary>
    public string CodeFor(SecretId id) => _secrets[id].Code;

    public bool IsConfirmed(SecretId id) => _secrets[id].Confirmed;

    /// <summary>Moves a secret on to its next code, as the next time step does.</summary>
    public void Rotate(SecretId id)
    {
        var state = _secrets[id];
        state.Counter++;
    }

    public ValueTask<TotpEnrollment> BeginEnrollmentAsync(string accountName, string issuer, CancellationToken cancellationToken)
    {
        var id = SecretId.New();
        _secrets[id] = new State { Order = _secrets.Count == 0 ? 0 : _secrets.Values.Max(v => v.Order) + 1 };
        return ValueTask.FromResult(new TotpEnrollment(id, new Uri($"otpauth://totp/{issuer}:{accountName}?secret=FAKE&issuer={issuer}")));
    }

    public ValueTask<bool> ConfirmEnrollmentAsync(SecretId id, string code, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_secrets.TryGetValue(id, out var state) && !state.Confirmed && state.Accept(code) && (state.Confirmed = true));

    public ValueTask<bool> VerifyAsync(SecretId id, string code, CancellationToken cancellationToken)
    {
        OnVerify?.Invoke();
        return ValueTask.FromResult(_secrets.TryGetValue(id, out var state) && state.Confirmed && state.Accept(code));
    }

    public ValueTask<bool> DeleteAsync(SecretId id, CancellationToken cancellationToken) => ValueTask.FromResult(_secrets.Remove(id));

    private sealed class State
    {
        public int Order { get; init; }

        public int Counter { get; set; } = 100_000;

        public int LastUsed { get; set; }

        public bool Confirmed { get; set; }

        public string Code => Counter.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public bool Accept(string code)
        {
            if (code != Code || LastUsed >= Counter)
            {
                return false;
            }

            LastUsed = Counter;
            return true;
        }
    }
}
