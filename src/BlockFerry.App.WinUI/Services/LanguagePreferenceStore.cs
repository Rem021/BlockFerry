using System.Text;
using BlockFerry.Core.System;
using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal interface ILanguagePreferenceStore
{
    string? Read(CancellationToken cancellationToken = default);

    bool Write(string value, CancellationToken cancellationToken = default);
}

internal sealed class LanguagePreferenceStore : ILanguagePreferenceStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly AppStorageGuard storage;
    private readonly NormalizedRelativePath relativePath;

    internal LanguagePreferenceStore(AppStorageGuard storage)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        if (!NormalizedRelativePath.TryCreate("language.txt", out var path, out _))
        {
            throw new InvalidOperationException("The language preference path is invalid.");
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
            return value is "zh-CN" or "en-US" ? value : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public bool Write(string value, CancellationToken cancellationToken = default)
    {
        if (value is not ("zh-CN" or "en-US"))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return storage.TryAtomicReplace(relativePath, StrictUtf8.GetBytes(value), cancellationToken).State ==
               AppStorageMutationState.CommittedVerified;
    }
}
