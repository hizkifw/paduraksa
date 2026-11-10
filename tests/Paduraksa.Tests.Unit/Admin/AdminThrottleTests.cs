using System.Net;

using Paduraksa.Admin.Auth;

namespace Paduraksa.Tests.Unit.Admin;

public sealed class AdminThrottleTests
{
    private static readonly IPAddress ClientA = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress ClientB = IPAddress.Parse("192.0.2.2");

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static AdminThrottle Create(ManualTime time, int perIp = 100, int perUser = 100, int maxTracked = 10_000, int concurrent = 4) => new(
        new AdminSecurityOptions
        {
            RateLimitPermitsPerIp = perIp,
            RateLimitPermitsPerUser = perUser,
            RateLimitWindow = TimeSpan.FromMinutes(1),
            RateLimitMaxTracked = maxTracked,
            MaxConcurrentPasswordChecks = concurrent,
            PasswordCheckWait = TimeSpan.FromMilliseconds(50),
        },
        time);

    [Fact]
    public void AClientGetsItsPermitsThenTheTimeLeftInTheWindow()
    {
        ManualTime time = new();
        using var throttle = Create(time, perIp: 2);

        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, ClientA, "a"));
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, ClientA, "b"));
        var wait = throttle.TryEnter(AdminThrottleScope.Login, ClientA, "c");

        Assert.Equal(TimeSpan.FromSeconds(40), wait);
        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, ClientB, "d"));

        time.Advance(TimeSpan.FromSeconds(41));
        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, ClientA, "e"));
    }

    [Fact]
    public void OnlyFailuresCountAgainstASubject_AndTheNameIsNormalised()
    {
        ManualTime time = new();
        using var throttle = Create(time, perUser: 2);

        for (var i = 0; i < 10; i++)
        {
            Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, ClientA, "Alice")); // attempts alone never use the budget
        }

        throttle.RecordFailure(AdminThrottleScope.Login, "Alice");
        throttle.RecordFailure(AdminThrottleScope.Login, "  alice ");

        Assert.NotNull(throttle.TryEnter(AdminThrottleScope.Login, ClientB, "ALICE"));
        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, ClientB, "bob"));
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, ClientB, "alice"));
    }

    [Fact]
    public void ScopesHaveBudgetsOfTheirOwn()
    {
        ManualTime time = new();
        using var throttle = Create(time, perIp: 1, perUser: 1);

        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, ClientA, "alice"));
        Assert.NotNull(throttle.TryEnter(AdminThrottleScope.Login, ClientA, "alice"));
        Assert.Null(throttle.TryEnter(AdminThrottleScope.SecondFactor, ClientA, "alice"));
        Assert.Null(throttle.TryEnter(AdminThrottleScope.Setup, ClientA, "alice"));
    }

    [Fact]
    public void AnIPv6ClientIsAOneSixtyFourAndAMappedIPv4IsAnIPv4()
    {
        Assert.Equal(AdminThrottle.ClientKey(IPAddress.Parse("2001:db8:1:2::1")), AdminThrottle.ClientKey(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff")));
        Assert.NotEqual(AdminThrottle.ClientKey(IPAddress.Parse("2001:db8:1:2::1")), AdminThrottle.ClientKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("192.0.2.1", AdminThrottle.ClientKey(IPAddress.Parse("::ffff:192.0.2.1")));
        Assert.Equal("unknown", AdminThrottle.ClientKey(null));

        ManualTime time = new();
        using var throttle = Create(time, perIp: 1);
        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, IPAddress.Parse("2001:db8:1:2::1"), null));
        Assert.NotNull(throttle.TryEnter(AdminThrottleScope.Login, IPAddress.Parse("2001:db8:1:2::2"), null));
    }

    [Fact]
    public void TrackedKeysAreBounded_PastTheCapNewKeysShareOneBucket_UntilWindowsExpire()
    {
        ManualTime time = new();
        using var throttle = Create(time, perIp: 1, maxTracked: 3);

        for (var i = 1; i <= 3; i++)
        {
            Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, IPAddress.Parse("192.0.2." + i), null));
        }

        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, IPAddress.Parse("192.0.2.4"), null)); // the overflow bucket's one permit
        Assert.NotNull(throttle.TryEnter(AdminThrottleScope.Login, IPAddress.Parse("192.0.2.5"), null)); // shared with the one before
        Assert.NotNull(throttle.TryEnter(AdminThrottleScope.Login, IPAddress.Parse("192.0.2.1"), null)); // a known key keeps its own window

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, IPAddress.Parse("192.0.2.6"), null)); // swept, so it gets a bucket of its own
        Assert.Null(throttle.TryEnter(AdminThrottleScope.Login, IPAddress.Parse("192.0.2.7"), null));
    }

    [Fact]
    public async Task PasswordChecksAreLimitedInNumber_ExcessWaitsThenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var throttle = Create(new ManualTime(), concurrent: 2);

        var first = await throttle.EnterPasswordCheckAsync(ct);
        var second = await throttle.EnterPasswordCheckAsync(ct);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(await throttle.EnterPasswordCheckAsync(ct));

        first.Dispose();
        first.Dispose(); // giving a slot back twice must not make a third
        var third = await throttle.EnterPasswordCheckAsync(ct);
        Assert.NotNull(third);
        Assert.Null(await throttle.EnterPasswordCheckAsync(ct));

        second.Dispose();
        third.Dispose();
        Assert.NotNull(await throttle.EnterPasswordCheckAsync(ct));
    }
}
