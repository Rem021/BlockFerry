using System.Security.Cryptography;
using System.Text;

namespace BlockFerry.Core.Pcl2;

/// <summary>
/// Binds a public discovery result to the normalized fields that Preview trusts.
/// A record created or materially changed outside discovery cannot manufacture
/// a valid proof for this process.
/// </summary>
internal static class Pcl2InstanceProof
{
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    public static Pcl2Instance Stamp(Pcl2Instance instance) =>
        instance with { DiscoveryProof = Compute(instance) };

    public static bool IsValid(Pcl2Instance instance)
    {
        if (string.IsNullOrEmpty(instance.DiscoveryProof))
        {
            return false;
        }

        try
        {
            var expected = Convert.FromHexString(Compute(instance));
            var actual = Convert.FromHexString(instance.DiscoveryProof);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Compute(Pcl2Instance instance)
    {
        var fields = new[]
        {
            instance.Id,
            Pcl2PathNormalizer.Normalize(instance.MinecraftRoot),
            Pcl2PathNormalizer.Normalize(instance.InstanceRoot),
            instance.GameRoot is null ? string.Empty : Pcl2PathNormalizer.Normalize(instance.GameRoot),
            instance.Isolation.ToString(),
            instance.HasUsableVersionMetadata ? "1" : "0",
            instance.MinecraftVersion ?? string.Empty,
        };
        using var hmac = new HMACSHA256(ProcessKey);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(string.Join('\u001F', fields))));
    }
}
