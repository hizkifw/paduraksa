using Paduraksa.Application.Settings;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Tests.Unit.Settings;

/// <summary>The precedence of a server setting: the environment's explicit value, then the config's, then the default.</summary>
public sealed class ServerSettingsStateTests
{
    private static readonly ServerSetting<TimeSpan> Metrics = ServerSettingCatalog.MetricsRetention;
    private static readonly ServerSetting<TimeSpan> Analytics = ServerSettingCatalog.AnalyticsRetention;

    [Fact]
    public void TheEnvironmentWinsTheConfigComesNextAndTheDefaultLast()
    {
        ServerSettingsState state = new(new Dictionary<string, string> { [Analytics.Key] = "7.00:00:00" });

        state.Take(new ServerSettings { MetricsRetention = TimeSpan.FromDays(30), AnalyticsRetention = TimeSpan.FromDays(60) });

        Assert.Equal((TimeSpan.FromDays(30), ServerSettingSource.Config), (Metrics.Resolve(state.Current), state.SourceOf(Metrics)));
        Assert.Equal((TimeSpan.FromDays(7), ServerSettingSource.Environment), (Analytics.Resolve(state.Current), state.SourceOf(Analytics)));
        var history = ServerSettingCatalog.NotificationHistoryRetention;
        Assert.Equal((history.Default, ServerSettingSource.Default), (history.Resolve(state.Current), state.SourceOf(history)));
    }

    [Fact]
    public void AnEnvironmentValueEqualToTheDefaultIsStillTheEnvironments()
    {
        ServerSettingsState state = new(new Dictionary<string, string> { [Metrics.Key] = Metrics.DefaultText });

        state.Take(new ServerSettings { MetricsRetention = TimeSpan.FromDays(1) });

        Assert.Equal((Metrics.Default, ServerSettingSource.Environment), (Metrics.Resolve(state.Current), state.SourceOf(Metrics)));
    }

    [Fact]
    public void ChangedIsRaisedOnlyWhenAValueInForceChanges()
    {
        ServerSettingsState state = new(new Dictionary<string, string>());
        List<ServerSettings> seen = [];
        state.Changed += seen.Add;

        state.Take(new ServerSettings { MetricsRetention = TimeSpan.FromDays(30) });
        state.Take(new ServerSettings { MetricsRetention = TimeSpan.FromDays(30) });
        state.Take(null);

        Assert.Equal([TimeSpan.FromDays(30), Metrics.Default], seen.Select(Metrics.Resolve));
    }

    [Fact]
    public void ARestartOnlySettingIsComparedWithWhatTheProcessStartedWith()
    {
        ServerSettingsState state = new(new Dictionary<string, string>());
        state.Take(new ServerSettings { LogMaxAge = TimeSpan.FromDays(2) });
        state.Seal();

        Assert.Equal(TimeSpan.FromDays(2), ServerSettingCatalog.LogMaxAge.Resolve(state.Startup));
        state.Take(null);
        Assert.Equal(TimeSpan.FromDays(2), ServerSettingCatalog.LogMaxAge.Resolve(state.Startup));
        Assert.All(ServerSettingCatalog.All.Where(s => s.Apply == ServerSettingApply.Live), s => Assert.False(state.RestartPending(s)));
    }

    private static readonly ServerSetting<TimeSpan> Idle = ServerSettingCatalog.AdminSessionIdleTimeout;
    private static readonly ServerSetting<TimeSpan> Lifetime = ServerSettingCatalog.AdminSessionLifetime;

    [Fact]
    public void AStoredLifetimeShorterThanTheEnvironmentsIdleTimeoutFallsBackToItsDefault()
    {
        ServerSettingsState state = new(new Dictionary<string, string> { [Idle.Key] = "00:05:00" });

        state.Take(new ServerSettings { AdminSessionLifetime = TimeSpan.FromMinutes(3) });

        Assert.Equal((TimeSpan.FromMinutes(5), ServerSettingSource.Environment), (Idle.Resolve(state.Current), state.SourceOf(Idle)));
        Assert.Equal((Lifetime.Default, ServerSettingSource.Default), (Lifetime.Resolve(state.Current), state.SourceOf(Lifetime)));
        Assert.All(ServerSettingCatalog.Rules, rule => Assert.Null(rule.Check(state.Current)));
    }

    [Fact]
    public void AStoredIdleTimeoutLongerThanTheEnvironmentsLifetimeIsDroppedAndSettlingEnds()
    {
        ServerSettingsState state = new(new Dictionary<string, string> { [Lifetime.Key] = "00:10:00" });

        state.Take(new ServerSettings { AdminSessionIdleTimeout = TimeSpan.FromHours(2) });

        // Nothing stored is left in the rule: the default idle timeout against the environment's lifetime is the environment's own mix, which
        // the options validator refuses at startup, so a running server never has it. A session is held to both, so the lifetime governs.
        Assert.Equal(ServerSettingSource.Default, state.SourceOf(Idle));
        Assert.Equal((TimeSpan.FromMinutes(10), ServerSettingSource.Environment), (Lifetime.Resolve(state.Current), state.SourceOf(Lifetime)));
    }

    [Fact]
    public void WhenBothSidesOfABrokenRuleAreStoredBothFallBack()
    {
        ServerSettingsState state = new(new Dictionary<string, string>());

        // The validator refuses this pair; the state settles it anyway, so nothing it hands on breaks a rule.
        state.Take(new ServerSettings { AdminSessionIdleTimeout = TimeSpan.FromHours(3), AdminSessionLifetime = TimeSpan.FromHours(1) });

        Assert.Equal((ServerSettingSource.Default, ServerSettingSource.Default), (state.SourceOf(Idle), state.SourceOf(Lifetime)));
        Assert.All(ServerSettingCatalog.Rules, rule => Assert.Null(rule.Check(state.Current)));
    }
}
