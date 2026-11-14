using System.Reflection;
using System.Text.Json;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Tests.Unit.Settings;

/// <summary>
/// The catalogue is the one declaration of each server setting (docs/design/config.md "Adding a server setting"): every property of
/// <see cref="ServerSettings"/> has exactly one, under its JSON name, with a default inside its bounds, and a value it writes reads back.
/// </summary>
public sealed class ServerSettingCatalogTests
{
    private static readonly PropertyInfo[] Properties = [.. typeof(ServerSettings).GetProperties().Where(p => p.Name != nameof(ServerSettings.SetsNothing))];

    [Fact]
    public void EveryPropertyHasExactlyOneDeclarationUnderItsJsonName()
    {
        var names = Properties.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order(StringComparer.Ordinal);

        Assert.Equal(names, ServerSettingCatalog.All.Select(s => s.Key).Order(StringComparer.Ordinal));
        Assert.Equal(ServerSettingCatalog.All.Count, ServerSettingCatalog.All.Select(s => s.ConfigurationKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EachDeclarationReadsAndWritesItsOwnProperty()
    {
        foreach (var setting in ServerSettingCatalog.All)
        {
            var set = setting.WithText(new ServerSettings(), setting.MinText ?? setting.DefaultText).Settings;
            var property = Properties.Single(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name) == setting.Key);

            Assert.True(setting.IsSet(set), setting.Key);
            Assert.NotNull(property.GetValue(set));
            Assert.All(Properties.Where(p => p != property), other => Assert.Null(other.GetValue(set)));
            Assert.False(setting.IsSet(setting.WithText(set, null).Settings), setting.Key);
        }
    }

    [Fact]
    public void EveryDefaultIsInsideItsBoundsAndTheBoundsAreAccepted()
    {
        foreach (var setting in ServerSettingCatalog.All)
        {
            foreach (var text in new[] { setting.DefaultText, setting.MinText, setting.MaxText }.OfType<string>())
            {
                var settings = setting.WithText(new ServerSettings(), text).Settings;
                Assert.Null(setting.Problem(settings));
                Assert.Equal(text, setting.TextOf(settings));
            }
        }
    }

    [Fact]
    public void ConfigurationTextIsReadTheWayTheBinderReadsIt()
    {
        Assert.Equal("90.00:00:00", ServerSettingCatalog.MetricsRetention.FromConfiguration("90.00:00:00"));
        Assert.Equal("1.12:00:00", ServerSettingCatalog.MetricsRetention.FromConfiguration("1.12:00"));
        Assert.Equal("1073741824", ServerSettingCatalog.LogMaxTotalSize.FromConfiguration(" 1073741824 "));
        Assert.Null(ServerSettingCatalog.LogRetainedFiles.FromConfiguration("thirty"));
    }

    [Fact]
    public void TheValidatorHoldsEachStoredValueToItsBounds()
    {
        ServerSettings tooShort = new() { MetricsRetention = TimeSpan.FromMinutes(5), LogMaxTotalSize = 10 };
        var errors = ConfigValidator.Validate(new ConfigDocument(Settings: new GlobalSettings(Server: tooShort)));

        Assert.Equal(
            ["/settings/server/logMaxTotalSize", "/settings/server/metricsRetention"],
            errors.Where(e => e.Code == ConfigErrorCodes.ServerSettingInvalid).Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Contains("from 1 hour to 5 years, was 5 minutes", errors.Single(e => e.Path.EndsWith("metricsRetention", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsRecordThatSetsNothingIsNoKeyInTheCanonicalDocument()
    {
        var json = System.Text.Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(new ConfigDocument(Settings: new GlobalSettings(Server: new ServerSettings()))));
        var set = System.Text.Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(new ConfigDocument(Settings: new GlobalSettings(Server: new ServerSettings { LogRetainedFiles = 5 }))));

        Assert.DoesNotContain("\"server\"", json, StringComparison.Ordinal);
        Assert.Contains("\"server\":{\"logRetainedFiles\":5}", set, StringComparison.Ordinal);
    }

    [Fact]
    public void LoweringARetentionSaysWhatIsDeletedAndRaisingItSaysNothing()
    {
        ServerSettings week = new() { MetricsRetention = TimeSpan.FromDays(7) };

        var lowered = Assert.Single(ServerSettingCatalog.Notices(null, week));
        Assert.Contains("goes down from 90 days to 7 days", lowered, StringComparison.Ordinal);
        Assert.Contains("can't be recovered", lowered, StringComparison.Ordinal);
        Assert.Empty(ServerSettingCatalog.Notices(week, null));
    }
}
