using System.Text;

using Paduraksa.Proxy.AccessLists;

namespace Paduraksa.Tests.Proxy.AccessLists;

public sealed class BasicCredentialsTests
{
    private static BasicParse Parse(string header, out string? name, out string? password)
    {
        var buffer = new byte[BasicCredentials.MaxDecodedLength + 64];
        var result = BasicCredentials.TryParse(header, buffer, out var length);
        if (result != BasicParse.Ok)
        {
            name = password = null;
            return result;
        }

        var credentials = Encoding.UTF8.GetString(buffer, 0, length);
        var colon = credentials.IndexOf(':', StringComparison.Ordinal);
        name = credentials[..colon];
        password = credentials[(colon + 1)..];
        return result;
    }

    private static string Header(string credentials, string scheme = "Basic") => $"{scheme} {Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials))}";

    [Theory]
    [InlineData("alice:secret", "alice", "secret")]
    [InlineData("alice:", "alice", "")]
    [InlineData("alice:pa:ss", "alice", "pa:ss")]
    [InlineData("alice:päss", "alice", "päss")]
    public void TheFirstColonSplitsTheNameFromThePassword(string credentials, string name, string password)
    {
        Assert.Equal(BasicParse.Ok, Parse(Header(credentials), out var actualName, out var actualPassword));
        Assert.Equal((name, password), (actualName, actualPassword));
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("BASIC")]
    public void TheSchemeIgnoresCase(string scheme)
    {
        Assert.Equal(BasicParse.Ok, Parse(Header("a:b", scheme), out _, out _));
    }

    [Theory]
    [InlineData("Bearer abc")]
    [InlineData("Basicabc")]
    [InlineData("Digest a=b")]
    [InlineData("")]
    public void AnotherSchemeIsNotBasic(string header)
    {
        Assert.Equal(BasicParse.NotBasic, Parse(header, out _, out _));
    }

    [Theory]
    [InlineData("Basic ")]
    [InlineData("Basic !!!")]
    [InlineData("Basic YWxpY2U")]
    public void UndecodableCredentialsAreUnreadable(string header)
    {
        Assert.Equal(BasicParse.Unreadable, Parse(header, out _, out _));
    }

    [Fact]
    public void ACredentialWithoutAColonOrWithoutANameIsUnreadable()
    {
        Assert.Equal(BasicParse.Unreadable, Parse(Header("alice"), out _, out _));
        Assert.Equal(BasicParse.Unreadable, Parse(Header(":secret"), out _, out _));
    }

    [Fact]
    public void AnOversizedTokenIsRefusedBeforeAnythingIsDecoded()
    {
        var buffer = new byte[BasicCredentials.MaxDecodedLength + 64];
        Array.Fill(buffer, (byte)0xFF);

        // Valid base64 all the way, a hundred thousand characters of it.
        var result = BasicCredentials.TryParse("Basic " + new string('A', 100_000), buffer, out var length);

        Assert.Equal((BasicParse.Unreadable, 0), (result, length));
        Assert.All(buffer, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void ACredentialLongerThanAnyStoredOneIsUnreadableWithoutBeingDecoded()
    {
        Assert.Equal(BasicParse.Unreadable, Parse(Header("alice:" + new string('x', 400)), out _, out _));
        Assert.Equal(BasicParse.Ok, Parse(Header("alice:" + new string('x', 256)), out _, out _));
    }
}
