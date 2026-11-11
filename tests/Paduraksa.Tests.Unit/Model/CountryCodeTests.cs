using Paduraksa.Core.Geo;

namespace Paduraksa.Tests.Unit.Model;

public sealed class CountryCodeTests
{
    private static readonly string[] RuleCodes = ["DE", "fr", "T1", "XX", "DE"];

    [Theory]
    [InlineData("DE", "DE")]
    [InlineData("de", "DE")]
    [InlineData("dE", "DE")]
    [InlineData("US", "US")]
    [InlineData("ZZ", "ZZ")]
    [InlineData("AA", "AA")]
    public void TwoAsciiLettersAreACountryInEitherCase(string value, string expected)
    {
        var code = CountryCode.Parse(value);

        Assert.True(code.IsLetterPair);
        Assert.Equal(expected, code.ToString());
    }

    [Theory]
    [InlineData("T1")]
    [InlineData("t1")]
    public void T1IsTorAndNotJustUnknown(string value)
    {
        var code = CountryCode.Parse(value);

        Assert.True(code.IsTor);
        Assert.False(code.IsUnknown);
        Assert.NotEqual(CountryCode.Unknown, code);
        Assert.Equal("T1", code.ToString());
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("xx")]
    [InlineData("")]
    [InlineData("D")]
    [InlineData("DEU")]
    [InlineData("1D")]
    [InlineData("D1")]
    [InlineData("T2")]
    [InlineData("D-")]
    [InlineData(" D")]
    [InlineData("ÄÖ")]
    [InlineData("@A")]
    [InlineData("[A")]
    [InlineData("A`")]
    [InlineData("A{")]
    public void AnythingElseIsUnknown(string value)
    {
        var code = CountryCode.Parse(value);

        Assert.True(code.IsUnknown);
        Assert.Equal(CountryCode.Unknown, code);
        Assert.Equal("XX", code.ToString());
    }

    [Fact]
    public void TheDefaultValueIsUnknown() => Assert.True(default(CountryCode).IsUnknown);

    [Fact]
    public void EveryLetterPairHasItsOwnSlotInsideTheSet()
    {
        HashSet<int> slots = [CountryCode.Unknown.Slot, CountryCode.Tor.Slot];
        for (var a = 'A'; a <= 'Z'; a++)
        {
            for (var b = 'A'; b <= 'Z'; b++)
            {
                var code = CountryCode.Parse($"{a}{b}");
                if ($"{a}{b}" == "XX")
                {
                    Assert.True(code.IsUnknown);
                    continue;
                }

                Assert.True(slots.Add(code.Slot));
                Assert.InRange(code.Slot, 0, CountryCode.SlotCount - 1);
                Assert.Equal($"{a}{b}", code.ToString());
            }
        }

        // 676 pairs less XX (which is unknown), plus unknown and Tor.
        Assert.Equal(676 - 1 + 2, slots.Count);
    }

    [Fact]
    public void TheCountryListIsTheIsoAssignmentsAndKosovo()
    {
        Assert.Equal(250, Countries.All.Count);
        Assert.Equal(Countries.All.Count, Countries.All.Select(c => c.Code).Distinct().Count());
        Assert.All(Countries.All, country =>
        {
            Assert.Equal(2, country.Code.Length);
            Assert.True(country.Code.All(char.IsAsciiLetterUpper), country.Code);
            Assert.True(country.Name.All(c => c is >= ' ' and <= '~'), country.Name);
        });
        Assert.Equal(Countries.All.Select(c => c.Name).Order(StringComparer.Ordinal), Countries.All.Select(c => c.Name));
        Assert.Contains(new Country("DE", "Germany"), Countries.All);
        Assert.Contains(Countries.All, c => c.Code == "XK");
        Assert.DoesNotContain(Countries.All, c => c.Code is "XX" or "T1" or "ZZ");
    }

    [Theory]
    [InlineData("DE", true)]
    [InlineData("de", true)]
    [InlineData("T1", true)]
    [InlineData("XX", true)]
    [InlineData("xx", true)]
    [InlineData("ZZ", false)]
    [InlineData("AN", false)]
    [InlineData("D", false)]
    [InlineData("", false)]
    [InlineData("DEU", false)]
    [InlineData("T2", false)]
    public void ARuleNamesAnAssignedCountryOrAPseudoCode(string value, bool known) =>
        Assert.Equal(known, Countries.TryParseRuleCode(value, out _));

    [Fact]
    public void ARuleCodeKeepsTorApartFromUnknown()
    {
        Assert.True(Countries.TryParseRuleCode("T1", out var tor));
        Assert.True(Countries.TryParseRuleCode("XX", out var unknown));
        Assert.True(tor.IsTor);
        Assert.True(unknown.IsUnknown);
        Assert.Equal("Tor exit nodes", Countries.NameOf(tor));
        Assert.Equal("Unknown country", Countries.NameOf(unknown));
        Assert.Equal("Germany", Countries.NameOf(CountryCode.Parse("de")));
    }

    [Fact]
    public void TheBitsetHoldsExactlyWhatItWasMadeFrom()
    {
        var set = CountrySet.Create(RuleCodes.Select(code => Countries.TryParseRuleCode(code, out var parsed) ? parsed : throw new InvalidOperationException(code)));

        Assert.Equal(4, set.Count);
        Assert.False(set.IsEmpty);
        Assert.True(set.Contains(CountryCode.Parse("DE")));
        Assert.True(set.Contains(CountryCode.Parse("FR")));
        Assert.True(set.Contains(CountryCode.Tor));
        Assert.True(set.Contains(CountryCode.Unknown));
        Assert.False(set.Contains(CountryCode.Parse("NL")));
        Assert.False(set.Contains(CountryCode.Parse("ED")));
    }

    [Fact]
    public void EveryBitIsIndependent()
    {
        // The slot of XX is never used: that pair is the unknown country, slot 0.
        for (var slot = 0; slot < CountryCode.SlotCount; slot++)
        {
            if (slot == CountryCode.Parse("XY").Slot - 1)
            {
                continue;
            }

            var code = SlotOf(slot);
            var set = CountrySet.Create([code]);

            Assert.Equal(1, set.Count);
            for (var other = 0; other < CountryCode.SlotCount; other++)
            {
                if (other != CountryCode.Parse("XY").Slot - 1)
                {
                    Assert.Equal(other == slot, set.Contains(SlotOf(other)));
                }
            }
        }
    }

    [Fact]
    public void AnEmptySetIsTheSharedOneAndContainsNothing()
    {
        Assert.Same(CountrySet.Empty, CountrySet.Create([]));
        Assert.True(CountrySet.Empty.IsEmpty);
        Assert.False(CountrySet.Empty.Contains(CountryCode.Unknown));
        Assert.False(CountrySet.Empty.Contains(CountryCode.Tor));
    }

    private static CountryCode SlotOf(int slot) =>
        slot == 0 ? CountryCode.Unknown
        : slot == 1 ? CountryCode.Tor
        : CountryCode.Parse($"{(char)('A' + ((slot - 2) / 26))}{(char)('A' + ((slot - 2) % 26))}");
}
