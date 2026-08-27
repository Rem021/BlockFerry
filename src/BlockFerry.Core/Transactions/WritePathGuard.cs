using System.Text;
using BlockFerry.Core.Content;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal static class WritePathGuard
{
    internal static bool TryNormalize(
        ContentRelativePath contentPath,
        out NormalizedRelativePath? normalized)
    {
        normalized = null;
        return contentPath is not null && TryNormalize(contentPath.Value, out normalized);
    }

    internal static bool TryNormalize(
        string candidate,
        out NormalizedRelativePath? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(candidate) ||
            !string.Equals(candidate, candidate.Normalize(NormalizationForm.FormC), StringComparison.Ordinal) ||
            !NormalizedRelativePath.TryCreate(candidate, out var created, out _) ||
            created is null ||
            created.Value.Length == 0)
        {
            return false;
        }

        normalized = created;
        return true;
    }

    internal static string CollisionKey(NormalizedRelativePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Value.Normalize(NormalizationForm.FormC);
    }
}

internal sealed class NormalizedRelativePathComparer : IEqualityComparer<NormalizedRelativePath>
{
    internal static NormalizedRelativePathComparer Instance { get; } = new();

    public bool Equals(NormalizedRelativePath? left, NormalizedRelativePath? right) =>
        ReferenceEquals(left, right) ||
        left is not null && right is not null &&
        string.Equals(
            WritePathGuard.CollisionKey(left),
            WritePathGuard.CollisionKey(right),
            StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(NormalizedRelativePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return StringComparer.OrdinalIgnoreCase.GetHashCode(WritePathGuard.CollisionKey(path));
    }
}
