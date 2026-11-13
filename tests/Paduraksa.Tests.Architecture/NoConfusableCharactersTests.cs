using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Paduraksa.Tests.Architecture;

/// <summary>
/// No tracked text file may contain a character that reads as ASCII without being ASCII, or one that reads as
/// nothing at all. VS Code's "Unicode Highlight" flags those as ambiguous or invisible and every reviewer has to
/// stop and look; this repository is written in ASCII. The forbidden set and its replacements live in
/// <see cref="Forbidden"/>, what stays allowed and why is below, and the exclusions are
/// <see cref="ExcludedDirectories"/>.
/// <para>
/// Why a test and not a build-time analyzer: Roslyn has no diagnostic for the contents of a literal
/// (BannedApiAnalyzers bans API symbols, not characters), and this repository built with zero warnings while it was full
/// of en dashes. A test runs in <c>dotnet test</c> and CI, covers every tracked file (Markdown, YAML and shell too) and
/// says exactly where each offender is.
/// </para>
/// <para>
/// Allowed on purpose: box drawing and arrows (the setup-token banner is drawn with them and has an ASCII fallback);
/// SECTION SIGN and the other symbols that do not read as ASCII letters or digits; accented Latin and other scripts in
/// test data. Greek and Cyrillic are <b>not</b> allowed, being the homoglyphs this rule is about: a test that needs one
/// writes it as a <c>\uXXXX</c> escape with a comment.
/// </para>
/// </summary>
public sealed class NoConfusableCharactersTests
{
    /// <summary>
    /// What is forbidden, and what to write instead. A <see langword="null"/> replacement means there is no
    /// single ASCII equivalent: spell the text in ASCII. The ranges must not overlap; the first match wins.
    /// </summary>
    private static readonly (int First, int Last, string Name, string? Replacement)[] Forbidden =
    [
        // Dashes, hyphens and minus signs. In prose an em dash reads better as ":", ";" or a comma, but a
        // plain "-" is always correct and keeps the change mechanical.
        (0x2010, 0x2010, "HYPHEN", "-"),
        (0x2011, 0x2011, "NON-BREAKING HYPHEN", "-"),
        (0x2012, 0x2012, "FIGURE DASH", "-"),
        (0x2013, 0x2013, "EN DASH", "-"),
        (0x2014, 0x2014, "EM DASH", "-"),
        (0x2015, 0x2015, "HORIZONTAL BAR", "-"),
        (0x2212, 0x2212, "MINUS SIGN", "-"),
        (0xFE58, 0xFE58, "SMALL EM DASH", "-"),
        (0xFE63, 0xFE63, "SMALL HYPHEN-MINUS", "-"),
        (0xFF0D, 0xFF0D, "FULLWIDTH HYPHEN-MINUS", "-"),

        // Quotation marks and primes: ASCII quotes and apostrophes.
        (0x2018, 0x2018, "LEFT SINGLE QUOTATION MARK", "'"),
        (0x2019, 0x2019, "RIGHT SINGLE QUOTATION MARK", "'"),
        (0x201A, 0x201A, "SINGLE LOW-9 QUOTATION MARK", "'"),
        (0x201B, 0x201B, "SINGLE HIGH-REVERSED-9 QUOTATION MARK", "'"),
        (0x201C, 0x201C, "LEFT DOUBLE QUOTATION MARK", "\""),
        (0x201D, 0x201D, "RIGHT DOUBLE QUOTATION MARK", "\""),
        (0x201E, 0x201E, "DOUBLE LOW-9 QUOTATION MARK", "\""),
        (0x201F, 0x201F, "DOUBLE HIGH-REVERSED-9 QUOTATION MARK", "\""),
        (0x2032, 0x2032, "PRIME", "'"),
        (0x2033, 0x2033, "DOUBLE PRIME", "\""),
        (0x00B4, 0x00B4, "ACUTE ACCENT", "'"),
        (0x02BC, 0x02BC, "MODIFIER LETTER APOSTROPHE", "'"),

        // Spaces that are not U+0020: a range or a table cell reads the same with a plain space.
        (0x00A0, 0x00A0, "NO-BREAK SPACE", " "),
        (0x2000, 0x2000, "EN QUAD", " "),
        (0x2001, 0x2001, "EM QUAD", " "),
        (0x2002, 0x2002, "EN SPACE", " "),
        (0x2003, 0x2003, "EM SPACE", " "),
        (0x2004, 0x2004, "THREE-PER-EM SPACE", " "),
        (0x2005, 0x2005, "FOUR-PER-EM SPACE", " "),
        (0x2006, 0x2006, "SIX-PER-EM SPACE", " "),
        (0x2007, 0x2007, "FIGURE SPACE", " "),
        (0x2008, 0x2008, "PUNCTUATION SPACE", " "),
        (0x2009, 0x2009, "THIN SPACE", " "),
        (0x200A, 0x200A, "HAIR SPACE", " "),
        (0x202F, 0x202F, "NARROW NO-BREAK SPACE", " "),
        (0x205F, 0x205F, "MEDIUM MATHEMATICAL SPACE", " "),
        (0x3000, 0x3000, "IDEOGRAPHIC SPACE", " "),

        // Invisible characters: delete them.
        (0x200B, 0x200B, "ZERO WIDTH SPACE", ""),
        (0x200C, 0x200C, "ZERO WIDTH NON-JOINER", ""),
        (0x200D, 0x200D, "ZERO WIDTH JOINER", ""),
        (0x2060, 0x2060, "WORD JOINER", ""),
        (0xFEFF, 0xFEFF, "ZERO WIDTH NO-BREAK SPACE (BOM)", ""),
        (0x00AD, 0x00AD, "SOFT HYPHEN", ""),
        (0x202A, 0x202A, "LEFT-TO-RIGHT EMBEDDING", ""),
        (0x202B, 0x202B, "RIGHT-TO-LEFT EMBEDDING", ""),
        (0x202C, 0x202C, "POP DIRECTIONAL FORMATTING", ""),
        (0x202D, 0x202D, "LEFT-TO-RIGHT OVERRIDE", ""),
        (0x202E, 0x202E, "RIGHT-TO-LEFT OVERRIDE", ""),
        (0x2066, 0x2066, "LEFT-TO-RIGHT ISOLATE", ""),
        (0x2067, 0x2067, "RIGHT-TO-LEFT ISOLATE", ""),
        (0x2068, 0x2068, "FIRST STRONG ISOLATE", ""),
        (0x2069, 0x2069, "POP DIRECTIONAL ISOLATE", ""),

        // Punctuation with an obvious ASCII spelling.
        (0x2026, 0x2026, "HORIZONTAL ELLIPSIS", "..."),
        (0x00D7, 0x00D7, "MULTIPLICATION SIGN", "x"),
    ];

    /// <summary>
    /// The files and directories no scan can cover, in one place. Only tracked files are scanned, so build output
    /// is not listed - it is never reached. There is no vendored third-party text in the repository (no minified
    /// JavaScript or CSS), so nothing is excluded for that reason, and the generated EF migration designer and
    /// snapshot files are scanned like any other file (they contain no confusable characters; only their leading
    /// byte-order mark, which <see cref="Scan"/> allows).
    /// </summary>
    private static readonly string[] ExcludedDirectories = [".git", "bin", "obj", "TestResults", "node_modules"];

    /// <summary>
    /// The fuzz inputs are raw bytes a fuzzer made, and <c>.gitattributes</c> marks them binary (<c>git grep -I</c>, which
    /// <c>check-confusables.sh</c> runs, skips them too): a YAML or header input mutated into a Cyrillic letter is the input, not text.
    /// </summary>
    private static readonly string[] RawInputPrefixes = ["fuzz/corpus/", "fuzz/regressions/"];

    /// <summary>The full scan: every tracked text file, every offending occurrence, one line each.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TrackedTextFilesHaveNoConfusableCharacters()
    {
        var findings = new List<Finding>();
        var scanned = 0;
        foreach (var path in await TrackedFilesAsync())
        {
            var text = ReadText(Path.Combine(RepoRoot, path));
            if (text is null)
            {
                continue; // not UTF-8 text: no character in it can be confusable
            }

            scanned++;
            findings.AddRange(Scan(path, text));
        }

        Assert.True(scanned > 100, $"Only {scanned} tracked text files were found under {RepoRoot}: the enumeration is broken.");

        var report = new StringBuilder();
        foreach (var finding in findings)
        {
            report.Append("  ").Append(finding.Path).Append(':').Append(finding.Line).Append(':').Append(finding.Column)
                .Append(": U+").Append(finding.CodePoint.ToString("X4", CultureInfo.InvariantCulture)).Append(' ')
                .Append(finding.Name).Append(" -> ").Append(Describe(finding.Replacement)).AppendLine();
            report.Append("      ").AppendLine(finding.Source.Trim());
        }

        Assert.True(
            findings.Count == 0,
            $"{findings.Count} confusable or invisible character(s) in tracked text files "
            + $"(write ASCII; {nameof(NoConfusableCharactersTests)} has the full list of replacements):{Environment.NewLine}"
            + report);
    }

    /// <summary>Proof that the scanner works: three offenders, each with the right line and column.</summary>
    [Fact]
    public void ScannerFindsAnEnDashANbspAndAZeroWidthSpace()
    {
        // Line 1 is clean; line 2 reads "a" EN DASH "b" NBSP "c" ZERO WIDTH SPACE "d".
        var findings = Scan("sample.md", "clean\na\u2013b\u00A0c\u200Bd\ntail");

        Assert.Equal(3, findings.Count);
        Assert.Equal((2, 2, 0x2013, "-"), (findings[0].Line, findings[0].Column, findings[0].CodePoint, findings[0].Replacement));
        Assert.Equal((2, 4, 0x00A0, " "), (findings[1].Line, findings[1].Column, findings[1].CodePoint, findings[1].Replacement));
        Assert.Equal((2, 6, 0x200B, ""), (findings[2].Line, findings[2].Column, findings[2].CodePoint, findings[2].Replacement));
        Assert.Equal("EN DASH", findings[0].Name);
        Assert.Equal("NO-BREAK SPACE", findings[1].Name);
        Assert.Equal("ZERO WIDTH SPACE", findings[2].Name);
        Assert.All(findings, f => Assert.Equal("a\u2013b\u00A0c\u200Bd", f.Source));
    }

    /// <summary>A file may start with a byte-order mark (the EF migrations do); one anywhere else is a finding.</summary>
    [Fact]
    public void ALeadingByteOrderMarkIsFineButAStrayOneIsNot()
    {
        Assert.Empty(Scan("file.cs", "\uFEFFclass C;"));

        var finding = Assert.Single(Scan("file.cs", "class C;\uFEFF"));
        Assert.Equal(0xFEFF, finding.CodePoint);
        Assert.Equal(1, finding.Line);
        Assert.Equal(9, finding.Column);
    }

    /// <summary>The allowlist, pinned: characters that read as themselves must not be flagged.</summary>
    [Theory]
    [InlineData("\u00A7")] // SECTION SIGN: PLAN section references
    [InlineData("\u2192")] // RIGHTWARDS ARROW: the design pages' flows
    [InlineData("\u2194")] // LEFT RIGHT ARROW
    [InlineData("\u2500")] // BOX DRAWINGS LIGHT HORIZONTAL: the setup-token banner
    [InlineData("\u2554")] // BOX DRAWINGS DOUBLE DOWN AND RIGHT: the same banner
    [InlineData("\u2264")] // LESS-THAN OR EQUAL TO
    [InlineData("\u00B1")] // PLUS-MINUS SIGN
    [InlineData("\u00B7")] // MIDDLE DOT
    [InlineData("\u2153")] // VULGAR FRACTION ONE THIRD
    [InlineData("\u00B0")] // DEGREE SIGN
    [InlineData("\u00F7")] // DIVISION SIGN
    [InlineData("\u2705")] // WHITE HEAVY CHECK MARK: status tables
    [InlineData("\u00E9")] // LATIN SMALL LETTER E WITH ACUTE: prose and names
    public void CharactersThatReadAsThemselvesAreAllowed(string text) => Assert.Empty(Scan("allowed.md", text));

    /// <summary>Every offending occurrence in one file, in order, with a 1-based line and code-point column.</summary>
    private static List<Finding> Scan(string path, string text)
    {
        var findings = new List<Finding>();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var source = lines[index].TrimEnd('\r');
            var column = 1;
            foreach (var rune in source.EnumerateRunes())
            {
                // A BOM at the very start is the file's encoding marker, kept for the EF migration files; that is
                // the one place it is not invisible-but-present in the text.
                if (index == 0 && column == 1 && rune.Value == 0xFEFF)
                {
                    continue;
                }

                if (ForbiddenOrNull(rune) is { } rule)
                {
                    findings.Add(new Finding(path, index + 1, column, rune.Value, rule.Name, rule.Replacement, source));
                }

                column++;
            }
        }

        return findings;
    }

    /// <summary>The rule for one character, or null when it is allowed.</summary>
    private static (string Name, string Replacement)? ForbiddenOrNull(Rune rune)
    {
        var value = rune.Value;
        foreach (var (first, last, name, replacement) in Forbidden)
        {
            if (value >= first && value <= last)
            {
                return (name, replacement!);
            }
        }

        // Fullwidth ASCII (U+FF01-U+FF5E) is the ASCII character shifted by 0xFEE0.
        if (value is >= 0xFF01 and <= 0xFF5E)
        {
            return ("FULLWIDTH ASCII FORM", ((char)(value - 0xFEE0)).ToString());
        }

        // Greek and Cyrillic letters: the homoglyphs. No ASCII equivalent - spell the word in Latin letters.
        return value is >= 0x0370 and <= 0x04FF && Rune.IsLetter(rune)
            ? ("GREEK OR CYRILLIC LETTER", "<write it in ASCII>")
            : null;
    }

    private static string Describe(string replacement) => replacement switch
    {
        "" => "<nothing>",
        " " => "<space>",
        _ => $"'{replacement}'",
    };

    /// <summary>
    /// The tracked files, relative to the repository root, from <c>git ls-files</c> where git is available (the
    /// repository's own definition of "tracked"), else by walking the tree without
    /// <see cref="ExcludedDirectories"/>.
    /// </summary>
    private static async Task<List<string>> TrackedFilesAsync() =>
        [.. (await GitLsFilesAsync() ?? WalkRepository()).Where(file => !RawInputPrefixes.Any(prefix => file.StartsWith(prefix, StringComparison.Ordinal)))];

    private static async Task<List<string>?> GitLsFilesAsync()
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("-z");

        try
        {
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdout;
            _ = await stderr;
            if (process.ExitCode != 0)
            {
                return null;
            }

            return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null; // no git on PATH (a source tarball): fall back to walking the tree
        }
    }

    private static List<string> WalkRepository()
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(RepoRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(child), StringComparer.Ordinal))
                {
                    pending.Push(child);
                }
            }

            files.AddRange(
                Directory.EnumerateFiles(directory).Select(f => Path.GetRelativePath(RepoRoot, f).Replace('\\', '/')));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>The file as UTF-8 text, or null when it is not text (a NUL byte, or bytes that are not UTF-8).</summary>
    private static string? ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Paduraksa.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Paduraksa.slnx not found above {AppContext.BaseDirectory}");
    }

    /// <summary>One offending character: where it is, what it is, and what to write instead.</summary>
    private sealed record Finding(
        string Path,
        int Line,
        int Column,
        int CodePoint,
        string Name,
        string Replacement,
        string Source);
}
