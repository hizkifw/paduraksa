using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Paduraksa.Admin.Auth;

/// <summary>What is being throttled; each has its own budget per client.</summary>
public enum AdminThrottleScope
{
    Login = 1,

    SecondFactor = 2,

    Setup = 3,
}

/// <summary>
/// Rate limiting and load shedding for the credential-checking endpoints, on top of the account lockout (which counts failures per
/// account in the database).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Per client:</b> every attempt, successful or not, takes one permit from the client's fixed window (<see cref="AdminSecurityOptions.RateLimitPermitsPerIp"/>).
/// An IPv6 client is keyed by its /64, the smallest block one subscriber normally holds.</item>
/// <item><b>Per username (or user):</b> only <i>failed</i> attempts count (<see cref="AdminSecurityOptions.RateLimitPermitsPerUser"/>), so a flood of
/// wrong guesses against a name cannot be used to lock the real owner out of signing in with the right password for longer than the
/// window, and the owner's own successful sign-ins never use the budget up.</item>
/// <item><b>Bounded memory:</b> at most <see cref="AdminSecurityOptions.RateLimitMaxTracked"/> keys are live per kind. Expired windows are swept when the
/// table is full; if it is still full, new keys share one overflow bucket (so a flood of distinct keys throttles itself).</item>
/// <item><b>Bounded CPU:</b> <see cref="EnterPasswordCheckAsync"/> lets at most <see cref="AdminSecurityOptions.MaxConcurrentPasswordChecks"/> password
/// derivations run at once; others wait briefly and are then refused, so PBKDF2 cannot be used to burn every core.</item>
/// </list>
/// Windows are in memory and start afresh with the process. Retry-After is the time left in the window that refused the attempt.
/// </remarks>
public sealed class AdminThrottle : IDisposable
{
    private const int MaxSubjectLength = 128;
    private const string OverflowKey = "*overflow*";

    private readonly Counter _byClient;
    private readonly Counter _bySubject;
    private readonly SemaphoreSlim _passwordChecks;
    private readonly TimeSpan _passwordCheckWait;

    public AdminThrottle(AdminSecurityOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        time ??= TimeProvider.System;
        _byClient = new Counter(options.RateLimitPermitsPerIp, options.RateLimitWindow, options.RateLimitMaxTracked, time);
        _bySubject = new Counter(options.RateLimitPermitsPerUser, options.RateLimitWindow, options.RateLimitMaxTracked, time);
        _passwordChecks = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentPasswordChecks));
        _passwordCheckWait = options.PasswordCheckWait;
    }

    /// <summary>
    /// Takes a permit from the client's budget, and checks (without spending) that the subject has not used up its failures. Returns null
    /// when the attempt may go ahead, otherwise how long to wait (a whole number of seconds, at least one).
    /// </summary>
    public TimeSpan? TryEnter(AdminThrottleScope scope, IPAddress? client, string? subject)
    {
        if (_byClient.TryTake(Prefix(scope) + ClientKey(client), consume: true) is { } clientWait)
        {
            return ToRetryAfter(clientWait);
        }

        return SubjectKey(scope, subject) is { } key && _bySubject.TryTake(key, consume: false) is { } subjectWait ? ToRetryAfter(subjectWait) : null;
    }

    /// <summary>Counts a failed attempt against the subject (a wrong password or code, a wrong setup token).</summary>
    public void RecordFailure(AdminThrottleScope scope, string? subject)
    {
        if (SubjectKey(scope, subject) is { } key)
        {
            _bySubject.Add(key);
        }
    }

    /// <summary>
    /// Waits briefly for one of the password-derivation slots. The returned handle gives the slot back when disposed; null means the
    /// server is busy and the attempt should be refused (503) without deriving anything.
    /// </summary>
    public async ValueTask<IDisposable?> EnterPasswordCheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await _passwordChecks.WaitAsync(_passwordCheckWait, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new Slot(_passwordChecks);
    }

    public void Dispose() => _passwordChecks.Dispose();

    /// <summary>The key of a client: an IPv4 address as is, an IPv6 address by its /64.</summary>
    internal static string ClientKey(IPAddress? client)
    {
        if (client is null)
        {
            return "unknown";
        }

        if (client.IsIPv4MappedToIPv6)
        {
            client = client.MapToIPv4();
        }

        if (client.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return client.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        client.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }

    private static string Prefix(AdminThrottleScope scope) => ((int)scope).ToString(CultureInfo.InvariantCulture) + "|";

    private static string? SubjectKey(AdminThrottleScope scope, string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        string normalized = subject.Trim().ToUpperInvariant();
        return Prefix(scope) + (normalized.Length > MaxSubjectLength ? normalized[..MaxSubjectLength] : normalized);
    }

    private static TimeSpan ToRetryAfter(TimeSpan wait) => TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(wait.TotalSeconds)));

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }

    /// <summary>Fixed-window counters by key, bounded in size.</summary>
    private sealed class Counter(int limit, TimeSpan window, int maxTracked, TimeProvider time)
    {
        private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _windows = [];
        private readonly Lock _gate = new();
        private DateTimeOffset _lastSweep;

        /// <summary>The time left until the key's window ends when it has used its permits, else null (taking one if <paramref name="consume"/>).</summary>
        public TimeSpan? TryTake(string key, bool consume)
        {
            lock (_gate)
            {
                DateTimeOffset now = time.GetUtcNow();
                string slot = Resolve(key, now);
                (DateTimeOffset start, int count) = Current(slot, now);
                if (count >= limit)
                {
                    return start + window - now;
                }

                if (consume)
                {
                    _windows[slot] = (start, count + 1);
                }

                return null;
            }
        }

        public void Add(string key)
        {
            lock (_gate)
            {
                DateTimeOffset now = time.GetUtcNow();
                string slot = Resolve(key, now);
                (DateTimeOffset start, int count) = Current(slot, now);
                _windows[slot] = (start, count < int.MaxValue ? count + 1 : count);
            }
        }

        private (DateTimeOffset Start, int Count) Current(string slot, DateTimeOffset now) =>
            _windows.TryGetValue(slot, out (DateTimeOffset Start, int Count) entry) && now - entry.Start < window ? entry : (now, 0);

        /// <summary>The key itself, or the shared overflow bucket when the table is full of live windows.</summary>
        private string Resolve(string key, DateTimeOffset now)
        {
            if (_windows.ContainsKey(key) || _windows.Count < maxTracked)
            {
                return key;
            }

            // At most once a second, so a flood of new keys cannot make every request scan the table.
            if (now - _lastSweep >= TimeSpan.FromSeconds(1))
            {
                _lastSweep = now;
                foreach (string expired in _windows.Where(w => now - w.Value.Start >= window).Select(w => w.Key).ToList())
                {
                    _windows.Remove(expired);
                }
            }

            return _windows.Count < maxTracked ? key : OverflowKey;
        }
    }
}
