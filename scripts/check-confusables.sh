#!/bin/sh
# Quick local check for the rule NoConfusableCharactersTests (Paduraksa.Tests.Architecture) enforces: no tracked
# text file may contain a character that reads as ASCII without being ASCII, or one that reads as nothing at
# all. That test is the rule and runs in `dotnet test` and in CI; this is the same check in one grep, for when
# you want the answer in a second.
#
#   ./scripts/check-confusables.sh
#
# Exits 0 when the tree is clean, 1 when it is not, and 2 when `git grep` has no PCRE support. Two differences
# from the test, both in the test's favour: this does not report a byte-order mark in the middle of a file (one
# at the start is allowed and is the only kind the repository has), and its Greek/Cyrillic range is whole blocks
# rather than only letters. Fix what it reports; trust the test for the last word.
set -eu
cd "$(dirname "$0")/.."

# The forbidden set, as one PCRE class. Keep it in step with NoConfusableCharactersTests.Forbidden: dashes and
# minus signs, smart quotes and primes, the typographic spaces, zero-width characters, the soft hyphen, bidi
# controls, Greek and Cyrillic letters (homoglyphs), fullwidth ASCII, the ellipsis and the multiplication sign.
# Box drawing, arrows, the section sign and U+2016 (the double vertical line of the audit-chain notation) are
# deliberately absent: they are allowed.
class='[\x{00A0}\x{00AD}\x{00B4}\x{00D7}\x{02BC}\x{0370}-\x{04FF}'
class="${class}"'\x{2000}-\x{200D}\x{2010}-\x{2015}\x{2018}-\x{201F}\x{2026}\x{202A}-\x{202F}'
class="${class}"'\x{2032}\x{2033}\x{205F}\x{2060}\x{2066}-\x{2069}\x{2212}\x{3000}'
class="${class}"'\x{FE58}\x{FE63}\x{FF01}-\x{FF5E}]'

status=0
matches=$(git grep -n -P "$class" --) || status=$?

case $status in
    0)
        echo "Confusable or invisible characters in tracked files:" >&2
        printf '%s\n' "$matches" >&2
        echo >&2
        echo "Write ASCII instead; NoConfusableCharactersTests lists the replacement for each code point." >&2
        exit 1
        ;;
    1)
        echo "No confusable or invisible characters in tracked files."
        ;;
    *)
        echo "git grep -P failed (status $status): this needs a git built with PCRE support." >&2
        exit 2
        ;;
esac
