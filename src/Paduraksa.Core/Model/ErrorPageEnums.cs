using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Core.Model;

/// <summary>How an operator's error page is written.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ErrorPageMode>))]
public enum ErrorPageMode
{
    /// <summary>A heading and a sentence, rendered into the built-in layout (and its strict CSP).</summary>
    Message,

    /// <summary>The operator's own full HTML document, served with a CSP that allows its inline style and nothing that runs.</summary>
    Html,
}

/// <summary>When a host answers with its own page in place of an error body its upstream sent.</summary>
[JsonConverter(typeof(ConfigEnumConverter<UpstreamErrorReplacement>))]
public enum UpstreamErrorReplacement
{
    /// <summary>The upstream's error responses pass through untouched (the built-in default).</summary>
    Off,

    /// <summary>An upstream error whose body is <c>text/html</c>, an error page of the app's or its web server's, is replaced.</summary>
    Html,

    /// <summary>An upstream error is replaced whatever its content type is (JSON and plain text included).</summary>
    Any,
}

/// <summary>Which layer of the settings chain an error page came from.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ErrorPageSource>))]
public enum ErrorPageSource
{
    /// <summary>No layer set one, so the page built into Paduraksa is served.</summary>
    BuiltIn,

    GlobalDefaults,

    Profile,

    Host,
}
