using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// The challenge page, pre-split into byte segments around the per-request challenge and return target, one template per
/// token lifetime and difficulty. The inline script and style are hashed once at type initialization for the page's CSP;
/// there are no external resources at all, and the whole page is ASCII.
/// </summary>
/// <remarks>
/// The script's SHA-256 is written in the script itself (pure JS) because <c>crypto.subtle</c> is unavailable on a
/// plain-HTTP origin. It runs in <c>navigator.hardwareConcurrency</c> Web Workers (at most 8) built from a <c>Blob</c>
/// URL, each searching an interleaved counter range; the CSP allows exactly that with <c>worker-src blob:</c>.
/// </remarks>
internal sealed class ProofOfWorkPage
{
    // Colours are the brand's (the dashboard's favicon), so the gate reads as Paduraksa's own page.
    private const string Style = """
        :root{color-scheme:light dark;--bg:#f5f5f3;--fg:#111110;--muted:#5d5c58;--track:#e2e1dc}@media (prefers-color-scheme:dark){:root{--bg:#111110;--fg:#e7e5e0;--muted:#9a988f;--track:#2b2b29}}html{font-family:system-ui,-apple-system,"Segoe UI",sans-serif;line-height:1.5}body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:var(--fg)}main{display:flex;flex-direction:column;align-items:center;gap:.6rem;max-width:22rem;padding:2rem;text-align:center}svg{width:44px;height:44px;margin-bottom:.4rem}h1{font-size:1.05rem;font-weight:600;margin:0}p{margin:0;font-size:.9rem;color:var(--muted)}.bar{width:9rem;height:3px;margin-top:.6rem;border-radius:3px;background:var(--track);overflow:hidden}.bar::after{content:"";display:block;width:40%;height:100%;border-radius:3px;background:var(--fg);animation:slide 1.1s ease-in-out infinite}@keyframes slide{from{transform:translateX(-100%)}to{transform:translateX(250%)}}@media (prefers-reduced-motion:reduce){.bar::after{animation:none;width:100%;opacity:.35}}#progress:empty{display:none}footer{position:fixed;bottom:1.25rem;left:0;right:0;text-align:center;font-size:.75rem}footer a{color:var(--muted);text-decoration:none}footer a:hover{text-decoration:underline}
        """;

    private const string Script = """
        (function(){
        "use strict";
        var workerSource = '"use strict";' +
        'var K=new Int32Array([1116352408,1899447441,-1245643825,-373957723,961987163,1508970993,-1841331548,-1424204075,-670586216,310598401,607225278,1426881987,1925078388,-2132889090,-1680079193,-1046744716,-459576895,-272742522,264347078,604807628,770255983,1249150122,1555081692,1996064986,-1740746414,-1473132947,-1341970488,-1084653625,-958395405,-710438585,113926993,338241895,666307205,773529912,1294757372,1396182291,1695183700,1986661051,-2117940946,-1838011259,-1564481375,-1474664885,-1035236496,-949202525,-778901479,-694614492,-200395387,275423344,430227734,506948616,659060556,883997877,958139571,1322822218,1537002063,1747873779,1955562222,2024104815,-2067236844,-1933114872,-1866530822,-1538233109,-1090935817,-965641998]);' +
        'var W=new Int32Array(64),buf=new Uint8Array(128),out=new Uint8Array(32),prefix=null,prefixLength=0,difficulty=0,h0,h1,h2,h3,h4,h5,h6,h7;' +
        'function compress(offset){' +
        'var i,o,t1,t2,x,y,s0,s1;' +
        'for(i=0;i<16;i++){o=offset+i*4;W[i]=(buf[o]<<24)|(buf[o+1]<<16)|(buf[o+2]<<8)|buf[o+3];}' +
        'for(i=16;i<64;i++){x=W[i-15];y=W[i-2];s0=((x>>>7)|(x<<25))^((x>>>18)|(x<<14))^(x>>>3);s1=((y>>>17)|(y<<15))^((y>>>19)|(y<<13))^(y>>>10);W[i]=(W[i-16]+s0+W[i-7]+s1)|0;}' +
        'var a=h0,b=h1,c=h2,d=h3,e=h4,f=h5,g=h6,h=h7;' +
        'for(i=0;i<64;i++){s1=((e>>>6)|(e<<26))^((e>>>11)|(e<<21))^((e>>>25)|(e<<7));t1=(h+s1+((e&f)^(~e&g))+K[i]+W[i])|0;s0=((a>>>2)|(a<<30))^((a>>>13)|(a<<19))^((a>>>22)|(a<<10));t2=(s0+((a&b)^(a&c)^(b&c)))|0;h=g;g=f;f=e;e=(d+t1)|0;d=c;c=b;b=a;a=(t1+t2)|0;}h0=(h0+a)|0;h1=(h1+b)|0;h2=(h2+c)|0;h3=(h3+d)|0;h4=(h4+e)|0;h5=(h5+f)|0;h6=(h6+g)|0;h7=(h7+h)|0;}' +
        'function writeDigits(n,pos){var digits=0,t=n,p;if(n===0){buf[pos++]=48;return pos;}while(t>0){digits++;t=Math.floor(t/10);}p=pos+digits;while(n>0){buf[--p]=48+(n%10);n=Math.floor(n/10);}return pos+digits;}' +
        'function hash(blocks){var i,b;h0=1779033703;h1=-1150833019;h2=1013904242;h3=-1521486534;h4=1359893119;h5=-1694144372;h6=528734635;h7=1541459225;for(b=0;b<blocks;b++){compress(b*64);}out[0]=(h0>>>24)&255;out[1]=(h0>>>16)&255;out[2]=(h0>>>8)&255;out[3]=h0&255;out[4]=(h1>>>24)&255;out[5]=(h1>>>16)&255;out[6]=(h1>>>8)&255;out[7]=h1&255;out[8]=(h2>>>24)&255;out[9]=(h2>>>16)&255;out[10]=(h2>>>8)&255;out[11]=h2&255;out[12]=(h3>>>24)&255;out[13]=(h3>>>16)&255;out[14]=(h3>>>8)&255;out[15]=h3&255;out[16]=(h4>>>24)&255;out[17]=(h4>>>16)&255;out[18]=(h4>>>8)&255;out[19]=h4&255;out[20]=(h5>>>24)&255;out[21]=(h5>>>16)&255;out[22]=(h5>>>8)&255;out[23]=h5&255;out[24]=(h6>>>24)&255;out[25]=(h6>>>16)&255;out[26]=(h6>>>8)&255;out[27]=h6&255;out[28]=(h7>>>24)&255;out[29]=(h7>>>16)&255;out[30]=(h7>>>8)&255;out[31]=h7&255;}' +
        'function leadingZeroBits(){var i,v,bits=0;for(i=0;i<32;i++){v=out[i];if(v===0){bits+=8;continue;}bits+=Math.clz32(v)-24;break;}return bits;}' +
        'self.onmessage=function(event){var d=event.data,n=d.start,stride=d.stride;prefix=new TextEncoder().encode(d.challenge+":");prefixLength=prefix.length;difficulty=d.difficulty;for(;;){buf.fill(0);buf.set(prefix,0);var length=writeDigits(n,prefixLength);buf[length]=0x80;var bitLength=length*8;var blocks=((length+9+63)>>6);var end=blocks*64;buf[end-4]=(bitLength>>>24)&255;buf[end-3]=(bitLength>>>16)&255;buf[end-2]=(bitLength>>>8)&255;buf[end-1]=bitLength&255;hash(blocks);if(leadingZeroBits()>=difficulty){self.postMessage({solution:n});return;}n+=stride;}};';
        var challenge=document.getElementById("challenge").value;
        var difficulty=parseInt(document.getElementById("difficulty").value,10);
        var form=document.getElementById("pow");
        var progress=document.getElementById("progress");
        var solution=document.getElementById("solution");
        var count=Math.max(1,Math.min(navigator.hardwareConcurrency||1,8));
        var workers=[];
        var found=false;
        function stop(n){if(found){return;}found=true;for(var i=0;i<workers.length;i++){workers[i].terminate();}solution.value=String(n);form.submit();}
        function fail(){progress.textContent="Your browser couldn't run the check. Reload to try again.";}
        try{
        var url=URL.createObjectURL(new Blob([workerSource],{type:"text/javascript"}));
        for(var i=0;i<count;i++){(function(start){
        var worker=new Worker(url);
        worker.onmessage=function(event){if(event.data.solution!==undefined){stop(event.data.solution);}};
        worker.onerror=fail;
        worker.postMessage({challenge:challenge,difficulty:difficulty,start:start,stride:count});
        workers.push(worker);
        })(i);}
        }catch(error){fail();}
        })();
        """;

    private static readonly PrecomputedHeader[] Headers =
    [
        new("Content-Security-Policy", new StringValues(
            $"default-src 'none'; script-src '{Hash(Script)}'; style-src '{Hash(Style)}'; worker-src blob:; form-action 'self'; base-uri 'none'; frame-ancestors 'none'")),
        new("Referrer-Policy", new StringValues("no-referrer")),
        new("X-Content-Type-Options", new StringValues("nosniff")),
    ];

    private readonly byte[] _head;
    private readonly byte[] _middle;
    private readonly byte[] _tail;

    public ProofOfWorkPage(int difficulty)
    {
        string head = string.Concat(
            "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
            "<title>Checking your browser</title><style>", Style, "</style></head><body><main>",
            "<svg viewBox=\"0 0 32 32\" role=\"img\" aria-label=\"Paduraksa\"><path fill=\"currentColor\" d=\"M4 27V19h2v-5h2V9h2V5h4v22zM28 27V19h-2v-5h-2V9h-2V5h-4v22z\"/>",
            "<rect fill=\"currentColor\" opacity=\"0.45\" x=\"2\" y=\"28\" width=\"28\" height=\"2\" rx=\"1\"/></svg>",
            "<h1>Checking your browser</h1>",
            "<noscript><p>Enable JavaScript to continue.</p></noscript>",
            "<div class=\"bar\" aria-hidden=\"true\"></div>",
            "<p id=\"progress\" role=\"status\" aria-live=\"polite\"></p>",
            "<form id=\"pow\" method=\"post\" action=\"/.well-known/paduraksa/pow\" hidden>",
            "<input id=\"challenge\" name=\"challenge\" type=\"hidden\" value=\"");
        string middle = string.Concat(
            "\"><input id=\"solution\" name=\"solution\" type=\"hidden\" value=\"\">",
            "<input id=\"return\" name=\"return\" type=\"hidden\" value=\"");
        string tail = string.Concat(
            "\"><input id=\"difficulty\" type=\"hidden\" value=\"",
            difficulty.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "\"></form><script>", Script, "</script></main>",
            "<footer><a href=\"https://github.com/hizkifw/paduraksa\">Protected by Paduraksa</a></footer></body></html>\n");
        _head = Encoding.ASCII.GetBytes(head);
        _middle = Encoding.ASCII.GetBytes(middle);
        _tail = Encoding.ASCII.GetBytes(tail);
    }

    /// <summary>The fixed headers the page carries: a strict CSP with the inline script's and style's hashes.</summary>
    public static PrecomputedHeader[] SecurityHeaders => Headers;

    /// <summary>The page's CSP value, for a test that recomputes the hashes from what the page really contains.</summary>
    internal static string ContentSecurityPolicy => Headers[0].Value.ToString();

    internal static string ScriptText => Script;

    internal static string StyleText => Style;

    /// <summary>Builds the page for one challenge and return target (the original path and query).</summary>
    public PrecomputedResponse Build(ReadOnlySpan<char> challenge, string returnTarget)
    {
        var challengeBytes = Encoding.ASCII.GetBytes(challenge.ToString());
        var returnBytes = Encoding.UTF8.GetBytes(WebUtility.HtmlEncode(returnTarget));
        var body = new byte[_head.Length + challengeBytes.Length + _middle.Length + returnBytes.Length + _tail.Length];
        var position = 0;
        _head.CopyTo(body, position);
        position += _head.Length;
        challengeBytes.CopyTo(body, position);
        position += challengeBytes.Length;
        _middle.CopyTo(body, position);
        position += _middle.Length;
        returnBytes.CopyTo(body, position);
        position += returnBytes.Length;
        _tail.CopyTo(body, position);
        return PrecomputedResponse.Create(StatusCodes.Status200OK, body, "text/html; charset=utf-8", "no-store", Headers);
    }

    private static string Hash(string content) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
