using System.Text;

using Microsoft.JSInterop;

namespace Paduraksa.Dashboard;

/// <summary>
/// What a page asks of the browser beyond rendering (<c>paduraksa.js</c>): copying text to the clipboard and saving a file the page made
/// (recovery codes, an audit export). The content goes over the circuit straight to the browser; nothing is stored or served from a URL.
/// </summary>
public sealed class DashboardBrowser(IJSRuntime js)
{
    /// <summary>Copies <paramref name="text"/>; false when the browser refused or the page isn't live.</summary>
    public async Task<bool> CopyAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return await js.InvokeAsync<bool>("paduraksa.copyText", text);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Saves <paramref name="content"/> as a file named <paramref name="fileName"/>; false when the page isn't live.</summary>
    public async Task<bool> DownloadAsync(string fileName, string contentType, Stream content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            using DotNetStreamReference stream = new(content, leaveOpen: true);
            await js.InvokeVoidAsync("paduraksa.download", fileName, contentType, stream);
            return true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Saves <paramref name="text"/> as a UTF-8 text file.</summary>
    public async Task<bool> DownloadTextAsync(string fileName, string text, string contentType = "text/plain;charset=utf-8")
    {
        ArgumentNullException.ThrowIfNull(text);
        using MemoryStream content = new(Encoding.UTF8.GetBytes(text));
        return await DownloadAsync(fileName, contentType, content);
    }
}
