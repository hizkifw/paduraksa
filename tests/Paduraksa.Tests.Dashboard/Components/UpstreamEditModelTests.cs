using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class UpstreamEditModelTests
{
    private static readonly UpstreamConfig Nas = new(UpstreamId.New(), "nas", Destinations: [new DestinationConfig(DestinationId.New(), "https://nas:8443")]);

    [Fact]
    public void TheDefaultTrustStaysOutOfTheDocument() =>
        Assert.Null(UpstreamEditModel.From(Nas).ToConfig().Tls);

    [Fact]
    public void PinsRoundTripOnePerLine()
    {
        string pin = new('a', 64);
        var model = UpstreamEditModel.From(Nas with { Tls = new UpstreamTlsConfig(UpstreamTlsTrust.Pinned, [pin]) });
        Assert.Equal(UpstreamTlsTrust.Pinned, model.TlsTrust);
        Assert.Equal(pin, model.PinnedSha256);

        model.PinnedSha256 = $"  {pin}  \n\n{new string('b', 64)}\n";

        var tls = model.ToConfig().Tls;
        Assert.NotNull(tls);
        Assert.Equal(UpstreamTlsTrust.Pinned, tls.Trust);
        Assert.Equal([pin, new string('b', 64)], tls.PinnedSha256);
    }

    [Fact]
    public void AnyTrustIsKeptWithoutPins()
    {
        var model = UpstreamEditModel.From(Nas);
        model.TlsTrust = UpstreamTlsTrust.Any;

        Assert.Equal(new UpstreamTlsConfig(UpstreamTlsTrust.Any), model.ToConfig().Tls);
    }

    [Fact]
    public void LeavingPinnedTrustDropsThePinsButTheFieldKeepsThem()
    {
        // The validator refuses pins under any other trust, so switching from pinned to any had to be refused.
        string pin = new('a', 64);
        var model = UpstreamEditModel.From(Nas with { Tls = new UpstreamTlsConfig(UpstreamTlsTrust.Pinned, [pin]) });

        model.TlsTrust = UpstreamTlsTrust.Any;
        Assert.Equal(new UpstreamTlsConfig(UpstreamTlsTrust.Any), model.ToConfig().Tls);

        model.TlsTrust = UpstreamTlsTrust.System;
        Assert.Null(model.ToConfig().Tls);

        model.TlsTrust = UpstreamTlsTrust.Pinned;
        Assert.Equal([pin], model.ToConfig().Tls!.PinnedSha256);
    }

    [Fact]
    public void TheClientsHostIsTheDefaultAndStaysOutOfTheDocument()
    {
        Assert.Null(UpstreamEditModel.From(Nas).ToConfig().HostHeader);
        Assert.Null(UpstreamEditModel.From(Nas with { HostHeader = new UpstreamHostHeaderConfig() }).ToConfig().HostHeader);
    }

    [Fact]
    public void ACustomNameRoundTripsTrimmedAndTheFieldKeepsItWhileAnotherModeIsPicked()
    {
        var model = UpstreamEditModel.From(Nas with { HostHeader = new UpstreamHostHeaderConfig(UpstreamHostHeaderMode.Custom, "nas.internal:8443") });
        Assert.Equal(UpstreamHostHeaderMode.Custom, model.HostHeaderMode);

        model.HostHeaderValue = "  nas.internal  ";
        Assert.Equal(new UpstreamHostHeaderConfig(UpstreamHostHeaderMode.Custom, "nas.internal"), model.ToConfig().HostHeader);

        model.HostHeaderMode = UpstreamHostHeaderMode.Destination;
        Assert.Equal(new UpstreamHostHeaderConfig(UpstreamHostHeaderMode.Destination), model.ToConfig().HostHeader);

        model.HostHeaderMode = UpstreamHostHeaderMode.Custom;
        Assert.Equal("nas.internal", model.ToConfig().HostHeader!.Value);
    }

    [Theory]
    [InlineData(UpstreamHostHeaderMode.Original, null, new[] { null, "app.example.com" })]
    [InlineData(UpstreamHostHeaderMode.Destination, null, new string?[] { null })]
    [InlineData(UpstreamHostHeaderMode.Custom, "nas.internal:8443", new[] { "nas.internal" })]
    [InlineData(UpstreamHostHeaderMode.Custom, "nas.internal", new[] { "nas.internal" })]
    [InlineData(UpstreamHostHeaderMode.Custom, "192.0.2.7:8443", new string?[] { null })]
    [InlineData(UpstreamHostHeaderMode.Custom, "[::1]:8443", new string?[] { null })]
    public void ThePinFetchAsksForTheNameTheProxyWillSendAsSni(UpstreamHostHeaderMode mode, string? value, string?[] expected)
    {
        var model = UpstreamEditModel.From(Nas);
        model.HostHeaderMode = mode;
        model.HostHeaderValue = value ?? string.Empty;

        Assert.Equal(expected, model.SniNames(["app.example.com"]));
    }
}
