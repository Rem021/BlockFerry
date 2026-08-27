using Microsoft.Windows.Storage.Pickers;

namespace BlockFerry.App.WinUI.Services;

internal sealed class FolderPickerService(Microsoft.UI.WindowId ownerWindowId) : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FolderPicker(ownerWindowId);
        var result = await picker.PickSingleFolderAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result?.Path;
    }
}
