// The runtime's OpenSSL exception is internal to System.Net.Security, so PeerGoneLogFilter recognises it by its full type name,
// "Interop+OpenSsl+SslException". This stand-in has that same name, so the tests can build the chains production logged.
#pragma warning disable CA1050, CA1034, S3903
internal static class Interop
{
    internal static class OpenSsl
    {
        internal sealed class SslException(string message) : Exception(message);
    }
}
#pragma warning restore CA1050, CA1034, S3903
