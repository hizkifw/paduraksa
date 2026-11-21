using System.Text;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// The words the site is built from, written for this site and bundled as UTF-8 so a page copies bytes and never encodes. Every word is
/// lower-case ASCII letters, so nothing generated needs escaping in HTML, XML or a script string.
/// </summary>
internal static class Lexicon
{
    private const string NounList =
        "garden kitchen workshop window bridge river harbor lantern compass ledger notebook engine orchard market station ladder blanket kettle " +
        "bicycle thread needle fabric pattern habit rhythm balance margin season winter summer autumn morning evening weekend journey road trail " +
        "mountain valley forest meadow pond island coast lighthouse boat anchor sail rope basket bottle jar candle clock mirror door gate fence " +
        "wall roof floor stair corner shelf drawer cabinet table chair lamp desk pencil paper letter postcard map atlas camera photograph film " +
        "record guitar melody song story chapter sentence paragraph recipe bread soup salad sauce spice pepper lemon apple pear plum cherry berry " +
        "honey butter cheese flour sugar salt coffee tea seed soil root branch flower petal vine herb tool hammer wrench ruler gauge bolt screw " +
        "hinge pipe valve wire circuit sensor signal network server router cable battery motor wheel gear spring lever pulley machine device " +
        "system method process project plan draft sketch design layout template model theory question answer problem solution idea lesson skill " +
        "craft trade practice routine schedule budget account list note report review guide manual checklist tunnel tower village street square " +
        "fountain bench path hill field barn cottage cabin shed studio gallery library museum classroom lecture exercise puzzle riddle game " +
        "ticket passport suitcase umbrella jacket scarf button pocket zipper ribbon stamp envelope parcel package crate barrel bucket spoon " +
        "plate bowl mug pan oven stove grill toaster blender whisk";

    private const string AdjectiveList =
        "quiet steady careful curious simple small large patient honest plain bright dark warm cool gentle sharp soft rough smooth heavy light " +
        "early late slow quick narrow wide deep shallow old new fresh stale tidy messy ordinary unusual familiar distant local common rare " +
        "practical clever modest humble stubborn flexible reliable fragile sturdy balanced crooked straight round hollow solid golden silver " +
        "rustic quaint cozy breezy misty dusty sunny rainy frosty windy leafy salty sweet bitter sour spicy crisp tender hearty wooden metal " +
        "woolen handmade secondhand vintage compact portable sensible thoughtful tireless restless cheerful serious playful subtle obvious " +
        "hidden open";

    // Base form, third person, -ing form.
    private const string VerbList =
        "build builds building make makes making keep keeps keeping carry carries carrying follow follows following shape shapes shaping " +
        "mend mends mending trace traces tracing gather gathers gathering sort sorts sorting plan plans planning test tests testing " +
        "measure measures measuring explore explores exploring share shares sharing borrow borrows borrowing repair repairs repairing " +
        "polish polishes polishing sketch sketches sketching fold folds folding stack stacks stacking balance balances balancing cover covers " +
        "covering open opens opening close closes closing read reads reading write writes writing learn learns learning teach teaches teaching " +
        "notice notices noticing remember remembers remembering choose chooses choosing watch watches watching wait waits waiting start starts " +
        "starting finish finishes finishing check checks checking fix fixes fixing store stores storing move moves moving turn turns turning " +
        "hold holds holding bring brings bringing place places placing cut cuts cutting join joins joining mix mixes mixing bake bakes baking " +
        "grow grows growing plant plants planting water waters watering paint paints painting draw draws drawing walk walks walking climb " +
        "climbs climbing settle settles settling wander wanders wandering return returns returning";

    private const string AdverbList =
        "slowly carefully quietly quickly gently steadily patiently honestly usually rarely often always sometimes simply mostly barely nearly " +
        "almost lately eventually mainly briefly roughly exactly";

    private const string OpenerList =
        "In practice|Most days|As it turns out|Strictly speaking|For what it is worth|At first|Later on|More often than not|Once in a while|" +
        "To be fair|Looking back|By the end of the week|Every so often|On a good day|Without much fuss";

    private const string FirstNameList =
        "Alma Bruno Clara Dario Elena Felix Greta Hugo Irene Jonas Katya Lukas Mira Nolan Olga Pavel Quinn Rosa Stefan Tilda Viktor Wren";

    private const string LastNameList =
        "Hartmann Okafor Lindqvist Moreau Petrov Rinaldi Sandoval Tanaka Whitfield Yilmaz Novak Bergstrom Castellan Duarte Eriksen Fontaine " +
        "Galloway Halvorsen";

    private const string CategoryList =
        "Kitchen Garden Workshop Travel Notes Reviews Guides Projects Journal Essays Tools Weekends Pantry Reading Music Maps Photography Recipes " +
        "Repairs Crafts Cycling Seasons Letters Archive";

    private const string PrefixWordList = "archive journal notes blog posts articles stories writing entries";

    public static byte[][] Nouns { get; } = Encode(NounList);

    public static byte[][] NounPlurals { get; } = EncodePlurals(NounList);

    public static byte[][] Adjectives { get; } = Encode(AdjectiveList);

    public static byte[][] Adverbs { get; } = Encode(AdverbList);

    public static byte[][] Openers { get; } = Encode(OpenerList, '|');

    public static byte[][] FirstNames { get; } = Encode(FirstNameList);

    public static byte[][] LastNames { get; } = Encode(LastNameList);

    /// <summary>The category names, capitalised as written.</summary>
    public static byte[][] Categories { get; } = Encode(CategoryList);

    public static string[] PrefixWords { get; } = PrefixWordList.Split(' ');

    /// <summary>The verbs' base forms.</summary>
    public static byte[][] VerbBase { get; }

    /// <summary>The verbs' third-person forms.</summary>
    public static byte[][] VerbS { get; }

    /// <summary>The verbs' -ing forms.</summary>
    public static byte[][] VerbIng { get; }

    static Lexicon()
    {
        var forms = VerbList.Split(' ');
        VerbBase = new byte[forms.Length / 3][];
        VerbS = new byte[forms.Length / 3][];
        VerbIng = new byte[forms.Length / 3][];
        for (var i = 0; i < forms.Length / 3; i++)
        {
            VerbBase[i] = Encoding.ASCII.GetBytes(forms[i * 3]);
            VerbS[i] = Encoding.ASCII.GetBytes(forms[(i * 3) + 1]);
            VerbIng[i] = Encoding.ASCII.GetBytes(forms[(i * 3) + 2]);
        }
    }

    private static byte[][] Encode(string list, char separator = ' ')
    {
        // No LINQ: the proxy project bans it, static initialisation included.
        var words = list.Split(separator);
        var encoded = new byte[words.Length][];
        for (var i = 0; i < words.Length; i++)
        {
            encoded[i] = Encoding.ASCII.GetBytes(words[i]);
        }

        return encoded;
    }

    private static byte[][] EncodePlurals(string list)
    {
        var words = list.Split(' ');
        var encoded = new byte[words.Length][];
        for (var i = 0; i < words.Length; i++)
        {
            encoded[i] = Encoding.ASCII.GetBytes(Plural(words[i]));
        }

        return encoded;
    }

    /// <summary>The regular plural. The noun list holds no irregular noun and none that ends in "o" or "f", so the rule is complete for it.</summary>
    private static string Plural(string noun)
    {
        if (noun.EndsWith('y') && noun.Length > 1 && "aeiou".IndexOf(noun[^2], StringComparison.Ordinal) < 0)
        {
            return string.Concat(noun.AsSpan(0, noun.Length - 1), "ies");
        }

        return noun.EndsWith('s') || noun.EndsWith('x') || noun.EndsWith('z') || noun.EndsWith("ch", StringComparison.Ordinal) || noun.EndsWith("sh", StringComparison.Ordinal)
            ? noun + "es"
            : noun + "s";
    }
}
