using System.Text;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Options;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Pcl2;

public sealed record Pcl2OptionsFileFingerprint(bool Exists, string? Sha256);

public sealed record Pcl2OptionsSelectionPreparation(
    bool IsBlocked,
    Pcl2OptionsSelectionSession? Session,
    IReadOnlyList<Pcl2Diagnostic> Diagnostics);

public sealed class Pcl2OptionsSelectionSession
{
    internal Pcl2OptionsSelectionSession(
        Pcl2Instance source,
        Pcl2Instance target,
        string sourceGameRoot,
        string targetGameRoot,
        string sourceOptionsPath,
        string targetOptionsPath,
        PhysicalDirectoryIdentity sourcePhysicalIdentity,
        PhysicalDirectoryIdentity targetPhysicalIdentity,
        Pcl2OptionsFileFingerprint sourceFingerprint,
        Pcl2OptionsFileFingerprint targetFingerprint,
        OptionsSelectionCatalog catalog,
        string sourceContent,
        string targetContent)
    {
        SourceInstance = source;
        TargetInstance = target;
        SourceGameRoot = sourceGameRoot;
        TargetGameRoot = targetGameRoot;
        SourceOptionsPath = sourceOptionsPath;
        TargetOptionsPath = targetOptionsPath;
        SourcePhysicalIdentity = sourcePhysicalIdentity;
        TargetPhysicalIdentity = targetPhysicalIdentity;
        SourceFingerprint = sourceFingerprint;
        TargetFingerprint = targetFingerprint;
        Catalog = catalog;
        SourceContent = sourceContent;
        TargetContent = targetContent;
    }

    public string SourceGameRoot { get; }
    public string TargetGameRoot { get; }
    public string SourceOptionsPath { get; }
    public string TargetOptionsPath { get; }
    public Pcl2OptionsFileFingerprint SourceFingerprint { get; }
    public Pcl2OptionsFileFingerprint TargetFingerprint { get; }
    public OptionsSelectionCatalog Catalog { get; }
    internal string SourceContent { get; }
    internal string TargetContent { get; }
    internal Pcl2Instance SourceInstance { get; }
    internal Pcl2Instance TargetInstance { get; }
    internal PhysicalDirectoryIdentity SourcePhysicalIdentity { get; }
    internal PhysicalDirectoryIdentity TargetPhysicalIdentity { get; }
}

public sealed record Pcl2SelectedOptionsPreview(
    bool IsBlocked,
    bool IsStale,
    string? SourceOptionsPath,
    string? TargetOptionsPath,
    string? Content,
    IReadOnlyList<OptionsMergeItem> PlannedChanges,
    IReadOnlyList<OptionsMergeItem> SkippedDifferences,
    IReadOnlyList<OptionsMergeItem> ProtectedDifferences,
    IReadOnlyList<OptionsMergeItem> TargetOnlyItems,
    IReadOnlyList<Pcl2Diagnostic> Diagnostics);

internal sealed class Pcl2ContentOptionsSelectionSession
{
    private readonly IReadOnlyInstanceAccess sourceAccess;
    private readonly IReadOnlyInstanceAccess targetAccess;

    internal Pcl2ContentOptionsSelectionSession(
        ContentProbeContext context,
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot,
        OptionsSelectionCatalog catalog,
        string sourceContent,
        string targetContent)
    {
        Generation = context.Generation;
        sourceAccess = context.Source;
        targetAccess = context.Target;
        SourceIdentity = context.Source.Identity;
        TargetIdentity = context.Target.Identity;
        SourceSnapshot = sourceSnapshot;
        TargetSnapshot = targetSnapshot;
        Catalog = catalog;
        SourceContent = sourceContent;
        TargetContent = targetContent;
    }

    internal long Generation { get; }

    internal ContentInstanceIdentity SourceIdentity { get; }

    internal ContentInstanceIdentity TargetIdentity { get; }

    internal ContentFileSnapshot SourceSnapshot { get; }

    internal ContentFileSnapshot TargetSnapshot { get; }

    internal OptionsSelectionCatalog Catalog { get; }

    internal string SourceContent { get; }

    internal string TargetContent { get; }

    internal bool IsBoundTo(
        ContentProbeContext context,
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot) =>
        context.Generation == Generation &&
        ReferenceEquals(context.Source, sourceAccess) &&
        ReferenceEquals(context.Target, targetAccess) &&
        context.Source.Identity == SourceIdentity &&
        context.Target.Identity == TargetIdentity &&
        SameSnapshot(SourceSnapshot, sourceSnapshot) &&
        SameSnapshot(TargetSnapshot, targetSnapshot);

    private static bool SameSnapshot(
        ContentFileSnapshot expected,
        ContentFileSnapshot actual) =>
        expected.RelativePath.Equals(actual.RelativePath) &&
        expected.Exists == actual.Exists &&
        expected.Length == actual.Length &&
        string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal) &&
        expected.LastWriteTimeUtc == actual.LastWriteTimeUtc &&
        expected.WindowsFileAttributes == actual.WindowsFileAttributes &&
        expected.Identity == actual.Identity;
}

internal sealed record Pcl2ContentSelectedOptionsPreview(
    bool IsStale,
    SelectedOptionsMergeResult? Result);

public sealed partial class Pcl2OptionsMigrationPreviewer
{
    internal const int MaximumOptionsFileBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public Pcl2OptionsSelectionPreparation PrepareSelection(
        Pcl2Instance source,
        Pcl2Instance target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var diagnostics = new List<Pcl2Diagnostic>();
        var paths = ValidateOptionsPaths(source, target, diagnostics, cancellationToken);
        if (paths.IsBlocked ||
            !TryReadOptionsFile(
                source,
                "source",
                MissingOptionsBehavior.BlockSource,
                diagnostics,
                out var sourceSnapshot,
                cancellationToken) ||
            !TryReadOptionsFile(
                target,
                "target",
                MissingOptionsBehavior.ReportMissingTarget,
                diagnostics,
                out var targetSnapshot,
                cancellationToken))
        {
            return new Pcl2OptionsSelectionPreparation(true, null, diagnostics.AsReadOnly());
        }

        if (!TryValidateOptionsSchemaVersion(
                sourceSnapshot.Content,
                targetSnapshot.Content,
                diagnostics))
        {
            return new Pcl2OptionsSelectionPreparation(true, null, diagnostics.AsReadOnly());
        }

        var plannerProtectedKeys = mergePlanner.PlanSelected(
                sourceSnapshot.Content,
                targetSnapshot.Content,
                new HashSet<string>(StringComparer.Ordinal))
            .ProtectedDifferences
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        var catalog = selectionCatalogBuilder.Build(
            sourceSnapshot.Content,
            targetSnapshot.Content,
            plannerProtectedKeys);
        var session = new Pcl2OptionsSelectionSession(
            source,
            target,
            paths.SourceGameRoot!,
            paths.TargetGameRoot!,
            paths.SourceOptionsPath!,
            paths.TargetOptionsPath!,
            paths.SourcePhysicalIdentity!.Value,
            paths.TargetPhysicalIdentity!.Value,
            sourceSnapshot.Fingerprint,
            targetSnapshot.Fingerprint,
            catalog,
            sourceSnapshot.Content,
            targetSnapshot.Content);
        return new Pcl2OptionsSelectionPreparation(false, session, diagnostics.AsReadOnly());
    }

    public Pcl2SelectedOptionsPreview PreviewSelected(
        Pcl2OptionsSelectionSession session,
        IReadOnlySet<string> selectedKeys,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selectedKeys);
        var diagnostics = new List<Pcl2Diagnostic>();
        var paths = ValidateOptionsPaths(
            session.SourceInstance,
            session.TargetInstance,
            diagnostics,
            cancellationToken);
        if (paths.IsBlocked ||
            !Pcl2PathNormalizer.AreEquivalent(paths.SourceOptionsPath!, session.SourceOptionsPath) ||
            !Pcl2PathNormalizer.AreEquivalent(paths.TargetOptionsPath!, session.TargetOptionsPath))
        {
            return BlockedSelectedPreview(false, session, diagnostics);
        }

        if (paths.SourcePhysicalIdentity != session.SourcePhysicalIdentity ||
            paths.TargetPhysicalIdentity != session.TargetPhysicalIdentity)
        {
            return SnapshotChangedSelectedPreview(session, diagnostics);
        }

        if (!TryReadOptionsFile(
                session.SourceInstance,
                "source",
                MissingOptionsBehavior.Silent,
                diagnostics,
                out var sourceSnapshot,
                cancellationToken) ||
            !TryReadOptionsFile(
                session.TargetInstance,
                "target",
                MissingOptionsBehavior.Silent,
                diagnostics,
                out var targetSnapshot,
                cancellationToken))
        {
            return BlockedSelectedPreview(false, session, diagnostics);
        }

        if (sourceSnapshot.Fingerprint != session.SourceFingerprint ||
            targetSnapshot.Fingerprint != session.TargetFingerprint)
        {
            return SnapshotChangedSelectedPreview(session, diagnostics);
        }

        var result = mergePlanner.PlanSelected(
            session.SourceContent,
            session.TargetContent,
            selectedKeys);
        return new Pcl2SelectedOptionsPreview(
            false,
            false,
            session.SourceOptionsPath,
            session.TargetOptionsPath,
            result.Content,
            result.PlannedChanges,
            result.SkippedDifferences,
            result.ProtectedDifferences,
            result.TargetOnlyItems,
            diagnostics.AsReadOnly());
    }

    private bool TryReadOptionsFile(
        Pcl2Instance instance,
        string role,
        MissingOptionsBehavior missingBehavior,
        List<Pcl2Diagnostic> diagnostics,
        out OptionsFileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        snapshot = new OptionsFileSnapshot(
            new Pcl2OptionsFileFingerprint(false, null),
            string.Empty);
        if (!Pcl2WindowsHandleGuard.TryOpenInstanceAccess(
                fileSystem,
                instance,
                cancellationToken,
                out var access,
                out var rejectedPath,
                out var readFailure) ||
            access is null ||
            instance.CapabilityAccess?.GameRootRelativePath is not NormalizedRelativePath gameRelative)
        {
            access?.Dispose();
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.OptionsReadFailed,
                Pcl2DiagnosticSeverity.Error,
                readFailure ?? $"The {role} options.txt has no capability-bound game root.",
                rejectedPath ?? instance.GameRoot,
                instance.Id));
            return false;
        }

        using (access)
        {
            var optionsRelative = Pcl2ReadPathGuard.Combine(gameRelative, "options.txt");
            BoundedFileSnapshot bounded;
            try
            {
                bounded = access.ReadMinecraftFile(
                    optionsRelative,
                    MaximumOptionsFileBytes,
                    cancellationToken);
            }
            catch (CapabilityBoundaryException exception)
            {
                diagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.OptionsReadFailed,
                    Pcl2DiagnosticSeverity.Error,
                    DiagnosticText.EscapeTechnicalValue(exception.Message),
                    access.GetMinecraftAbsolutePath(optionsRelative),
                    instance.Id));
                return false;
            }

            if (!bounded.Exists)
            {
                if (missingBehavior == MissingOptionsBehavior.BlockSource)
                {
                    diagnostics.Add(new Pcl2Diagnostic(
                        Pcl2DiagnosticCode.SourceOptionsMissing,
                        Pcl2DiagnosticSeverity.Error,
                        "The source has no options.txt to migrate.",
                        access.GetMinecraftAbsolutePath(optionsRelative),
                        instance.Id));
                    return false;
                }

                if (missingBehavior == MissingOptionsBehavior.ReportMissingTarget)
                {
                    diagnostics.Add(new Pcl2Diagnostic(
                        Pcl2DiagnosticCode.TargetOptionsMissing,
                        Pcl2DiagnosticSeverity.Info,
                        "The target has no options.txt; the planner will preview without creating it.",
                        access.GetMinecraftAbsolutePath(optionsRelative),
                        instance.Id));
                }

                return true;
            }

            snapshot = new OptionsFileSnapshot(
                new Pcl2OptionsFileFingerprint(true, bounded.Sha256.ToLowerInvariant()),
                DecodeOptionsContent(bounded.CopyBytes()));
            return true;
        }
    }

    private static string DecodeOptionsContent(byte[] bytes)
    {
        if (bytes.Length > MaximumOptionsFileBytes)
        {
            throw new CapabilityLimitExceededException(
                "The options.txt snapshot exceeded the 4 MiB parser limit.");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal Pcl2ContentOptionsSelectionSession PrepareSelection(
        ContentProbeContext context,
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        context.ThrowIfUnavailable();
        ValidateContentSnapshot(sourceSnapshot, requireExisting: true);
        ValidateContentSnapshot(targetSnapshot, requireExisting: false);
        var sourceContent = DecodeOptionsContent(sourceSnapshot.Bytes.CopyBytes());
        var targetContent = targetSnapshot.Exists
            ? DecodeOptionsContent(targetSnapshot.Bytes.CopyBytes())
            : string.Empty;
        if (!OptionsSchemaVersionPolicy.IsSafeForMigration(sourceContent, targetContent))
        {
            throw new OptionsSchemaVersionException();
        }

        var plannerProtectedKeys = mergePlanner.PlanSelected(
                sourceContent,
                targetContent,
                new HashSet<string>(StringComparer.Ordinal))
            .ProtectedDifferences
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        var catalog = selectionCatalogBuilder.Build(
            sourceContent,
            targetContent,
            plannerProtectedKeys);
        return new Pcl2ContentOptionsSelectionSession(
            context,
            sourceSnapshot,
            targetSnapshot,
            catalog,
            sourceContent,
            targetContent);
    }

    internal Pcl2ContentSelectedOptionsPreview PreviewSelected(
        Pcl2ContentOptionsSelectionSession session,
        ContentProbeContext context,
        ContentFileSnapshot currentSourceSnapshot,
        ContentFileSnapshot currentTargetSnapshot,
        IReadOnlySet<string> selectedKeys,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentSourceSnapshot);
        ArgumentNullException.ThrowIfNull(currentTargetSnapshot);
        ArgumentNullException.ThrowIfNull(selectedKeys);
        context.ThrowIfUnavailable();
        if (!session.IsBoundTo(context, currentSourceSnapshot, currentTargetSnapshot))
        {
            return new Pcl2ContentSelectedOptionsPreview(true, null);
        }

        return new Pcl2ContentSelectedOptionsPreview(
            false,
            mergePlanner.PlanSelected(
                session.SourceContent,
                session.TargetContent,
                selectedKeys));
    }

    internal SelectedOptionsMergeResult PreviewSelectedSnapshots(
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot,
        IReadOnlySet<string> selectedKeys,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        ArgumentNullException.ThrowIfNull(selectedKeys);
        ValidateContentSnapshot(sourceSnapshot, requireExisting: true);
        ValidateContentSnapshot(targetSnapshot, requireExisting: false);
        return mergePlanner.PlanSelected(
            DecodeOptionsContent(sourceSnapshot.Bytes.CopyBytes()),
            targetSnapshot.Exists
                ? DecodeOptionsContent(targetSnapshot.Bytes.CopyBytes())
                : string.Empty,
            selectedKeys);
    }

    internal static byte[] EncodeSelectedContent(
        string content,
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        var original = targetSnapshot.Exists
            ? targetSnapshot.Bytes.CopyBytes()
            : sourceSnapshot.Bytes.CopyBytes();
        var keepBom = original.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var payloadBytes = Utf8WithoutBom.GetByteCount(content);
        var totalBytes = checked(payloadBytes + (keepBom ? Encoding.UTF8.Preamble.Length : 0));
        if (totalBytes > MaximumOptionsFileBytes)
        {
            throw new CapabilityLimitExceededException(
                "The planned options.txt exceeded the 4 MiB output limit.");
        }

        var output = new byte[totalBytes];
        var offset = 0;
        if (keepBom)
        {
            Encoding.UTF8.Preamble.CopyTo(output);
            offset = Encoding.UTF8.Preamble.Length;
        }

        var written = Utf8WithoutBom.GetBytes(content, output.AsSpan(offset));
        if (offset + written != output.Length)
        {
            throw new InvalidOperationException("The options.txt UTF-8 length changed during encoding.");
        }

        return output;
    }

    private static void ValidateContentSnapshot(
        ContentFileSnapshot snapshot,
        bool requireExisting)
    {
        if (!string.Equals(snapshot.RelativePath.Value, "options.txt", StringComparison.Ordinal) ||
            snapshot.Length > MaximumOptionsFileBytes ||
            requireExisting && !snapshot.Exists)
        {
            throw new CapabilityBoundaryException(
                "The bounded options.txt snapshot is missing, relabeled, or oversized.");
        }
    }

    private static Pcl2SelectedOptionsPreview BlockedSelectedPreview(
        bool isStale,
        Pcl2OptionsSelectionSession session,
        List<Pcl2Diagnostic> diagnostics) =>
        new(
            true,
            isStale,
            session.SourceOptionsPath,
            session.TargetOptionsPath,
            null,
            [],
            [],
            [],
            [],
            diagnostics.AsReadOnly());

    private static Pcl2SelectedOptionsPreview SnapshotChangedSelectedPreview(
        Pcl2OptionsSelectionSession session,
        List<Pcl2Diagnostic> diagnostics)
    {
        diagnostics.Clear();
        diagnostics.Add(new Pcl2Diagnostic(
            Pcl2DiagnosticCode.OptionsSnapshotChanged,
            Pcl2DiagnosticSeverity.Error,
            "The source or target options.txt snapshot changed after selection was prepared; preview is blocked."));
        return BlockedSelectedPreview(true, session, diagnostics);
    }

    private enum MissingOptionsBehavior
    {
        Silent,
        BlockSource,
        ReportMissingTarget,
    }

    private sealed record OptionsFileSnapshot(
        Pcl2OptionsFileFingerprint Fingerprint,
        string Content);
}
