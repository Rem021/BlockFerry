namespace BlockFerry.App.WinUI.Services;

internal interface IFolderPickerService
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken);
}
