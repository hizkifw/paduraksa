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
}
