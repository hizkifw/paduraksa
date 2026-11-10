using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Proxy.Http;

/// <summary>The <c>Server</c> header. The compiler builds one value per host into the host's <see
/// cref="ResponseHeaderOperation"/> list, which the gate applies next to <c>Alt-Svc</c> and HSTS, before it answers
/// or forwards, so every response the host gives carries the same one.</summary>
public static class ServerHeader
{
    /// <summary>The built-in name, and the header <see cref="ServerHeaderMode.Paduraksa"/> sends.</summary>
    public const string Product = "Paduraksa";

    public const string HeaderName = "Server";

    /// <summary>The longest custom value accepted, in characters.</summary>
    public const int MaxValueLength = 128;

    /// <summary>The header value for one host. Empty for <see cref="ServerHeaderMode.Upstream"/> and <see
    /// cref="ServerHeaderMode.Remove"/>, and for a custom mode with no value (which validation and the compiler
    /// refuse).</summary>
    public static StringValues ValueOf(HostServerHeaderSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Mode switch
        {
            ServerHeaderMode.Paduraksa => new StringValues(Product),
            ServerHeaderMode.Custom => string.IsNullOrEmpty(spec.Value) ? StringValues.Empty : new StringValues(spec.Value),
            _ => StringValues.Empty,
        };
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a custom header value: 1-128 visible ASCII characters or spaces, with no
    /// leading or trailing space and no control characters. RFC 9110 §10.2.4's grammar allows more; this is the simple,
    /// safe subset a host setting needs.
    /// </summary>
    public static bool IsValidValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength
            || value[0] == ' ' || value[^1] == ' ')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
