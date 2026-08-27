namespace BlockFerry.App.WinUI.Services;

internal interface IFileSavePickerService
{
    Task<string?> PickSaveFileAsync(
        string suggestedFileName,
        CancellationToken cancellationToken);

    Task<bool> SaveDiagnosticAsync(
        string suggestedFileName,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);
}
