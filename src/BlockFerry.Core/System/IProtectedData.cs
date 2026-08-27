namespace BlockFerry.Core.System;

using global::System.Security.Cryptography;

public interface IProtectedData
{
    byte[] Protect(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> entropy,
        int maximumOutputBytes);

    byte[] Unprotect(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> entropy,
        int maximumOutputBytes);
}

public sealed class ProtectedDataLimitException : CryptographicException
{
    public ProtectedDataLimitException(string message)
        : base(message)
    {
    }
}
