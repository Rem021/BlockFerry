using Microsoft.Windows.Storage.Pickers;

namespace BlockFerry.App.WinUI.Services;

internal sealed class FileSavePickerService(Microsoft.UI.WindowId ownerWindowId) : IFileSavePickerService
{
    public async Task<string?> PickSaveFileAsync(
        string suggestedFileName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileSavePicker(ownerWindowId)
        {
            SuggestedFileName = suggestedFileName,
        };
        picker.FileTypeChoices.Add("JSON 诊断", [".json"]);
        var result = await picker.PickSaveFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result?.Path;
    }

    public async Task<bool> SaveDiagnosticAsync(
        string suggestedFileName,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var selectedPath = await PickSaveFileAsync(suggestedFileName, cancellationToken);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return false;
        }

        await using var stream = new FileStream(
            selectedPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return true;
    }
}
