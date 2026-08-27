using System.Text;
using BlockFerry.Core.System;
using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal interface IThemePreferenceStore
{
    string? Read(CancellationToken cancellationToken = default);

    bool Write(string value, CancellationToken cancellationToken = default);
}

internal sealed class ThemePreferenceStore : IThemePreferenceStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly AppStorageGuard storage;
    private readonly NormalizedRelativePath relativePath;

    internal ThemePreferenceStore(AppStorageGuard storage)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        if (!NormalizedRelativePath.TryCreate("theme.txt", out var path, out _))
        {
            throw new InvalidOperationException("The theme preference path is invalid.");
        }

        relativePath = path!;
    }

    public string? Read(CancellationToken cancellationToken = default)
    {
        var result = storage.TryRead(relativePath, 16, cancellationToken);
        if (result.State != AppStorageReadState.Read || result.Bytes is null)
        {
            return null;
        }

        try
        {
            var value = StrictUtf8.GetString(result.Bytes).Trim();
            return value is "light" or "dark" ? value : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public bool Write(string value, CancellationToken cancellationToken = default)
    {
        if (value is not ("light" or "dark"))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var bytes = StrictUtf8.GetBytes(value);
        return storage.TryAtomicReplace(relativePath, bytes, cancellationToken).State ==
               AppStorageMutationState.CommittedVerified;
    }
}
