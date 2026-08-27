namespace BlockFerry.Core.Content;

internal interface IContentAdapter
{
    string Id { get; }

    ContentProbeResult Probe(
        ContentProbeContext context,
        CancellationToken cancellationToken);

    ContentCatalog BuildCatalog(
        ContentProbeContext context,
        CancellationToken cancellationToken);

    ContentAdapterPlan Plan(
        ContentProbeContext context,
        ContentCatalog catalog,
        ValidatedContentSelection selection,
        CancellationToken cancellationToken);

    ContentStageResult Stage(
        ContentAdapterPlan plan,
        CancellationToken cancellationToken);

    ContentVerificationResult Verify(
        ContentStageResult staged,
        IReadOnlyList<ContentFileSnapshot> pathBoundRereads,
        CancellationToken cancellationToken);

    IReadOnlySet<ContentRelativePath> RegenerateAllowedPaths(
        ContentProbeContext context,
        CancellationToken cancellationToken);

    IReadOnlySet<ContentRelativePath> RegenerateRecoveryAllowedPaths(
        RecoveryCatalogContext context,
        IReadOnlySet<ContentRelativePath> storedCandidatePaths,
        CancellationToken cancellationToken);
}
