# Paduraksa.ProxyProtocol

A strict, allocation-free parser for the HAProxy [PROXY protocol](https://www.haproxy.org/download/3.0/doc/proxy-protocol.txt)
(v1 text and v2 binary), with no dependency beyond the .NET base class library. It is fuzzed (coverage-guided, with its corpus replayed on every commit) and tested against the spec's edge cases.

Using Kestrel? [Paduraksa.ProxyProtocol.AspNetCore](https://www.nuget.org/packages/Paduraksa.ProxyProtocol.AspNetCore) wraps this in a
`UseProxyProtocol` connection middleware.

## Parse a buffer

```csharp
using Paduraksa.ProxyProtocol;

ReadOnlySpan<byte> bytes = "PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\nGET / HTTP/1.1\r\n"u8;

switch (ProxyProtocolParser.TryParse(bytes, out var header, out var consumed))
{
    case ProxyProtocolParseStatus.Success:
        // bytes[consumed..] is whatever follows the header
        Console.WriteLine($"{header.SourceAddress}:{header.SourcePort}");
        break;
    case ProxyProtocolParseStatus.Incomplete:
        // a valid prefix so far: read more and call again
        break;
    case ProxyProtocolParseStatus.Invalid:
        // not a header: drop the connection
        break;
}
```

`TryParse` also takes a `ReadOnlySequence<byte>`, and neither overload allocates.

## Read a header off a pipe

```csharp
var result = await ProxyProtocolReader.ReadAsync(connection.Transport.Input, TimeSpan.FromSeconds(3));
if (result.Status == ProxyProtocolReadStatus.Success)
{
    IPEndPoint? client = result.Header.SourceEndPoint; // null when the header carries no address
}
```

The reader consumes the header and leaves every byte after it in the pipe. It never throws for what the peer sends or does: malformed bytes, a slow
drip, a close or a reset each map to a status. The timeout is required and must be greater than zero.

## What it accepts

- **v1:** `PROXY TCP4|TCP6 src dst sport dport\r\n` with single spaces, strictly valid addresses, decimal ports without leading zeros, and at most 107 bytes; or `PROXY UNKNOWN...\r\n`.
- **v2:** the 12-byte signature, version 2, command `LOCAL` or `PROXY`, a known family and transport, an address block long enough for the family, and a total length within `maxV2Length` (default 4096).
- **Not validated:** TLVs are skipped, and the optional CRC32C is not checked. `DGRAM` is rejected: this is for stream listeners.
- **Input that can't begin a header is rejected at once:** a byte that breaks the v1 prefix `PROXY ` or the v2 signature is `Invalid` without waiting for more data. Past the prefix, a v1 line is judged when its line feed arrives or 107 bytes have; until then it is `Incomplete`, so bound the wait with a timeout (the reader does).

A header without addresses (`LOCAL`, `UNSPEC`, `UNIX`, `UNKNOWN`) parses as a success with `HasAddresses == false`: use the socket's own endpoints then.

## Trust

The parser doesn't decide who may send a header: **only accept one from a load balancer you control.** Anyone who can send one can claim any client address. The ASP.NET Core package does this check for you.

## Addresses

`IpKey` is the address type: a 128-bit value (IPv4 stored IPv4-mapped, so `1.2.3.4` and `::ffff:1.2.3.4` are one key), with CIDR masking, ordering and
allocation-free formatting. `IpParser` is a strict, span-based IP text parser: no short forms, octal or leading zeros, zones or brackets. It accepts only what
`IPAddress.TryParse` accepts with the same meaning, since a parser that differs from another is how allow and deny lists get bypassed.
`header.SourceEndPoint` and `header.DestinationEndPoint` give plain `IPEndPoint`s for code that doesn't want `IpKey`; they allocate, so use them once per connection.

Targets .NET 10 and .NET 8.
