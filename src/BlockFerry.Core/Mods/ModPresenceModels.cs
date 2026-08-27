using BlockFerry.Core.Content;

namespace BlockFerry.Core.Mods;

internal sealed record ModProbeLimits(
    int MaximumJarFiles,
    int MaximumZipEntries,
    int MaximumEntryBytes,
    long MaximumTotalBytes,
    long MaximumArchiveBytes,
    long MaximumCentralDirectoryBytes);

internal enum ModDeclarationKind
{
    FabricJson,
    QuiltJson,
    ForgeToml,
    NeoForgeToml,
}

internal sealed record ModPresenceEvidence(
    string ModId,
    string? Version,
    ContentRelativePath JarPath,
    ModDeclarationKind DeclarationKind);

internal sealed class ModPresenceResult
{
    private ModPresenceResult(
        IReadOnlyList<ModPresenceEvidence> evidence,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        Evidence = evidence;
        Diagnostics = diagnostics;
    }

    internal IReadOnlyList<ModPresenceEvidence> Evidence { get; }

    internal IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    internal static ModPresenceResult Create(
        IEnumerable<ModPresenceEvidence> evidence,
        IEnumerable<ContentDiagnostic> diagnostics)
    {
        var evidenceCopy = CopyBounded(evidence, 2_048, nameof(evidence));
        var diagnosticCopy = CopyBounded(
            diagnostics,
            ContentContractLimits.MaximumDiagnostics,
            nameof(diagnostics));
        if (evidenceCopy.Any(item =>
                item is null ||
                !ContentValueValidation.IsTechnicalId(item.ModId) ||
                item.Version is not null &&
                !ContentValueValidation.IsOptionalTechnicalValue(item.Version) ||
                item.JarPath is null ||
                item.JarPath.Value.Length == 0 ||
                !Enum.IsDefined(item.DeclarationKind)) ||
            diagnosticCopy.Any(item => item is null))
        {
            throw new ArgumentException("Mod probe values must be bounded and valid.");
        }

        return new ModPresenceResult(
            new ModReadOnlyList<ModPresenceEvidence>(evidenceCopy),
            new ModReadOnlyList<ContentDiagnostic>(diagnosticCopy));
    }

    private static List<T> CopyBounded<T>(
        IEnumerable<T> source,
        int maximum,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var copy = new List<T>(Math.Min(maximum, 64));
        using var enumerator = source.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (copy.Count == maximum)
            {
                throw new ArgumentException("The mod probe collection exceeded its bound.", parameterName);
            }

            copy.Add(enumerator.Current);
        }

        return copy;
    }
}

internal sealed class ModReadOnlyList<T>(IEnumerable<T> source) : IReadOnlyList<T>
{
    private readonly T[] values = source.ToArray();

    public int Count => values.Length;

    public T this[int index] => values[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();

    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}
