using Microsoft.JSInterop;
using MudBlazor;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Copies text to the browser clipboard and reports the outcome through the snackbar. One implementation
/// instead of the copy-pasted helper every component used to carry.
/// </summary>
public class ClipboardService
{
    private readonly IJSRuntime _js;
    private readonly ISnackbar _snackbar;
    private readonly ILogger<ClipboardService> _logger;

    public ClipboardService(IJSRuntime js, ISnackbar snackbar, ILogger<ClipboardService> logger)
    {
        _js = js;
        _snackbar = snackbar;
        _logger = logger;
    }

    /// <returns>true when the text reached the clipboard.</returns>
    public async Task<bool> CopyAsync(string text, string successMessage = "Copied to clipboard")
    {
        try
        {
            await _js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            _snackbar.Add(successMessage, Severity.Success);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard write failed");
            _snackbar.Add("Couldn't copy automatically - select the text and copy it manually.", Severity.Warning);
            return false;
        }
    }
}
