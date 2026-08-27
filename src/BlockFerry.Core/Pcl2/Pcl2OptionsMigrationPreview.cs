using BlockFerry.Core.Options;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Pcl2;

public sealed record Pcl2OptionsMigrationPreview(
    bool IsBlocked,
    string? SourceGameRoot,
    string? TargetGameRoot,
    string? SourceOptionsPath,
    string? TargetOptionsPath,
    OptionsMergeResult? MergeResult,
    IReadOnlyList<Pcl2Diagnostic> Diagnostics)
{
    public bool WouldChangeTarget => !IsBlocked && MergeResult?.Changed == true;

    public IReadOnlyList<OptionsMergeItem> Differences => MergeResult?.Items
        .Where(item => !string.Equals(item.SourceValue, item.TargetValue, StringComparison.Ordinal))
        .ToArray() ?? [];
}

public sealed partial class Pcl2OptionsMigrationPreviewer
{
    private readonly IFileSystemCapability fileSystem;
    private readonly OptionsMergePlanner mergePlanner;
    private readonly OptionsSelectionCatalogBuilder selectionCatalogBuilder;

    public Pcl2OptionsMigrationPreviewer(
        IFileSystemCapability fileSystem,
        OptionsMergePlanner? mergePlanner = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        this.fileSystem = fileSystem;
        this.mergePlanner = mergePlanner ?? new OptionsMergePlanner();
        selectionCatalogBuilder = new OptionsSelectionCatalogBuilder();
    }

    public Pcl2OptionsMigrationPreview Preview(
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
            return Blocked(paths, diagnostics);
        }

        if (!TryValidateOptionsSchemaVersion(
                sourceSnapshot.Content,
                targetSnapshot.Content,
                diagnostics))
        {
            return Blocked(paths, diagnostics);
        }

        var mergeResult = mergePlanner.Plan(sourceSnapshot.Content, targetSnapshot.Content);
        return new Pcl2OptionsMigrationPreview(
            false,
            paths.SourceGameRoot,
            paths.TargetGameRoot,
            paths.SourceOptionsPath,
            paths.TargetOptionsPath,
            mergeResult,
            diagnostics.AsReadOnly());
    }

    private ValidatedOptionsPaths ValidateOptionsPaths(
        Pcl2Instance source,
        Pcl2Instance target,
        List<Pcl2Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var sourceRoot = ValidateInstanceContract(source, "source", diagnostics);
        var targetRoot = ValidateInstanceContract(target, "target", diagnostics);
        AddMetadataBlocker(source, "source", diagnostics);
        AddMetadataBlocker(target, "target", diagnostics);
        var sourceOptionsPath = sourceRoot is null
            ? null
            : Pcl2PathNormalizer.Normalize(Path.Combine(sourceRoot, "options.txt"));
        var targetOptionsPath = targetRoot is null
            ? null
            : Pcl2PathNormalizer.Normalize(Path.Combine(targetRoot, "options.txt"));
        if (sourceRoot is null || targetRoot is null)
        {
            return new ValidatedOptionsPaths(
                true,
                sourceRoot,
                targetRoot,
                sourceOptionsPath,
                targetOptionsPath);
        }

        if (Pcl2PathNormalizer.AreEquivalent(sourceRoot, targetRoot))
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.SameSourceAndTarget,
                Pcl2DiagnosticSeverity.Error,
                "Source and target resolve to the same normalized game root; preview is blocked.",
                sourceRoot));
        }

        if (!TryOpenCurrentIdentity(
                source,
                "source",
                diagnostics,
                cancellationToken,
                out var sourceIdentity) ||
            !TryOpenCurrentIdentity(
                target,
                "target",
                diagnostics,
                cancellationToken,
                out var targetIdentity))
        {
            return new ValidatedOptionsPaths(
                true,
                sourceRoot,
                targetRoot,
                sourceOptionsPath,
                targetOptionsPath);
        }

        if (sourceIdentity == targetIdentity)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.SameSourceAndTarget,
                Pcl2DiagnosticSeverity.Error,
                "Source and target resolve to the same physical game root; preview is blocked.",
                sourceRoot));
        }

        return new ValidatedOptionsPaths(
            diagnostics.Any(diagnostic => diagnostic.Severity == Pcl2DiagnosticSeverity.Error),
            sourceRoot,
            targetRoot,
            sourceOptionsPath,
            targetOptionsPath,
            sourceIdentity,
            targetIdentity);
    }

    private bool TryOpenCurrentIdentity(
        Pcl2Instance instance,
        string role,
        List<Pcl2Diagnostic> diagnostics,
        CancellationToken cancellationToken,
        out PhysicalDirectoryIdentity identity)
    {
        identity = default;
        if (!Pcl2WindowsHandleGuard.TryOpenInstanceAccess(
                fileSystem,
                instance,
                cancellationToken,
                out var access,
                out var rejectedPath,
                out var reason) ||
            access is null ||
            instance.CapabilityAccess?.GameRootIdentity is not PhysicalDirectoryIdentity currentIdentity)
        {
            access?.Dispose();
            diagnostics.Add(new Pcl2Diagnostic(
                reason?.Contains("reparse", StringComparison.OrdinalIgnoreCase) == true
                    ? Pcl2DiagnosticCode.ReparsePointRejected
                    : Pcl2DiagnosticCode.GameRootInvalid,
                Pcl2DiagnosticSeverity.Error,
                reason ?? $"The {role} physical game-root identity could not be established.",
                rejectedPath ?? instance.GameRoot,
                instance.Id));
            return false;
        }

        access.Dispose();
        identity = currentIdentity;
        return true;
    }

    private static string? ValidateInstanceContract(
        Pcl2Instance instance,
        string role,
        List<Pcl2Diagnostic> diagnostics)
    {
        if (!Pcl2InstanceProof.IsValid(instance) || instance.CapabilityAccess is null)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.InstanceContractMismatch,
                Pcl2DiagnosticSeverity.Error,
                $"The {role} instance was not produced by this process's capability-bound discovery result, or its trusted fields changed.",
                instance.InstanceRoot,
                instance.Id));
            return null;
        }

        if (!Pcl2PathNormalizer.TryNormalize(instance.MinecraftRoot, out var minecraftRoot) ||
            minecraftRoot is null ||
            !Pcl2PathNormalizer.TryNormalize(instance.InstanceRoot, out var instanceRoot) ||
            instanceRoot is null ||
            instance.GameRoot is null ||
            !Pcl2PathNormalizer.TryNormalize(instance.GameRoot, out var gameRoot) ||
            gameRoot is null)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.GameRootUnresolved,
                Pcl2DiagnosticSeverity.Error,
                $"The {role} instance has no valid resolved game-root contract.",
                instance.GameRoot ?? instance.SetupPath,
                instance.Id));
            return null;
        }

        var expectedRoot = instance.Isolation switch
        {
            Pcl2IsolationMode.Isolated => instanceRoot,
            Pcl2IsolationMode.SharedMinecraftRoot => minecraftRoot,
            _ => null,
        };
        if (expectedRoot is null || !Pcl2PathNormalizer.AreEquivalent(gameRoot, expectedRoot))
        {
            diagnostics.Add(new Pcl2Diagnostic(
                expectedRoot is null
                    ? Pcl2DiagnosticCode.GameRootUnresolved
                    : Pcl2DiagnosticCode.InstanceContractMismatch,
                Pcl2DiagnosticSeverity.Error,
                expectedRoot is null
                    ? $"The {role} instance isolation is unknown, so its game root cannot be trusted."
                    : $"The {role} instance game root conflicts with its saved isolation mode.",
                gameRoot,
                instance.Id));
            return null;
        }

        var versionsRoot = Pcl2PathNormalizer.Normalize(Path.Combine(minecraftRoot, "versions"));
        var relativeInstance = Path.GetRelativePath(versionsRoot, instanceRoot);
        if (Path.IsPathRooted(relativeInstance) ||
            relativeInstance.Equals("..", StringComparison.Ordinal) ||
            relativeInstance.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativeInstance.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.InstanceContractMismatch,
                Pcl2DiagnosticSeverity.Error,
                $"The {role} instance root is outside the Minecraft versions directory.",
                instanceRoot,
                instance.Id));
            return null;
        }

        return gameRoot;
    }

    private static void AddMetadataBlocker(
        Pcl2Instance instance,
        string role,
        List<Pcl2Diagnostic> diagnostics)
    {
        if (instance.HasUsableVersionMetadata)
        {
            return;
        }

        diagnostics.Add(new Pcl2Diagnostic(
            Pcl2DiagnosticCode.InstanceMetadataUnusable,
            Pcl2DiagnosticSeverity.Error,
            $"The {role} instance has no complete, usable version metadata.",
            instance.InstanceJsonPath ?? instance.InstanceRoot,
            instance.Id));
    }

    private static Pcl2OptionsMigrationPreview Blocked(
        ValidatedOptionsPaths paths,
        List<Pcl2Diagnostic> diagnostics) =>
        new(
            true,
            paths.SourceGameRoot,
            paths.TargetGameRoot,
            paths.SourceOptionsPath,
            paths.TargetOptionsPath,
            null,
            diagnostics.AsReadOnly());

    private static bool TryValidateOptionsSchemaVersion(
        string sourceContent,
        string targetContent,
        List<Pcl2Diagnostic> diagnostics)
    {
        if (OptionsSchemaVersionPolicy.IsSafeForMigration(sourceContent, targetContent))
        {
            return true;
        }

        diagnostics.Add(new Pcl2Diagnostic(
            Pcl2DiagnosticCode.OptionsSchemaUnsupported,
            Pcl2DiagnosticSeverity.Error,
            "The source or target options.txt data version is missing, invalid, or duplicated; preview is blocked."));
        return false;
    }

    private sealed record ValidatedOptionsPaths(
        bool IsBlocked,
        string? SourceGameRoot,
        string? TargetGameRoot,
        string? SourceOptionsPath,
        string? TargetOptionsPath,
        PhysicalDirectoryIdentity? SourcePhysicalIdentity = null,
        PhysicalDirectoryIdentity? TargetPhysicalIdentity = null);
}
