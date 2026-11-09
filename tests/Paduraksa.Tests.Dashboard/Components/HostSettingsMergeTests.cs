using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The form's inherited values (PLAN §5.3): `HostSettingsModel.Layer` folds the layers with the compiler's own merge, so
/// a record whose fields come from different layers keeps all of them, exactly as the proxy resolves them. The Unit
/// inheritance harness makes the same assertion through the compiler; this one holds the form to it.
/// </summary>
public sealed class HostSettingsMergeTests
{
    [Fact]
    public void ANestedRecordsFieldsKeepTheirOwnLayersWhenTheFormFoldsThem()
    {
        // Defaults, profile and host each set a different field of the same record.
        HostSettings defaults = new()
        {
            Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls13 },
            Hsts = new HstsSettings { Enabled = true },
            ServerHeader = new ServerHeaderSettings { Value = "edge/1.0" },
        };
        HostSettings profile = new()
        {
            Tls = new TlsSettings { TcpAlpn = TcpAlpn.Http11Only },
            Hsts = new HstsSettings { MaxAge = 86_400 },
            ServerHeader = new ServerHeaderSettings { Value = "edge/2.0" },
        };
        HostSettings host = new()
        {
            Tls = new TlsSettings { Http3 = false },
            Hsts = new HstsSettings { IncludeSubDomains = true, Preload = true },
            ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Remove },
        };

        HostSettings resolved = Fold(Fold(Fold(HostSettingsModel.BuiltIn, defaults), profile), host);

        Assert.Equal(TlsMinimumVersion.Tls13, resolved.Tls!.MinimumVersion);
        Assert.Equal(TcpAlpn.Http11Only, resolved.Tls.TcpAlpn);
        Assert.False(resolved.Tls.Http3);

        Assert.True(resolved.Hsts!.Enabled);
        Assert.Equal(86_400, resolved.Hsts.MaxAge);
        Assert.True(resolved.Hsts.IncludeSubDomains);
        Assert.True(resolved.Hsts.Preload);

        // The profile's value survives the host's mode: the case an earlier hand-written test pinned.
        Assert.Equal(ServerHeaderMode.Remove, resolved.ServerHeader!.Mode);
        Assert.Equal("edge/2.0", resolved.ServerHeader.Value);
    }

    [Fact]
    public void TheBuiltInDefaultsAreTheProxyOnes()
    {
        // The form reads the one place the built-in defaults are stated: it can't promise a default the proxy wouldn't apply.
        Assert.Equal(Paduraksa.Application.Compilation.HostSettingsLayers.BuiltIn, HostSettingsModel.BuiltIn);
    }

    /// <summary><paramref name="upper"/> over <paramref name="lower"/>: the form folding its layers.</summary>
    private static HostSettings Fold(HostSettings lower, HostSettings upper) => HostSettingsModel.Layer(upper, lower);
}
