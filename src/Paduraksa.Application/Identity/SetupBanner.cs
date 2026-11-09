using System.Text;

namespace Paduraksa.Application.Identity;

/// <summary>
/// The first-run setup token as the server logs it and <c>paduraksa setup-token</c> prints it: one boxed banner, so the
/// token is impossible to miss among the startup lines, with the box sized to its longest line so the borders always line up.
/// </summary>
/// <remarks>
/// <para><b>One message.</b> The server passes the whole banner as the text of a single log entry, so a structured sink records it as one
/// event instead of splitting it line by line.</para>
/// <para><b>Plain ASCII when the output isn't UTF-8.</b> Docker log viewers and a terminal with an ASCII locale mangle box-drawing
/// characters, so the corners become <c>+</c>, the rules <c>-</c> and the sides <c>|</c>: decided from
/// <see cref="Console.OutputEncoding"/>, and the ASCII form is used when that can't be read.</para>
/// <para>This is the one deliberate exception to "never log a secret": the token is single-use, worthless once an Owner exists
/// (and deleted then), and whoever can read this log already has the host's shell, and with it the file.</para>
/// </remarks>
public static class SetupBanner
{
    /// <summary>The command that shows the banner again.</summary>
    public const string ReprintCommand = "paduraksa setup-token";

    /// <summary>The banner, with box-drawing characters when this console's output encoding is UTF-8.</summary>
    public static string Render(string token, string tokenPath) => Render(token, tokenPath, ConsoleSupportsUnicode());

    /// <summary>
    /// The banner, always with the given border style: <paramref name="unicode"/> true draws <c>╔═╗║╚╝</c>, false <c>+-|</c>.
    /// </summary>
    public static string Render(string token, string tokenPath, bool unicode)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentException.ThrowIfNullOrEmpty(tokenPath);

        string[] lines =
        [
            "Paduraksa first-run setup",
            string.Empty,
            $"Setup token:  {token}",
            string.Empty,
            "Open the admin app and enter it to create the Owner.",
            $"Also stored in {tokenPath}. Reprint with:",
            $"  {ReprintCommand}",
            "It works once and is useless after setup completes.",
        ];

        int width = 0;
        foreach (string line in lines)
        {
            width = Math.Max(width, line.Length);
        }

        char topLeft = unicode ? '╔' : '+';
        char topRight = unicode ? '╗' : '+';
        char horizontal = unicode ? '═' : '-';
        char vertical = unicode ? '║' : '|';
        char bottomLeft = unicode ? '╚' : '+';
        char bottomRight = unicode ? '╝' : '+';

        StringBuilder banner = new();
        banner.Append(topLeft).Append(horizontal, width + 4).Append(topRight).Append('\n');
        foreach (string line in lines)
        {
            banner.Append(vertical).Append("  ").Append(line).Append(' ', width - line.Length + 2).Append(vertical).Append('\n');
        }

        banner.Append(bottomLeft).Append(horizontal, width + 4).Append(bottomRight);
        return banner.ToString();
    }

    /// <summary>Whether the console's output encoding is UTF-8, so the box-drawing characters will survive; false when it can't be read.</summary>
    public static bool ConsoleSupportsUnicode()
    {
        try
        {
            return Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            // A console that can't say how it encodes gets the form that always works.
            return false;
        }
    }
}
