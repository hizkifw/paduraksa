using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Paduraksa.Tests.Notifications.Fakes;

internal sealed record MailReceived(string From, IReadOnlyList<string> To, string Data, string? Login);

/// <summary>
/// A tiny in-process SMTP server: EHLO, STARTTLS (when offered), AUTH PLAIN, MAIL, RCPT, DATA, QUIT. <see cref="RejectRecipientWith"/> and
/// <see cref="RejectAuthWith"/> make it refuse, to see how the sender classifies it. Counts only completed sessions' mail.
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<MailReceived> _mail = new();
    private readonly X509Certificate2? _certificate;
    private readonly bool _implicitTls;
    private readonly bool _offerStartTls;
    private readonly Task _accept;
    private readonly CancellationTokenSource _stop = new();

    public FakeSmtpServer(bool implicitTls = false, bool offerStartTls = false, bool requireAuth = false)
    {
        _implicitTls = implicitTls;
        _offerStartTls = offerStartTls;
        RequireAuth = requireAuth;
        if (implicitTls || offerStartTls)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256);
            SubjectAlternativeNameBuilder names = new();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        }

        _listener.Start();
        _accept = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool RequireAuth { get; }

    /// <summary>The certificate's hash, for a test that trusts exactly this one.</summary>
    public string? Thumbprint => _certificate?.GetCertHashString();

    public string? RejectRecipientWith { get; set; }

    public string? RejectAuthWith { get; set; }

    public IReadOnlyList<MailReceived> Mail => [.. _mail];

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopping: the listener is closed under the pending accept, which throws one of these depending on the race.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                var secured = false;
                if (_implicitTls)
                {
                    stream = await UpgradeAsync(stream);
                    secured = true;
                }

                var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fake ESMTP");
                string from = string.Empty;
                List<string> to = [];
                string? login = null;
                while (await reader.ReadLineAsync() is { } line)
                {
                    var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                        case "HELO":
                            await writer.WriteLineAsync("250-fake");
                            if (_offerStartTls && !secured)
                            {
                                await writer.WriteLineAsync("250-STARTTLS");
                            }

                            await writer.WriteLineAsync("250-AUTH PLAIN");
                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "STARTTLS":
                            await writer.WriteLineAsync("220 go ahead");
                            stream = await UpgradeAsync(stream);
                            reader = new StreamReader(stream, Encoding.ASCII);
                            writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                            secured = true;
                            break;
                        case "AUTH":
                            if (RejectAuthWith is { } rejection)
                            {
                                await writer.WriteLineAsync(rejection);
                                break;
                            }

                            var initial = line.Split(' ', 3).ElementAtOrDefault(2) ?? await AskAsync(reader, writer);
                            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(initial)).Split('\0');
                            login = $"{parts[1]}:{parts[2]}";
                            await writer.WriteLineAsync("235 ok");
                            break;
                        case "MAIL":
                            if (RequireAuth && login is null)
                            {
                                await writer.WriteLineAsync("530 authentication required");
                                break;
                            }

                            from = line[(line.IndexOf('<', StringComparison.Ordinal) + 1)..line.IndexOf('>', StringComparison.Ordinal)];
                            to = [];
                            await writer.WriteLineAsync("250 ok");
                            break;
                        case "RCPT":
                            if (RejectRecipientWith is { } refusal)
                            {
                                await writer.WriteLineAsync(refusal);
                                break;
                            }

                            to.Add(line[(line.IndexOf('<', StringComparison.Ordinal) + 1)..line.IndexOf('>', StringComparison.Ordinal)]);
                            await writer.WriteLineAsync("250 ok");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 end with .");
                            StringBuilder data = new();
                            while (await reader.ReadLineAsync() is { } row && row != ".")
                            {
                                data.Append(row).Append("\r\n");
                            }

                            _mail.Enqueue(new MailReceived(from, to, data.ToString(), login));
                            await writer.WriteLineAsync("250 queued");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 bye");
                            return;
                        default:
                            await writer.WriteLineAsync("250 ok");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                // A client that hangs up mid-session, as the TLS refusal test does.
            }
        }
    }

    private static async Task<string> AskAsync(StreamReader reader, StreamWriter writer)
    {
        await writer.WriteLineAsync("334 ");
        return (await reader.ReadLineAsync())!;
    }

    private async Task<Stream> UpgradeAsync(Stream inner)
    {
        SslStream tls = new(inner, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(_certificate!);
        return tls;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        _certificate?.Dispose();
        _stop.Dispose();
    }
}
