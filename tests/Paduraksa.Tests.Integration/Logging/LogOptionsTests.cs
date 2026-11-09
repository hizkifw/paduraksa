using Microsoft.Extensions.Options;

using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Logging;

/// <summary>The log settings: validated before anything binds, and the directory resolved against the data directory.</summary>
public sealed class LogOptionsTests
{
    [Theory]
    [InlineData("Paduraksa:Logs:MaxFileSize", "10", "MaxFileSize")]
    [InlineData("Paduraksa:Logs:MaxFileSize", "999999999999999", "MaxFileSize")]
    [InlineData("Paduraksa:Logs:RetainedFiles", "0", "RetainedFiles")]
    [InlineData("Paduraksa:Logs:RetainedFiles", "10001", "RetainedFiles")]
    [InlineData("Paduraksa:Logs:MaxAge", "00:00:00", "MaxAge")]
    [InlineData("Paduraksa:Logs:MaxAge", "4000.00:00:00", "MaxAge")]
    [InlineData("Paduraksa:Logs:MaxTotalSize", "1024", "MaxTotalSize")]
    [InlineData("Paduraksa:Logs:ErrorLevel", "Trace", "ErrorLevel")]
    [InlineData("Paduraksa:Logs:ErrorLevel", "None", "ErrorLevel")]
    [InlineData("Paduraksa:Logs:Directory", "~/logs", "Directory")]
    public void ABadLogSettingIsRefusedAtStartup(string key, string value, string named)
    {
        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?> { [key] = value })));

        Assert.Contains(named, string.Join(" ", exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogsLiveUnderTheDataDirectoryByDefault()
    {
        string data = TestDataDirectories.NewPath();

        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?> { ["Paduraksa:DataDirectory"] = data }));

        Assert.Equal(Path.Combine(data, "logs"), options.Logs.ResolvedDirectory);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, options.Logs.ErrorLevel);
        Assert.Equal(64L * 1024 * 1024, options.Logs.MaxFileSize);
    }

    [Fact]
    public void ARelativeLogsDirectoryIsRelativeToTheDataDirectoryAndAnAbsoluteOneIsKept()
    {
        string data = TestDataDirectories.NewPath();

        var relative = PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:DataDirectory"] = data,
            ["Paduraksa:Logs:Directory"] = "var/logs",
        }));
        var absolute = PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:DataDirectory"] = data,
            ["Paduraksa:Logs:Directory"] = Path.Combine(data, "elsewhere"),
        }));

        Assert.Equal(Path.GetFullPath(Path.Combine(data, "var/logs")), relative.Logs.ResolvedDirectory);
        Assert.Equal(Path.Combine(data, "elsewhere"), absolute.Logs.ResolvedDirectory);
    }
}
