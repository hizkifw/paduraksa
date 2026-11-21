using System.Text;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>The blog's one theme: inline CSS, no external asset, one accent colour that differs per site.</summary>
internal static class Theming
{
    private const string Css =
        "*{box-sizing:border-box}body{margin:0;font:17px/1.65 Georgia,\"Times New Roman\",serif;color:var(--b);background:var(--g)}" +
        "a{color:var(--a);text-decoration:none}a:hover{text-decoration:underline}" +
        ".top{border-bottom:1px solid var(--l);background:#fff}" +
        ".bar{max-width:1040px;margin:0 auto;padding:18px 20px;display:flex;flex-wrap:wrap;align-items:baseline;justify-content:space-between;gap:12px}" +
        ".brand{font-size:24px;font-weight:700;color:var(--b)}nav a{margin-right:16px;font:14px system-ui,sans-serif}" +
        ".wrap{max-width:1040px;margin:0 auto;padding:28px 20px;display:flex;flex-wrap:wrap;gap:40px}" +
        "main{flex:1 1 560px;min-width:0}aside{flex:0 1 260px;font:14px/1.5 system-ui,sans-serif}" +
        "h1{font-size:34px;line-height:1.2;margin:0 0 8px}h2{font-size:24px;line-height:1.3;margin:1.6em 0 .4em}" +
        "h3{font-size:13px;text-transform:uppercase;letter-spacing:.06em;color:var(--m);margin:1.6em 0 .6em}" +
        ".meta{font:13px system-ui,sans-serif;color:var(--m);margin:.2em 0 1em}" +
        ".card{padding:0 0 24px;margin:0 0 24px;border-bottom:1px solid var(--l)}.card h2{margin:.5em 0 .1em}" +
        "img{max-width:100%;height:auto;display:block;border-radius:4px}" +
        "blockquote{margin:1.4em 0;padding:0 0 0 18px;border-left:3px solid var(--a);color:#444}blockquote footer{font:13px system-ui,sans-serif;color:var(--m)}" +
        "pre{background:#f0ede8;padding:14px;overflow:auto;font-size:14px;line-height:1.5;border-radius:4px}" +
        "ul,ol{padding-left:22px}aside ul,aside ol{list-style:none;padding:0}aside li{margin:0 0 6px}" +
        ".tags a{display:inline-block;margin:0 8px 4px 0}.hd{display:none}" +
        ".end{border-top:1px solid var(--l);padding:20px;text-align:center;font:13px system-ui,sans-serif;color:var(--m)}" +
        ".pager{display:flex;justify-content:space-between;font:15px system-ui,sans-serif;margin:24px 0}" +
        "@media(max-width:700px){h1{font-size:28px}.bar{padding:14px}}";

    public static byte[] Build(int accentHue) =>
        Encoding.ASCII.GetBytes(
            $"<style>:root{{--a:#{Hsl(accentHue, 55, 38):x6};--b:#1f1f22;--m:#6c6c72;--l:#e5e1da;--g:#faf8f4}}{Css}</style>");

    /// <summary>The colour as 0xRRGGBB. Saturation and lightness are percent.</summary>
    public static int Hsl(int hue, int saturation, int lightness)
    {
        var s = saturation / 100.0;
        var l = lightness / 100.0;
        var c = (1 - Math.Abs((2 * l) - 1)) * s;
        var x = c * (1 - Math.Abs((hue / 60.0 % 2) - 1));
        var m = l - (c / 2);
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return (ToByte(r + m) << 16) | (ToByte(g + m) << 8) | ToByte(b + m);
    }

    private static int ToByte(double channel) => (int)Math.Round(channel * 255);
}
