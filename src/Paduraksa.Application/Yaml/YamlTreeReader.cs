using System.Text;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Paduraksa.Application.Yaml;

/// <summary>What went wrong reading YAML text: a code from <see cref="YamlErrorCodes"/>, a message that never names a CLR type, and where (1-based; 0 when it isn't a place).</summary>
internal readonly record struct YamlProblem(string Code, string Message, long Line = 0, long Column = 0)
{
    public string Located => Line > 0 ? $"{Message} (line {Line}, column {Column})" : Message;
}

/// <summary>
/// Reads one YAML document into a <see cref="YamlNode"/> tree under <see cref="YamlLimits"/>. It never deserialises into types: YamlDotNet is used only as
/// a parser, so no tag can name a type to instantiate. Every tag is refused, there is exactly one document, a mapping's keys are unique scalars, and an alias
/// is expanded by copying inside the node budget, so a bomb stops at <see cref="YamlLimits.MaxNodes"/> instead of at memory. Merge keys (<c>&lt;&lt;</c>) aren't
/// special: they are an unknown key where the schema meets them.
/// </summary>
internal static class YamlTreeReader
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryRead(ReadOnlySpan<byte> utf8, YamlLimits limits, out YamlNode? root, out YamlProblem problem)
    {
        root = null;
        if (utf8.Length > limits.MaxBytes)
        {
            problem = new(YamlErrorCodes.TooLarge, $"The YAML is {utf8.Length} bytes; the limit is {limits.MaxBytes}.");
            return false;
        }

        string text;
        try
        {
            text = Strict.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            problem = new(YamlErrorCodes.Encoding, "The YAML isn't valid UTF-8.");
            return false;
        }

        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        try
        {
            return new Builder(limits, text).Build(out root, out problem);
        }
        catch (YamlException ex)
        {
            problem = new(YamlErrorCodes.Syntax, Describe(ex), ex.Start.Line, ex.Start.Column);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or FormatException or OverflowException)
        {
            // The scanner reports some malformed input (an unterminated flow collection followed by more lines, say) with these instead of its own exception: still just bad input.
            problem = new(YamlErrorCodes.Syntax, "The YAML is malformed.");
            return false;
        }
    }

    // The parser's own message is the useful part ("Did not find expected key"); its position is carried separately.
    private static string Describe(YamlException ex)
    {
        var message = ex.Message;
        var marker = message.IndexOf("): ", StringComparison.Ordinal);
        return marker >= 0 && message.StartsWith('(') ? message[(marker + 3)..] : message;
    }

    // An alias name is the input's to make as long as it likes; a message quotes at most this much of it.
    private static string Shown(string name) => name.Length <= 40 ? name : name[..37] + "...";

    private sealed class Builder(YamlLimits limits, string text)
    {
        private readonly Dictionary<string, (YamlNode Node, int Height)> _anchors = new(StringComparer.Ordinal);
        private int _nodes;
        private int _aliases;
        private int _documents;
        private YamlProblem _problem;

        public bool Build(out YamlNode? root, out YamlProblem problem)
        {
            root = null;
            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case StreamStart or StreamEnd or Comment:
                        break;
                    case DocumentStart start:
                        if (++_documents > 1)
                        {
                            problem = new(YamlErrorCodes.Documents, "The YAML has more than one document; import one.", start.Start.Line, start.Start.Column);
                            return false;
                        }

                        if (!ReadNode(parser, 1, out root, out _))
                        {
                            problem = _problem;
                            return false;
                        }

                        break;
                    case DocumentEnd:
                        break;
                    default:
                        problem = new(YamlErrorCodes.Syntax, "Unexpected content.", parser.Current!.Start.Line, parser.Current.Start.Column);
                        return false;
                }
            }

            if (root is null)
            {
                problem = new(YamlErrorCodes.Documents, "The YAML is empty.");
                return false;
            }

            problem = default;
            return true;
        }

        // Reads the node whose first event is the parser's next one. Height is the nesting below the node (a scalar is 1), for aliases.
        private bool ReadNode(Parser parser, int depth, out YamlNode? node, out int height)
        {
            node = null;
            height = 1;
            if (!parser.MoveNext())
            {
                return Fail(YamlErrorCodes.Syntax, "The YAML ended unexpectedly.", 0, 0);
            }

            var current = parser.Current!;
            switch (current)
            {
                case Scalar scalar:
                    return ReadScalar(scalar, out node);
                case SequenceStart sequence:
                    return ReadSequence(parser, sequence, depth, out node, out height);
                case MappingStart mapping:
                    return ReadMapping(parser, mapping, depth, out node, out height);
                case AnchorAlias alias:
                    return ExpandAlias(alias, depth, out node, out height);
                default:
                    return Fail(YamlErrorCodes.Syntax, "Unexpected content.", current.Start.Line, current.Start.Column);
            }
        }

        private bool ReadScalar(Scalar scalar, out YamlNode? node)
        {
            node = null;
            if (!scalar.Tag.IsEmpty)
            {
                return Fail(YamlErrorCodes.Tag, "Tags aren't supported; write the value without one.", scalar.Start.Line, scalar.Start.Column);
            }

            if (!CountNode(scalar.Start))
            {
                return false;
            }

            if (scalar.Value.Length > limits.MaxScalarLength)
            {
                return Fail(YamlErrorCodes.ScalarTooLong, $"A value is {scalar.Value.Length} characters; the limit is {limits.MaxScalarLength}.", scalar.Start.Line, scalar.Start.Column);
            }

            var created = new YamlScalar(scalar.Value, scalar.Style == ScalarStyle.Plain, scalar.Start.Line, scalar.Start.Column);
            node = created;
            return Anchor(scalar.Anchor, scalar.Start, created, 1);
        }

        private bool ReadSequence(Parser parser, SequenceStart start, int depth, out YamlNode? node, out int height)
        {
            node = null;
            height = 1;
            if (!start.Tag.IsEmpty)
            {
                return Fail(YamlErrorCodes.Tag, "Tags aren't supported; write the value without one.", start.Start.Line, start.Start.Column);
            }

            if (depth > limits.MaxDepth)
            {
                return Fail(YamlErrorCodes.Depth, $"The YAML nests deeper than {limits.MaxDepth} levels.", start.Start.Line, start.Start.Column);
            }

            if (!CountNode(start.Start))
            {
                return false;
            }

            YamlSequence sequence = new(start.Start.Line, start.Start.Column);
            var below = 0;
            while (true)
            {
                if (!parser.MoveNext())
                {
                    return Fail(YamlErrorCodes.Syntax, "The YAML ended unexpectedly.", 0, 0);
                }

                if (parser.Current is SequenceEnd)
                {
                    break;
                }

                if (!ReadItem(parser, depth + 1, out var item, out var itemHeight))
                {
                    return false;
                }

                sequence.Items.Add(item!);
                below = Math.Max(below, itemHeight);
            }

            node = sequence;
            height = below + 1;
            return Anchor(start.Anchor, start.Start, sequence, height);
        }

        private bool ReadMapping(Parser parser, MappingStart start, int depth, out YamlNode? node, out int height)
        {
            node = null;
            height = 1;
            if (!start.Tag.IsEmpty)
            {
                return Fail(YamlErrorCodes.Tag, "Tags aren't supported; write the value without one.", start.Start.Line, start.Start.Column);
            }

            if (depth > limits.MaxDepth)
            {
                return Fail(YamlErrorCodes.Depth, $"The YAML nests deeper than {limits.MaxDepth} levels.", start.Start.Line, start.Start.Column);
            }

            if (!CountNode(start.Start))
            {
                return false;
            }

            YamlMapping mapping = new(start.Start.Line, start.Start.Column);
            HashSet<string> seen = new(StringComparer.Ordinal);
            var below = 0;
            while (true)
            {
                if (!parser.MoveNext())
                {
                    return Fail(YamlErrorCodes.Syntax, "The YAML ended unexpectedly.", 0, 0);
                }

                if (parser.Current is MappingEnd)
                {
                    break;
                }

                // The key: a scalar (or an alias of one), never a collection.
                var keyEvent = parser.Current!;
                if (!ReadItem(parser, depth + 1, out var key, out _, current: keyEvent) || key is not YamlScalar keyScalar)
                {
                    return _problem.Code is { Length: > 0 } ? false : Fail(YamlErrorCodes.KeyInvalid, "A key must be plain text.", keyEvent.Start.Line, keyEvent.Start.Column);
                }

                if (keyScalar.Text.Length > limits.MaxKeyLength)
                {
                    return Fail(YamlErrorCodes.KeyInvalid, $"A key is longer than {limits.MaxKeyLength} characters.", keyScalar.Line, keyScalar.Column);
                }

                if (!seen.Add(keyScalar.Text))
                {
                    return Fail(YamlErrorCodes.KeyDuplicate, $"The key '{keyScalar.Text}' is written twice in one mapping.", keyScalar.Line, keyScalar.Column);
                }

                if (!parser.MoveNext())
                {
                    return Fail(YamlErrorCodes.Syntax, "The YAML ended unexpectedly.", 0, 0);
                }

                if (!ReadItem(parser, depth + 1, out var value, out var valueHeight, current: parser.Current))
                {
                    return false;
                }

                mapping.Entries.Add(new YamlEntry(keyScalar.Text, keyScalar.Line, keyScalar.Column, value!));
                below = Math.Max(below, valueHeight);
            }

            node = mapping;
            height = below + 1;
            return Anchor(start.Anchor, start.Start, mapping, height);
        }

        // An item whose first event is already the parser's current one (a key or value read by the mapping loop) or still to come.
        private bool ReadItem(Parser parser, int depth, out YamlNode? node, out int height, ParsingEvent? current = null)
        {
            node = null;
            height = 1;
            current ??= parser.Current!;
            switch (current)
            {
                case Scalar scalar:
                    return ReadScalar(scalar, out node);
                case SequenceStart sequence:
                    return ReadSequence(parser, sequence, depth, out node, out height);
                case MappingStart mapping:
                    return ReadMapping(parser, mapping, depth, out node, out height);
                case AnchorAlias alias:
                    return ExpandAlias(alias, depth, out node, out height);
                default:
                    return Fail(YamlErrorCodes.Syntax, "Unexpected content.", current.Start.Line, current.Start.Column);
            }
        }

        private bool Anchor(AnchorName anchor, Mark at, YamlNode node, int height)
        {
            if (anchor.IsEmpty)
            {
                return true;
            }

            if (_anchors.Count >= limits.MaxAnchors && !_anchors.ContainsKey(anchor.Value))
            {
                return Fail(YamlErrorCodes.AnchorLimit, $"The YAML defines more than {limits.MaxAnchors} anchors.", at.Line, at.Column);
            }

            _anchors[anchor.Value] = (node, height);
            return true;
        }

        private bool ExpandAlias(AnchorAlias alias, int depth, out YamlNode? node, out int height)
        {
            node = null;
            height = 1;
            if (++_aliases > limits.MaxAliases)
            {
                return Fail(YamlErrorCodes.AliasLimit, $"The YAML uses more than {limits.MaxAliases} aliases.", alias.Start.Line, alias.Start.Column);
            }

            if (!_anchors.TryGetValue(alias.Value.Value, out var anchored))
            {
                return Fail(YamlErrorCodes.AliasUnknown, $"The alias '*{Shown(alias.Value.Value)}' has no anchor before it.", alias.Start.Line, alias.Start.Column);
            }

            if (depth + anchored.Height - 1 > limits.MaxDepth)
            {
                return Fail(YamlErrorCodes.Depth, $"The YAML nests deeper than {limits.MaxDepth} levels.", alias.Start.Line, alias.Start.Column);
            }

            height = anchored.Height;
            return Copy(anchored.Node, alias.Start, out node);
        }

        private bool Copy(YamlNode source, Mark at, out YamlNode? copy)
        {
            copy = null;
            if (!CountNode(at))
            {
                return false;
            }

            switch (source)
            {
                case YamlScalar scalar:
                    copy = new YamlScalar(scalar.Text, scalar.Plain, at.Line, at.Column);
                    return true;
                case YamlSequence sequence:
                    YamlSequence sequenceCopy = new(at.Line, at.Column);
                    foreach (var item in sequence.Items)
                    {
                        if (!Copy(item, at, out var itemCopy))
                        {
                            return false;
                        }

                        sequenceCopy.Items.Add(itemCopy!);
                    }

                    copy = sequenceCopy;
                    return true;
                case YamlMapping mapping:
                    YamlMapping mappingCopy = new(at.Line, at.Column);
                    foreach (var entry in mapping.Entries)
                    {
                        if (!Copy(entry.Value, at, out var valueCopy))
                        {
                            return false;
                        }

                        mappingCopy.Entries.Add(new YamlEntry(entry.Key, at.Line, at.Column, valueCopy!));
                    }

                    copy = mappingCopy;
                    return true;
                default:
                    return Fail(YamlErrorCodes.Syntax, "Unexpected content.", at.Line, at.Column);
            }
        }

        private bool CountNode(Mark at) =>
            ++_nodes <= limits.MaxNodes || Fail(YamlErrorCodes.Nodes, $"The YAML expands to more than {limits.MaxNodes} values (aliases count each time they are used).", at.Line, at.Column);

        private bool Fail(string code, string message, long line, long column)
        {
            _problem = new YamlProblem(code, message, line, column);
            return false;
        }
    }
}
