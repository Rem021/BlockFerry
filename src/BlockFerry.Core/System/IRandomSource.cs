using System.Security.Cryptography;

namespace BlockFerry.Core.System;

internal interface IRandomSource
{
    Guid NewGuid();

    void Fill(Span<byte> destination);
}

internal sealed class CryptographicRandomSource : IRandomSource
{
    public Guid NewGuid() => Guid.NewGuid();

    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}
