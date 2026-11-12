using System.Text;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Paduraksa.Application.Yaml;

/// <summary>
/// Writes a <see cref="YamlNode"/> tree as block-style YAML: two-space indent, no wrapping, sequences indented under their key, a sequence of short scalars in
/// flow style (<c>names: [a.example.com]</c>) and a multi-line string as a literal block. A scalar that is <see cref="YamlScalar.Plain"/> is a number or a boolean
/// and is written as it is; any other is text, and is quoted whenever plain it would read back as something else (<see cref="YamlScalars.NeedsQuotes"/>).
/// </summary>
internal static class YamlTreeWriter
{
    private const int MaxFlowWidth = 72;

    public static string Write(YamlNode root)
    {
        StringBuilder text = new();
        using (StringWriter writer = new(text) { NewLine = "\n" })
        {
            var settings = new EmitterSettings(bestIndent: 2, bestWidth: int.MaxValue, isCanonical: false, maxSimpleKeyLength: 1024, skipAnchorName: false, indentSequences: true, newLine: "\n");
            Emitter emitter = new(writer, settings);
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart());
            Emit(emitter, root);
            emitter.Emit(new DocumentEnd(isImplicit: true));
            emitter.Emit(new StreamEnd());
        }

        return text.ToString();
    }

    private static void Emit(Emitter emitter, YamlNode node)
    {
        switch (node)
        {
            case YamlScalar scalar:
                emitter.Emit(Scalar(scalar.Text, scalar.Plain));
                break;
            case YamlSequence sequence:
                var flow = sequence.Items.All(static i => i is YamlScalar { Text.Length: > 0 } s && !s.Text.Contains('\n')) && FlowWidth(sequence) <= MaxFlowWidth;
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, flow ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in sequence.Items)
                {
                    Emit(emitter, item);
                }

                emitter.Emit(new SequenceEnd());
                break;
            case YamlMapping mapping:
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));
                foreach (var entry in mapping.Entries)
                {
                    emitter.Emit(Key(entry.Key));
                    Emit(emitter, entry.Value);
                }

                emitter.Emit(new MappingEnd());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(node));
        }
    }

    private static int FlowWidth(YamlSequence sequence) => sequence.Items.Sum(static i => ((YamlScalar)i).Text.Length + 4);

    // A key is always text, whatever it looks like: 403 is the key "403".
    private static Scalar Key(string key) => TextScalar(key, allowLiteral: false);

    private static Scalar Scalar(string text, bool plain) =>
        plain
            ? new Scalar(AnchorName.Empty, TagName.Empty, text, ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: true)
            : TextScalar(text, allowLiteral: true);

    private static Scalar TextScalar(string text, bool allowLiteral)
    {
        if (allowLiteral && text.Contains('\n') && IsLiteralSafe(text))
        {
            return new Scalar(AnchorName.Empty, TagName.Empty, text, ScalarStyle.Literal, isPlainImplicit: false, isQuotedImplicit: true);
        }

        // Plain only for text that can't be mistaken for anything else: the emitter's own analysis misjudges some mixes of indicators and quotes. Everything else is quoted, single
        // when it can be (no line break, control character or edge whitespace), double otherwise, where every character can be escaped.
        var style = IsBoring(text) && !YamlScalars.NeedsQuotes(text)
            ? ScalarStyle.Plain
            : HasEdgeWhitespace(text) || HasOddCharacter(text) ? ScalarStyle.DoubleQuoted : ScalarStyle.SingleQuoted;
        return new Scalar(AnchorName.Empty, TagName.Empty, text, style, isPlainImplicit: style == ScalarStyle.Plain, isQuotedImplicit: true);
    }

    // Words, hosts, paths, addresses and URLs: letters, digits and a few joiners, starting and ending with something solid, with a colon only inside.
    private static bool IsBoring(string text)
    {
        if (text.Length == 0 || !(char.IsLetterOrDigit(text[0]) || text[0] is '/' or '_') || text[^1] is ' ' or ':' or '.' or '-')
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var fine = char.IsLetterOrDigit(c) || c is '.' or '_' or '/' or '-' or '+' or '@' or '=' or '(' or ')' or ',' or '~' or '$'
                || c == ' ' && text[i - 1] != ' ' && text[i + 1] != '#'
                || c == ':' && i > 0 && text[i + 1] != ' ';
            if (!fine)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasEdgeWhitespace(string text) => text.Length > 0 && (text[0] is ' ' or '\t' || text[^1] is ' ' or '\t');

    // A line break, a control character, a C1 control, a Unicode line or paragraph separator or a byte-order mark: written escaped, where every one of them can be spelled.
    private static bool HasOddCharacter(string text)
    {
        foreach (var c in text)
        {
            if (c < ' ' || c == '\u007f' || c is >= '\u0080' and <= '\u009f' || c is '\u2028' or '\u2029' or '\uFEFF')
            {
                return true;
            }
        }

        return false;
    }

    // A literal block keeps its text exactly only when no line starts or ends with whitespace, there are no empty edge lines and no carriage returns or other controls: the cases a block
    // scalar's indentation and chomping indicators get subtle in. Anything else is written double-quoted, which can escape every character.
    private static bool IsLiteralSafe(string text)
    {
        if (text.Length == 0 || text[0] is ' ' or '\n' or '\t' || text[^1] is ' ' or '\t' || text.EndsWith("\n\n", StringComparison.Ordinal))
        {
            return false;
        }

        var lineStart = true;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lineStart = true;
                continue;
            }

            if (c < ' ' || c == '\u007f' || c is >= '\u0080' and <= '\u009f' || c is '\u2028' or '\u2029' or '\uFEFF')
            {
                return false;
            }

            if (lineStart && c is ' ' or '\t')
            {
                return false;
            }

            lineStart = false;
        }

        return !text.Contains(" \n", StringComparison.Ordinal) && !text.Contains("\t\n", StringComparison.Ordinal);
    }
}
