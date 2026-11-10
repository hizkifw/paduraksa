using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Net;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>
/// <see cref="DefaultHttpContext"/>s set up like Kestrel's for allocation tests (spike S3 §8): a persistent-state
/// feature, a response header dictionary that stores <c>Content-Length</c> as a number, and reuse across iterations.
/// </summary>
internal static class TestContexts
{
    public static DefaultHttpContext Create(string? host, string path = "/", string method = "GET", string scheme = "http", IPAddress? remote = null, string? rawTarget = null)
    {
        FeatureCollection features = new();
        HttpRequestFeature request = new()
        {
            Method = method,
            Scheme = scheme,
            Path = path,
            RawTarget = rawTarget ?? path,
            Protocol = "HTTP/1.1",
        };
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        features.Set<IHttpRequestFeature>(request);
        features.Set<IHttpResponseFeature>(new HttpResponseFeature { Headers = new KestrelLikeHeaders() });
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
        features.Set<IPersistentStateFeature>(new PersistentState());
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature { RemoteIpAddress = remote ?? IPAddress.Loopback });
        return new DefaultHttpContext(features);
    }

    /// <summary>Kestrel keeps this per connection / pooled stream.</summary>
    private sealed class PersistentState : IPersistentStateFeature
    {
        public IDictionary<object, object?> State { get; } = new Dictionary<object, object?>();
    }
}

/// <summary>Response headers with Kestrel's allocation profile: <c>Content-Length</c> is a field, not a string.</summary>
[SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "Mirrors IHeaderDictionary.")]
internal sealed class KestrelLikeHeaders : IHeaderDictionary
{
    private readonly Dictionary<string, StringValues> _values = new(StringComparer.OrdinalIgnoreCase);

    public long? ContentLength { get; set; }

    public int Count => _values.Count + (ContentLength is null ? 0 : 1);

    public bool IsReadOnly => false;

    public ICollection<string> Keys => _values.Keys;

    public ICollection<StringValues> Values => _values.Values;

    public StringValues this[string key]
    {
        get => _values.TryGetValue(key, out var value) ? value : StringValues.Empty;
        set => _values[key] = value;
    }

    public void Add(string key, StringValues value) => _values.Add(key, value);

    public void Add(KeyValuePair<string, StringValues> item) => _values.Add(item.Key, item.Value);

    public void Clear()
    {
        _values.Clear();
        ContentLength = null;
    }

    public bool Contains(KeyValuePair<string, StringValues> item) => _values.TryGetValue(item.Key, out var v) && v == item.Value;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public void CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, StringValues>>)_values).CopyTo(array, arrayIndex);

    public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() => _values.GetEnumerator();

    public bool Remove(string key) => _values.Remove(key);

    public bool Remove(KeyValuePair<string, StringValues> item) => _values.Remove(item.Key);

    public bool TryGetValue(string key, out StringValues value) => _values.TryGetValue(key, out value);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
