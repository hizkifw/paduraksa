using System.Net.Sockets;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

using Paduraksa.Server.Hosting;
using Paduraksa.Server.Logging;

namespace Paduraksa.Tests.Integration.Logging;

/// <summary>
/// A client that vanished mid-write is ordinary traffic: Kestrel's Error entries for it become one Debug line and a count, while
/// every other error keeps its level and its stack. The chains are the ones production logged.
/// </summary>
public sealed class PeerGoneLogFilterTests
{
    private const string Kestrel = "Microsoft.AspNetCore.Server.Kestrel";

    private readonly PeerGoneLogFilter _filter = new();

    // What production logged: the TLS write's IOException over OpenSSL's SSL_ERROR_SYSCALL, both at the GOAWAY flush and at dispose.
    private static IOException EncryptFailed() =>
        new("The encryption operation failed, see inner exception.", new Interop.OpenSsl.SslException("Encrypt failed with OpenSSL error - SSL_ERROR_SYSCALL."));

    public static TheoryData<Exception> PeerGone() =>
    [
        EncryptFailed(),
        new IOException("Unable to write data to the transport connection.", new Interop.OpenSsl.SslException("Operation failed with error - 5")),
        new IOException("x", new SocketException((int)SocketError.TimedOut)),
        new IOException("x", new SocketException((int)SocketError.ConnectionReset)),
        new IOException("x", new SocketException((int)SocketError.OperationAborted)),
        new SocketException((int)SocketError.ConnectionAborted),
        new ConnectionResetException("The client reset the request stream."),
        new IOException("outer", new IOException("inner", new SocketException((int)SocketError.Shutdown))),
    ];

    public static TheoryData<Exception> NotPeerGone() =>
    [
        new InvalidOperationException("a bug", new SocketException((int)SocketError.ConnectionReset)),
        new IOException("disk full"),
        new IOException("x", new SocketException((int)SocketError.AddressAlreadyInUse)),
        new IOException("x", new Interop.OpenSsl.SslException("SSL_ERROR_SSL: certificate verify failed")),
        new IOException("x", new UnauthorizedAccessException()),
    ];

    [Theory]
    [MemberData(nameof(PeerGone))]
    public void AKestrelErrorWhoseChainIsAClientGoingAwayIsQuietedAndCounted(Exception exception)
    {
        Assert.True(_filter.IsPeerGone(Kestrel, LogLevel.Error, exception, out var cause));
        Assert.NotEmpty(cause);
        Assert.Equal(1, _filter.Count);
    }

    [Theory]
    [MemberData(nameof(NotPeerGone))]
    public void AnyOtherChainKeepsItsLevel(Exception exception)
    {
        Assert.False(_filter.IsPeerGone(Kestrel, LogLevel.Error, exception, out _));
        Assert.Equal(0, _filter.Count);
    }

    [Fact]
    public void OnlyKestrelsCategoriesAndOnlyWarningsAndAbove()
    {
        Assert.False(_filter.IsPeerGone("Paduraksa.Proxy.ForwardAuth", LogLevel.Error, EncryptFailed(), out _));
        Assert.False(_filter.IsPeerGone("Yarp.ReverseProxy.Forwarder.HttpForwarder", LogLevel.Error, EncryptFailed(), out _));
        Assert.False(_filter.IsPeerGone(Kestrel, LogLevel.Information, EncryptFailed(), out _));
        Assert.False(_filter.IsPeerGone(Kestrel, LogLevel.Error, exception: null, out _));
        Assert.True(_filter.IsPeerGone(Kestrel + ".Http2", LogLevel.Error, EncryptFailed(), out _));
        Assert.Equal(1, _filter.Count);
    }

    [Fact]
    public void ThroughTheRoleLoggerItIsOneDebugLineWithoutAStackAndOtherErrorsAreUntouched()
    {
        List<(LogLevel Level, string Message, Exception? Exception)> entries = [];
        using var inner = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new Capture(entries)));
        RoleLoggerFactory factory = new(inner, HostRole.Public, peerGone: _filter);

        var kestrel = factory.CreateLogger(Kestrel);
        Error(kestrel, EncryptFailed(), "Unhandled exception while processing 0HNQ0C487QE4U.");
        InvalidOperationException bug = new("a bug");
        Error(kestrel, bug, "Unexpected exception in a handler.");

        Assert.Equal(2, entries.Count);
        Assert.Equal(LogLevel.Debug, entries[0].Level);
        Assert.Null(entries[0].Exception);
        Assert.Equal(
            "Unhandled exception while processing 0HNQ0C487QE4U. The client went away: Encrypt failed with OpenSSL error - SSL_ERROR_SYSCALL.",
            entries[0].Message);
        Assert.Equal((LogLevel.Error, bug), (entries[1].Level, entries[1].Exception));
        Assert.Equal(1, _filter.Count);
    }

    [Fact]
    public void ItIsCountedEvenWhenDebugIsOff()
    {
        List<(LogLevel Level, string Message, Exception? Exception)> entries = [];
        using var inner = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(new Capture(entries)));
        RoleLoggerFactory factory = new(inner, HostRole.Public, peerGone: _filter);

        Error(factory.CreateLogger(Kestrel), EncryptFailed(), "Unexpected exception in TimingPipeFlusher.FlushAsync.");

        Assert.Empty(entries);
        Assert.Equal(1, _filter.Count);
    }

    // Through ILogger.Log, as Kestrel's generated loggers end up calling it.
    private static void Error(ILogger logger, Exception exception, string message) =>
        logger.Log(LogLevel.Error, default, message, exception, static (state, _) => state);

    private sealed class Capture(List<(LogLevel, string, Exception?)> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(entries);

        public void Dispose()
        {
        }

        private sealed class Logger(List<(LogLevel, string, Exception?)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
