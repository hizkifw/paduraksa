using Microsoft.Extensions.Logging;

namespace Paduraksa.Acme;

// What the client logs: methods, URLs (ACME URLs carry no secrets), problem types, counts. Never a request or response body: a signed
// body can carry a CSR or an external account binding, a response a certificate order.
internal static partial class AcmeLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "ACME {Method} {Url}")]
    public static partial void Request(ILogger logger, string method, string url);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "ACME {Url} failed with {ProblemType} (HTTP {Status})")]
    public static partial void Problem(ILogger logger, string url, string problemType, int status);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "ACME {Url} answered {ProblemType} (HTTP {Status})")]
    public static partial void ProblemDebug(ILogger logger, string url, string problemType, int status);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "ACME {Url} still refused the nonce after {Attempts} attempts")]
    public static partial void BadNonceExhausted(ILogger logger, Uri url, int attempts);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "ACME {Url} refused the nonce, signing again (retry {Attempt})")]
    public static partial void BadNonceRetry(ILogger logger, Uri url, int attempt);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "ACME {Url} is {Status}, polling again in {Delay}")]
    public static partial void Polling(ILogger logger, Uri url, AcmeStatus status, TimeSpan delay);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "ACME directory {Url} fetched")]
    public static partial void DirectoryFetched(ILogger logger, Uri url);
}
