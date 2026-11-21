namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// English-like text from the bundled lexicon and a set of templates. The sentences are grammatical and mean nothing, which is what a crawler
/// that stores the text for training should be given. Every function draws from the generator in a fixed order, so the same state always
/// produces the same text.
/// </summary>
internal static class TextGen
{
    public static void Word(ref Utf8Out o, byte[][] list, ref SiteRandom r, bool capitalize = false) => Put(ref o, list[r.Below(list.Length)], capitalize);

    /// <summary>A noun phrase: a determiner, maybe an adjective, and a noun.</summary>
    public static void NounPhrase(ref Utf8Out o, ref SiteRandom r, bool plural, bool capitalize = false)
    {
        var adjective = r.Chance(60) ? r.Below(Lexicon.Adjectives.Length) : -1;
        var noun = r.Below(Lexicon.Nouns.Length);
        var nounForm = plural ? Lexicon.NounPlurals[noun] : Lexicon.Nouns[noun];
        var first = adjective >= 0 ? Lexicon.Adjectives[adjective] : nounForm;
        var roll = r.Below(6);
        if (plural)
        {
            Put(ref o, roll switch { 2 => "some"u8, 3 => "many"u8, 4 => "most"u8, 5 => "these"u8, _ => "the"u8 }, capitalize);
        }
        else
        {
            Put(ref o, roll switch { 2 => IsVowel(first[0]) ? "an"u8 : "a"u8, 3 => "this"u8, 4 => "every"u8, 5 => "that"u8, _ => "the"u8 }, capitalize);
        }

        o.Write((byte)' ');
        if (adjective >= 0)
        {
            o.Write(Lexicon.Adjectives[adjective]);
            o.Write((byte)' ');
        }

        o.Write(nounForm);
    }

    /// <summary>"a" or "an" and then a noun, which a fixed "a" in a template would get wrong.</summary>
    private static void IndefiniteNoun(ref Utf8Out o, ref SiteRandom r, bool capitalize = false)
    {
        var noun = Lexicon.Nouns[r.Below(Lexicon.Nouns.Length)];
        o.Write(IsVowel(noun[0]) ? "an "u8 : "a "u8);
        Put(ref o, noun, capitalize);
    }

    /// <summary>A noun phrase of either number.</summary>
    private static void AnyNounPhrase(ref Utf8Out o, ref SiteRandom r) => NounPhrase(ref o, ref r, r.Chance(45));

    /// <summary>One sentence, ending in a full stop.</summary>
    public static void Sentence(ref Utf8Out o, ref SiteRandom r)
    {
        switch (r.Below(16))
        {
            case 0:
                NounPhrase(ref o, ref r, false, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.VerbS, ref r);
                MaybeAdverb(ref o, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                break;
            case 1:
                NounPhrase(ref o, ref r, true, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                MaybeAdverb(ref o, ref r);
                break;
            case 2:
                Word(ref o, Lexicon.Openers, ref r);
                o.Write(", "u8);
                NounPhrase(ref o, ref r, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                break;
            case 3:
                NounPhrase(ref o, ref r, false, true);
                o.Write(" is "u8);
                Word(ref o, Lexicon.Adjectives, ref r);
                o.Write(", and "u8);
                NounPhrase(ref o, ref r, false);
                o.Write((byte)' ');
                Word(ref o, Lexicon.VerbS, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                break;
            case 4:
                o.Write("It is "u8);
                Word(ref o, Lexicon.Adjectives, ref r);
                o.Write(" to "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                MaybeAdverb(ref o, ref r);
                break;
            case 5:
                o.Write("Nobody expects "u8);
                NounPhrase(ref o, ref r, false);
                o.Write(" to "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                break;
            case 6:
                o.Write("When "u8);
                NounPhrase(ref o, ref r, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                o.Write(", "u8);
                NounPhrase(ref o, ref r, false);
                o.Write((byte)' ');
                Word(ref o, Lexicon.VerbS, ref r);
                o.Write((byte)' ');
                Word(ref o, Lexicon.Adverbs, ref r);
                break;
            case 7:
                o.Write("Every "u8);
                Word(ref o, Lexicon.Nouns, ref r);
                o.Write(" has "u8);
                IndefiniteNoun(ref o, ref r);
                o.Write(", and every "u8);
                Word(ref o, Lexicon.Nouns, ref r);
                o.Write(" has "u8);
                IndefiniteNoun(ref o, ref r);
                break;
            case 8:
                Word(ref o, Lexicon.VerbIng, ref r, true);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                o.Write(" is "u8);
                Word(ref o, Lexicon.Adjectives, ref r);
                o.Write(" work"u8);
                break;
            case 9:
                Word(ref o, Lexicon.Openers, ref r);
                o.Write(", "u8);
                NounPhrase(ref o, ref r, false);
                o.Write((byte)' ');
                Word(ref o, Lexicon.VerbS, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                break;
            case 10:
                NounPhrase(ref o, ref r, false, true);
                o.Write(" and "u8);
                NounPhrase(ref o, ref r, false);
                o.Write(" both "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                break;
            case 11:
                o.Write("The trick is to "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                o.Write(" before you "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                break;
            case 12:
                o.Write("You can "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                o.Write(" or "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                o.Write(", but not both"u8);
                break;
            case 13:
                o.Write("I "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                o.Write(" every "u8);
                Word(ref o, Lexicon.Nouns, ref r);
                o.Write(", and it still feels "u8);
                Word(ref o, Lexicon.Adjectives, ref r);
                break;
            case 14:
                o.Write("We "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                MaybeAdverb(ref o, ref r);
                o.Write(", then "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                break;
            default:
                o.Write("There is no "u8);
                Word(ref o, Lexicon.Adjectives, ref r);
                o.Write(" way to "u8);
                Word(ref o, Lexicon.VerbBase, ref r);
                o.Write((byte)' ');
                AnyNounPhrase(ref o, ref r);
                break;
        }

        o.Write((byte)'.');
    }

    /// <summary>The words before an inline link; the link and a full stop follow.</summary>
    public static void LinkLead(ref Utf8Out o, ref SiteRandom r) => o.Write(r.Below(4) switch
    {
        0 => "This builds on "u8,
        1 => "For more, read "u8,
        2 => "It echoes what was said in "u8,
        _ => "Anyone curious should start with "u8,
    });

    /// <summary>A paragraph of <paramref name="sentences"/> sentences.</summary>
    public static void Paragraph(ref Utf8Out o, ref SiteRandom r, int sentences)
    {
        for (var i = 0; i < sentences; i++)
        {
            if (i > 0)
            {
                o.Write((byte)' ');
            }

            Sentence(ref o, ref r);
        }
    }

    /// <summary>A post title: every content word capitalised.</summary>
    public static void Title(ref Utf8Out o, ref SiteRandom r)
    {
        switch (r.Below(10))
        {
            case 0:
                Word(ref o, Lexicon.Adjectives, ref r, true);
                o.Write((byte)' ');
                PluralNoun(ref o, ref r, true);
                break;
            case 1:
                o.Write("How to "u8);
                Word(ref o, Lexicon.VerbBase, ref r, true);
                o.Write((byte)' ');
                var adjective = Lexicon.Adjectives[r.Below(Lexicon.Adjectives.Length)];
                o.Write(IsVowel(adjective[0]) ? "an "u8 : "a "u8);
                o.WriteCapitalized(adjective);
                o.Write((byte)' ');
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            case 2:
                o.Write("Notes on the "u8);
                Word(ref o, Lexicon.Adjectives, ref r, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            case 3:
                o.Write("Why Every "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                o.Write(" Needs "u8);
                IndefiniteNoun(ref o, ref r, true);
                break;
            case 4:
                o.Write("The "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                o.Write(" and the "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            case 5:
                o.WriteInt(r.Between(3, 12));
                o.Write((byte)' ');
                Word(ref o, Lexicon.Adjectives, ref r, true);
                o.Write((byte)' ');
                PluralNoun(ref o, ref r, true);
                o.Write(" for the "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            case 6:
                Word(ref o, Lexicon.VerbIng, ref r, true);
                o.Write(" the "u8);
                Word(ref o, Lexicon.Adjectives, ref r, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            case 7:
                o.Write("A Week with the "u8);
                Word(ref o, Lexicon.Adjectives, ref r, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            case 8:
                o.Write("What the "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                o.Write(" Taught Me About "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            default:
                o.Write("Small "u8);
                PluralNoun(ref o, ref r, true);
                o.Write(", Big "u8);
                PluralNoun(ref o, ref r, true);
                break;
        }
    }

    /// <summary>A section heading.</summary>
    public static void Heading(ref Utf8Out o, ref SiteRandom r)
    {
        switch (r.Below(4))
        {
            case 0:
                o.Write("The "u8);
                Word(ref o, Lexicon.Adjectives, ref r, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            case 1:
                Word(ref o, Lexicon.VerbIng, ref r, true);
                o.Write((byte)' ');
                PluralNoun(ref o, ref r, true);
                break;
            case 2:
                o.Write("On "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            default:
                o.Write("Before You "u8);
                Word(ref o, Lexicon.VerbBase, ref r, true);
                break;
        }
    }

    /// <summary>A list item: an imperative with an object.</summary>
    public static void ListItem(ref Utf8Out o, ref SiteRandom r)
    {
        Word(ref o, Lexicon.VerbBase, ref r, true);
        o.Write(" the "u8);
        Word(ref o, Lexicon.Adjectives, ref r);
        o.Write((byte)' ');
        Word(ref o, Lexicon.Nouns, ref r);
    }

    /// <summary>A few lines of pseudo-code, using identifiers from the lexicon. It holds nothing HTML would escape.</summary>
    public static void CodeLines(ref Utf8Out o, ref SiteRandom r)
    {
        var lines = r.Between(3, 5);
        for (var i = 0; i < lines; i++)
        {
            switch (r.Below(4))
            {
                case 0:
                    o.Write("for "u8);
                    Word(ref o, Lexicon.Nouns, ref r);
                    o.Write(" in "u8);
                    Identifier(ref o, ref r);
                    o.Write(":"u8);
                    break;
                case 1:
                    o.Write("    "u8);
                    Word(ref o, Lexicon.Nouns, ref r);
                    o.Write(" = "u8);
                    Word(ref o, Lexicon.VerbBase, ref r);
                    o.Write((byte)'_');
                    Word(ref o, Lexicon.Nouns, ref r);
                    o.Write((byte)'(');
                    Word(ref o, Lexicon.Nouns, ref r);
                    o.Write(", "u8);
                    o.WriteInt(r.Between(1, 64));
                    o.Write((byte)')');
                    break;
                case 2:
                    o.Write("    if "u8);
                    Identifier(ref o, ref r);
                    o.Write(" > "u8);
                    o.WriteInt(r.Between(0, 200));
                    o.Write(":"u8);
                    break;
                default:
                    o.Write("        "u8);
                    Word(ref o, Lexicon.VerbBase, ref r);
                    o.Write((byte)'(');
                    Identifier(ref o, ref r);
                    o.Write((byte)')');
                    break;
            }

            o.Write((byte)'\n');
        }
    }

    /// <summary>A made-up word: three or four consonant-vowel syllables and sometimes a final consonant.</summary>
    public static void MadeUpWord(ref Utf8Out o, ref SiteRandom r)
    {
        var syllables = r.Between(3, 4);
        for (var i = 0; i < syllables; i++)
        {
            o.Write("bcdfghjklmnprstvz"u8[r.Below(17)]);
            o.Write("aeiou"u8[r.Below(5)]);
        }

        if (r.Chance(50))
        {
            o.Write("nrlsx"u8[r.Below(5)]);
        }
    }

    /// <summary>The blog's title when the operator sets none.</summary>
    public static void SiteName(ref Utf8Out o, ref SiteRandom r)
    {
        switch (r.Below(5))
        {
            case 0:
                o.Write("The "u8);
                Word(ref o, Lexicon.Adjectives, ref r, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            case 1:
                Word(ref o, Lexicon.Adjectives, ref r, true);
                o.Write((byte)' ');
                Word(ref o, Lexicon.Nouns, ref r, true);
                o.Write(" Notes"u8);
                break;
            case 2:
                o.Write("Notes from the "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
            case 3:
                o.Write("The "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                o.Write(" Journal"u8);
                break;
            default:
                Word(ref o, Lexicon.Nouns, ref r, true);
                o.Write(" and "u8);
                Word(ref o, Lexicon.Nouns, ref r, true);
                break;
        }
    }

    private static void Identifier(ref Utf8Out o, ref SiteRandom r)
    {
        Word(ref o, Lexicon.Adjectives, ref r);
        o.Write((byte)'_');
        Word(ref o, Lexicon.Nouns, ref r);
    }

    private static void PluralNoun(ref Utf8Out o, ref SiteRandom r, bool capitalize) => Put(ref o, Lexicon.NounPlurals[r.Below(Lexicon.NounPlurals.Length)], capitalize);

    private static void MaybeAdverb(ref Utf8Out o, ref SiteRandom r)
    {
        if (r.Chance(40))
        {
            o.Write((byte)' ');
            Word(ref o, Lexicon.Adverbs, ref r);
        }
    }

    private static void Put(ref Utf8Out o, scoped ReadOnlySpan<byte> word, bool capitalize)
    {
        if (capitalize)
        {
            o.WriteCapitalized(word);
        }
        else
        {
            o.Write(word);
        }
    }

    private static bool IsVowel(byte letter) => letter is (byte)'a' or (byte)'e' or (byte)'i' or (byte)'o' or (byte)'u';
}
