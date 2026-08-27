using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Pcl2;

public sealed class Pcl2InstanceDiscovery(IFileSystemCapability fileSystem)
{
    private const int MaximumVersions = 4096;
    private const int MaximumInstanceEntries = 512;
    private const long MaximumTextBytes = 4 * 1024 * 1024;

    public Pcl2DiscoveryResult Discover(
        Pcl2DiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Candidates);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<Pcl2Diagnostic>();
        var budget = new Pcl2DiscoveryBudget(request.Limits ?? new Pcl2DiscoveryLimits());
        var rawCandidates = Pcl2RawInput.Take(
            request.Candidates,
            budget.MaximumCandidates,
            cancellationToken);
        if (rawCandidates.Failure is not null)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.CandidateEnumerationFailed,
                Pcl2DiagnosticSeverity.Error,
                $"The PCL candidate input could not be enumerated: {DiagnosticText.EscapeTechnicalValue(rawCandidates.Failure.Message)}"));
            return new Pcl2DiscoveryResult([], diagnostics.AsReadOnly());
        }

        var boundedCandidates = TakeDistinctCandidates(rawCandidates.Values);
        if (request.CandidateInputLimitReached || rawCandidates.LimitReached)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.DiscoveryLimitReached,
                Pcl2DiagnosticSeverity.Error,
                $"Only the first {budget.MaximumCandidates} raw PCL discovery candidates were observed before filtering."));
        }

        if (boundedCandidates.Count == 0)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.Pcl2NotFound,
                Pcl2DiagnosticSeverity.Error,
                "No manual or automatic PCL2 Minecraft-root candidates were supplied."));
            return new Pcl2DiscoveryResult([], diagnostics.AsReadOnly());
        }

        var resolvedCandidates = new List<Pcl2RootCandidate>();
        var resolver = new InstanceCandidateResolver(fileSystem, budget);
        foreach (var candidate in boundedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.ResolvedAccess is not null)
            {
                resolvedCandidates.Add(candidate);
                continue;
            }

            var resolution = resolver.ResolveResult(
                new DiscoveryCandidate(
                    candidate.CandidatePath,
                    candidate.Origin,
                    "PCL discovery request"),
                cancellationToken);
            resolvedCandidates.AddRange(resolution.Candidates);
            diagnostics.AddRange(resolution.Diagnostics.Select(ToPclDiagnostic));
        }

        var accumulators = new List<RootAccumulator>();
        foreach (var candidate in resolvedCandidates
                     .Where(candidate => candidate.ResolvedAccess is not null)
                     .OrderBy(candidate => candidate.Origin)
                     .ThenBy(candidate => candidate.CandidatePath, StringComparer.Ordinal))
        {
            var access = candidate.ResolvedAccess!;
            var existing = accumulators.FirstOrDefault(item =>
                item.Access.MinecraftRootIdentity == access.MinecraftRootIdentity);
            if (existing is null)
            {
                existing = new RootAccumulator(access);
                accumulators.Add(existing);
            }

            existing.Origins.Add(candidate.Origin);
        }

        var roots = new List<Pcl2MinecraftRoot>();
        foreach (var accumulator in accumulators
                     .OrderBy(item => item.Origins.Min())
                     .ThenBy(item => item.Access.MinecraftRootPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = DiscoverRoot(accumulator, budget, cancellationToken);
            if (root is null)
            {
                continue;
            }

            roots.Add(root);
            diagnostics.AddRange(root.Diagnostics);
        }

        if (roots.Count == 0)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.Pcl2NotFound,
                Pcl2DiagnosticSeverity.Error,
                "None of the supplied candidates resolved to a PCL2 Minecraft root."));
        }
        else if (roots.Count > 1)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.MultipleMinecraftRoots,
                Pcl2DiagnosticSeverity.Warning,
                $"{roots.Count} distinct Minecraft roots were found; a caller must select the intended root."));
        }

        return new Pcl2DiscoveryResult(roots.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private Pcl2MinecraftRoot? DiscoverRoot(
        RootAccumulator rootCandidate,
        Pcl2DiscoveryBudget budget,
        CancellationToken cancellationToken)
    {
        var rootDiagnostics = new List<Pcl2Diagnostic>();
        try
        {
            using var access = new Pcl2ReadPathGuard(
                fileSystem,
                rootCandidate.Access,
                cancellationToken,
                budget);
            var versionsRelative = Pcl2ReadPathGuard.Relative("versions");
            if (budget.RemainingInstances <= 0)
            {
                rootDiagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.DiscoveryLimitReached,
                    Pcl2DiagnosticSeverity.Error,
                    "The PCL discovery instance budget was exhausted before versions enumeration.",
                    access.GetMinecraftAbsolutePath(versionsRelative)));
                return new Pcl2MinecraftRoot(
                    rootCandidate.Access.MinecraftRootPath,
                    rootCandidate.Origins.OrderBy(origin => origin).ToArray(),
                    null,
                    [],
                    rootDiagnostics.AsReadOnly());
            }

            IReadOnlyList<FileSystemEntrySnapshot> versionEntries;
            try
            {
                versionEntries = access.EnumerateMinecraft(
                    versionsRelative,
                    Math.Min(MaximumVersions, budget.RemainingInstances),
                    cancellationToken);
            }
            catch (CapabilityBoundaryException exception)
            {
                rootDiagnostics.Add(new Pcl2Diagnostic(
                    exception is CapabilityLimitExceededException
                        ? Pcl2DiagnosticCode.DiscoveryLimitReached
                        : exception.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
                        ? Pcl2DiagnosticCode.ReparsePointRejected
                        : Pcl2DiagnosticCode.VersionsDirectoryUnreadable,
                    Pcl2DiagnosticSeverity.Error,
                    $"The versions directory could not be enumerated: {DiagnosticText.EscapeTechnicalValue(exception.Message)}",
                    access.GetMinecraftAbsolutePath(versionsRelative)));
                return new Pcl2MinecraftRoot(
                    rootCandidate.Access.MinecraftRootPath,
                    rootCandidate.Origins.OrderBy(origin => origin).ToArray(),
                    null,
                    [],
                    rootDiagnostics.AsReadOnly());
            }

            var versionDirectories = versionEntries
                .Where(entry => entry.IsDirectory)
                .OrderBy(entry => entry.RelativePath.Value, StringComparer.Ordinal)
                .ToArray();
            budget.ConsumeInstances(versionDirectories.Length);
            if (!HasPclEvidence(access, versionDirectories, cancellationToken))
            {
                rootDiagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.Pcl2NotFound,
                    Pcl2DiagnosticSeverity.Error,
                    "The candidate has a versions directory but no PCL.ini or bounded PCL/Setup.ini evidence.",
                    rootCandidate.Access.MinecraftRootPath));
                return null;
            }

            var selectedInstanceName = ReadSelectedInstance(
                access,
                rootDiagnostics,
                cancellationToken);
            if (versionDirectories.Length == 0)
            {
                rootDiagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.NoVersionInstances,
                    Pcl2DiagnosticSeverity.Warning,
                    "The Minecraft root contains no versions subdirectories.",
                    access.GetMinecraftAbsolutePath(versionsRelative)));
            }

            var instances = new List<Pcl2Instance>(versionDirectories.Length);
            foreach (var entry in versionDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var instanceHandle = access.OpenMinecraftDirectory(
                        entry.RelativePath,
                        cancellationToken);
                    var instance = DiscoverInstance(
                        access,
                        entry.RelativePath,
                        instanceHandle.Identity,
                        selectedInstanceName,
                        cancellationToken);
                    instances.Add(instance);
                    rootDiagnostics.AddRange(instance.Diagnostics);
                }
                catch (CapabilityBoundaryException exception)
                {
                    rootDiagnostics.Add(new Pcl2Diagnostic(
                        exception is CapabilityLimitExceededException
                            ? Pcl2DiagnosticCode.DiscoveryLimitReached
                            : Pcl2DiagnosticCode.ReparsePointRejected,
                        Pcl2DiagnosticSeverity.Error,
                        DiagnosticText.EscapeTechnicalValue(exception.Message),
                        access.GetMinecraftAbsolutePath(entry.RelativePath)));
                }
            }

            return new Pcl2MinecraftRoot(
                rootCandidate.Access.MinecraftRootPath,
                rootCandidate.Origins.OrderBy(origin => origin).ToArray(),
                selectedInstanceName,
                instances.AsReadOnly(),
                rootDiagnostics.AsReadOnly());
        }
        catch (CapabilityBoundaryException exception)
        {
            rootDiagnostics.Add(new Pcl2Diagnostic(
                exception is CapabilityLimitExceededException
                    ? Pcl2DiagnosticCode.DiscoveryLimitReached
                    : Pcl2DiagnosticCode.ReparsePointRejected,
                Pcl2DiagnosticSeverity.Error,
                DiagnosticText.EscapeTechnicalValue(exception.Message),
                rootCandidate.Access.MinecraftRootPath));
            return new Pcl2MinecraftRoot(
                rootCandidate.Access.MinecraftRootPath,
                rootCandidate.Origins.OrderBy(origin => origin).ToArray(),
                null,
                [],
                rootDiagnostics.AsReadOnly());
        }
    }

    private static Pcl2Instance DiscoverInstance(
        Pcl2ReadPathGuard access,
        NormalizedRelativePath instanceRelative,
        PhysicalDirectoryIdentity instanceIdentity,
        string? selectedInstanceName,
        CancellationToken cancellationToken)
    {
        var instanceRoot = access.GetMinecraftAbsolutePath(instanceRelative);
        var instanceName = instanceRelative.Segments[^1];
        var instanceId = CreateStableInstanceId(instanceRoot);
        var diagnostics = new List<Pcl2Diagnostic>();
        IReadOnlyList<FileSystemEntrySnapshot> instanceEntries;
        try
        {
            instanceEntries = access.EnumerateMinecraft(
                instanceRelative,
                MaximumInstanceEntries,
                cancellationToken);
        }
        catch (CapabilityBoundaryException exception)
        {
            instanceEntries = [];
            diagnostics.Add(new Pcl2Diagnostic(
                exception is CapabilityLimitExceededException
                    ? Pcl2DiagnosticCode.DiscoveryLimitReached
                    : exception.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
                    ? Pcl2DiagnosticCode.ReparsePointRejected
                    : Pcl2DiagnosticCode.InstanceJsonMissing,
                Pcl2DiagnosticSeverity.Error,
                $"The instance directory could not be enumerated: {DiagnosticText.EscapeTechnicalValue(exception.Message)}",
                instanceRoot,
                instanceId));
        }

        var instanceJsonRelative = SelectInstanceJson(
            access,
            instanceRelative,
            instanceName,
            instanceId,
            instanceEntries,
            diagnostics,
            cancellationToken);
        var setupRelative = Pcl2ReadPathGuard.Combine(instanceRelative, "PCL", "Setup.ini");
        var isolation = ReadIsolation(
            access,
            instanceRelative,
            setupRelative,
            instanceId,
            instanceEntries,
            diagnostics,
            cancellationToken);

        var gameRelative = isolation switch
        {
            Pcl2IsolationMode.Isolated => instanceRelative,
            Pcl2IsolationMode.SharedMinecraftRoot => Pcl2ReadPathGuard.Relative(string.Empty),
            _ => null,
        };
        var gameIdentity = isolation switch
        {
            Pcl2IsolationMode.Isolated => instanceIdentity,
            Pcl2IsolationMode.SharedMinecraftRoot => access.MinecraftRootIdentity,
            _ => (PhysicalDirectoryIdentity?)null,
        };
        var gameRoot = gameRelative is null
            ? null
            : access.GetMinecraftAbsolutePath(gameRelative);
        if (isolation == Pcl2IsolationMode.SharedMinecraftRoot)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.NonIsolatedInstance,
                Pcl2DiagnosticSeverity.Warning,
                "This PCL2 version is not isolated; its actual game root is the shared Minecraft root.",
                access.Access.MinecraftRootPath,
                instanceId));
        }
        else if (gameRoot is null)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.GameRootUnresolved,
                Pcl2DiagnosticSeverity.Error,
                "The actual game root cannot be resolved until the PCL2 isolation setting is explicit.",
                access.GetMinecraftAbsolutePath(setupRelative),
                instanceId));
        }

        var metadata = Pcl2MetadataDetector.Read(
            access,
            instanceRelative,
            instanceName,
            instanceJsonRelative,
            instanceId,
            diagnostics,
            cancellationToken);
        var discovered = new Pcl2Instance(
            instanceId,
            metadata.DisplayName,
            access.Access.MinecraftRootPath,
            instanceRoot,
            gameRoot,
            instanceJsonRelative is null
                ? null
                : access.GetMinecraftAbsolutePath(instanceJsonRelative),
            access.GetMinecraftAbsolutePath(setupRelative),
            isolation,
            metadata.MinecraftVersion,
            metadata.ModLoaders,
            metadata.ModpackIdentity,
            metadata.HasUsableVersionMetadata,
            string.Equals(instanceName, selectedInstanceName, StringComparison.OrdinalIgnoreCase),
            diagnostics.AsReadOnly())
        {
            CapabilityAccess = new Pcl2InstanceCapabilityAccess(
                access.Access,
                instanceRelative,
                instanceIdentity,
                gameRelative,
                gameIdentity),
        };
        return Pcl2InstanceProof.Stamp(discovered);
    }

    private static NormalizedRelativePath? SelectInstanceJson(
        Pcl2ReadPathGuard access,
        NormalizedRelativePath instanceRelative,
        string instanceName,
        string instanceId,
        IReadOnlyList<FileSystemEntrySnapshot> instanceEntries,
        List<Pcl2Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var jsonFiles = instanceEntries
            .Where(entry => !entry.IsDirectory)
            .Where(entry => entry.RelativePath.Value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !IsSupplementalMetadataJson(entry.RelativePath.Segments[^1]))
            .OrderBy(entry => entry.RelativePath.Value, StringComparer.Ordinal)
            .ToArray();
        if (jsonFiles.Length == 0)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.InstanceJsonMissing,
                Pcl2DiagnosticSeverity.Error,
                "No version JSON exists directly under this versions subdirectory.",
                access.GetMinecraftAbsolutePath(instanceRelative),
                instanceId));
            return null;
        }

        var preferredName = instanceName + ".json";
        var preferred = jsonFiles.FirstOrDefault(entry =>
            entry.RelativePath.Segments[^1].Equals(preferredName, StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            if (jsonFiles.Length > 1)
            {
                diagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.MultipleInstanceJsonFiles,
                    Pcl2DiagnosticSeverity.Warning,
                    $"Additional JSON files exist, but the matching '{preferredName}' version JSON takes precedence.",
                    access.GetMinecraftAbsolutePath(instanceRelative),
                    instanceId));
            }

            return preferred.RelativePath;
        }

        var recognizable = jsonFiles
            .Where(entry => IsStructurallyRecognizableVersionJson(
                access,
                entry.RelativePath,
                cancellationToken))
            .ToArray();
        if (recognizable.Length == 1)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.InstanceJsonFallback,
                Pcl2DiagnosticSeverity.Warning,
                $"The matching '{preferredName}' file is absent; a unique recognizable fallback was selected.",
                access.GetMinecraftAbsolutePath(recognizable[0].RelativePath),
                instanceId));
            return recognizable[0].RelativePath;
        }

        diagnostics.Add(new Pcl2Diagnostic(
            recognizable.Length > 1
                ? Pcl2DiagnosticCode.InstanceJsonAmbiguous
                : Pcl2DiagnosticCode.InstanceJsonSchemaInvalid,
            Pcl2DiagnosticSeverity.Error,
            recognizable.Length > 1
                ? $"The matching '{preferredName}' file is absent and recognizable fallback JSON files are ambiguous."
                : $"The matching '{preferredName}' file is absent and no fallback JSON has recognizable version metadata.",
            access.GetMinecraftAbsolutePath(instanceRelative),
            instanceId));
        return null;
    }

    private static bool IsStructurallyRecognizableVersionJson(
        Pcl2ReadPathGuard access,
        NormalizedRelativePath jsonRelative,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = access.ReadMinecraftFile(
                jsonRelative,
                MaximumTextBytes,
                cancellationToken);
            using var document = JsonDocument.Parse(snapshot.CopyBytes(), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   (document.RootElement.TryGetProperty("mainClass", out _) ||
                    document.RootElement.TryGetProperty("inheritsFrom", out _) ||
                    document.RootElement.TryGetProperty("patches", out _));
        }
        catch (CapabilityLimitExceededException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CapabilityBoundaryException or JsonException)
        {
            return false;
        }
    }

    private static Pcl2IsolationMode ReadIsolation(
        Pcl2ReadPathGuard access,
        NormalizedRelativePath instanceRelative,
        NormalizedRelativePath setupRelative,
        string instanceId,
        IReadOnlyList<FileSystemEntrySnapshot> instanceEntries,
        List<Pcl2Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var hasPclDirectory = instanceEntries.Any(entry =>
            entry.IsDirectory &&
            entry.RelativePath.Segments[^1].Equals("PCL", StringComparison.OrdinalIgnoreCase));
        BoundedFileSnapshot? setup = null;
        if (hasPclDirectory)
        {
            try
            {
                setup = access.ReadMinecraftFile(
                    setupRelative,
                    MaximumTextBytes,
                    cancellationToken);
            }
            catch (CapabilityBoundaryException exception)
            {
                diagnostics.Add(new Pcl2Diagnostic(
                    exception is CapabilityLimitExceededException
                        ? Pcl2DiagnosticCode.DiscoveryLimitReached
                        : Pcl2DiagnosticCode.SetupReadFailed,
                    Pcl2DiagnosticSeverity.Error,
                    $"PCL/Setup.ini could not be read: {DiagnosticText.EscapeTechnicalValue(exception.Message)}",
                    access.GetMinecraftAbsolutePath(setupRelative),
                    instanceId));
                return Pcl2IsolationMode.Unknown;
            }
        }

        if (setup is null || !setup.Exists)
        {
            var inferred = TryInferIsolationFromContent(
                access,
                instanceRelative,
                instanceId,
                instanceEntries,
                diagnostics,
                cancellationToken,
                out var evidence);
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.SetupMissing,
                inferred ? Pcl2DiagnosticSeverity.Warning : Pcl2DiagnosticSeverity.Error,
                inferred
                    ? "PCL/Setup.ini is missing; isolation was inferred from non-empty instance content."
                    : "PCL/Setup.ini is missing, so the actual game root cannot be assumed.",
                access.GetMinecraftAbsolutePath(setupRelative),
                instanceId));
            if (inferred)
            {
                AddIsolationInferenceDiagnostic(
                    access.GetMinecraftAbsolutePath(instanceRelative),
                    instanceId,
                    evidence,
                    diagnostics);
                return Pcl2IsolationMode.Isolated;
            }

            return Pcl2IsolationMode.Unknown;
        }

        var settings = ReadColonSettingValues(DecodeText(setup));
        if (settings.TryGetValue("VersionArgumentIndieV2", out var currentValues))
        {
            var parsed = new List<Pcl2IsolationMode>();
            foreach (var value in currentValues)
            {
                if (!TryReadCurrentIsolation(value, out var isolation))
                {
                    diagnostics.Add(new Pcl2Diagnostic(
                        Pcl2DiagnosticCode.IsolationSettingUnknown,
                        Pcl2DiagnosticSeverity.Error,
                        $"VersionArgumentIndieV2 has an unknown value: '{DiagnosticText.EscapeTechnicalValue(value)}'.",
                        access.GetMinecraftAbsolutePath(setupRelative),
                        instanceId));
                    return Pcl2IsolationMode.Unknown;
                }

                parsed.Add(isolation);
            }

            var distinct = parsed.Distinct().ToArray();
            if (distinct.Length != 1)
            {
                diagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.IsolationSettingConflict,
                    Pcl2DiagnosticSeverity.Error,
                    "Setup.ini contains conflicting VersionArgumentIndieV2 values.",
                    access.GetMinecraftAbsolutePath(setupRelative),
                    instanceId));
                return Pcl2IsolationMode.Unknown;
            }

            return distinct[0];
        }

        if (settings.TryGetValue("VersionArgumentIndie", out var legacyValues))
        {
            var distinct = legacyValues.Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length == 1 && distinct[0] == "1")
            {
                return Pcl2IsolationMode.Isolated;
            }

            if (distinct.Length == 1 && distinct[0] == "2")
            {
                return Pcl2IsolationMode.SharedMinecraftRoot;
            }

            if (distinct.Length == 1 && distinct[0] is "-1" or "0" &&
                TryInferIsolationFromContent(
                    access,
                    instanceRelative,
                    instanceId,
                    instanceEntries,
                    diagnostics,
                    cancellationToken,
                    out var evidence))
            {
                diagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.IsolationSettingUnknown,
                    Pcl2DiagnosticSeverity.Warning,
                    $"Legacy VersionArgumentIndie '{DiagnosticText.EscapeTechnicalValue(distinct[0])}' does not decide isolation without PCL's global setting.",
                    access.GetMinecraftAbsolutePath(setupRelative),
                    instanceId));
                AddIsolationInferenceDiagnostic(
                    access.GetMinecraftAbsolutePath(instanceRelative),
                    instanceId,
                    evidence,
                    diagnostics);
                return Pcl2IsolationMode.Isolated;
            }

            diagnostics.Add(new Pcl2Diagnostic(
                distinct.Length != 1
                    ? Pcl2DiagnosticCode.IsolationSettingConflict
                    : Pcl2DiagnosticCode.IsolationSettingUnknown,
                Pcl2DiagnosticSeverity.Error,
                "Legacy VersionArgumentIndie is conflicting, deferred without evidence, or unknown.",
                access.GetMinecraftAbsolutePath(setupRelative),
                instanceId));
            return Pcl2IsolationMode.Unknown;
        }

        diagnostics.Add(new Pcl2Diagnostic(
            Pcl2DiagnosticCode.IsolationSettingMissing,
            Pcl2DiagnosticSeverity.Error,
            "Setup.ini contains neither VersionArgumentIndieV2 nor legacy VersionArgumentIndie.",
            access.GetMinecraftAbsolutePath(setupRelative),
            instanceId));
        return Pcl2IsolationMode.Unknown;
    }

    private static bool TryInferIsolationFromContent(
        Pcl2ReadPathGuard access,
        NormalizedRelativePath instanceRelative,
        string instanceId,
        IReadOnlyList<FileSystemEntrySnapshot> instanceEntries,
        List<Pcl2Diagnostic> diagnostics,
        CancellationToken cancellationToken,
        out string evidence)
    {
        evidence = string.Empty;
        foreach (var directoryName in new[] { "mods", "saves" })
        {
            if (!instanceEntries.Any(entry =>
                    entry.IsDirectory &&
                    entry.RelativePath.Segments[^1].Equals(directoryName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var directory = Pcl2ReadPathGuard.Combine(instanceRelative, directoryName);
            try
            {
                if (access.EnumerateMinecraft(directory, 1, cancellationToken).Count > 0)
                {
                    evidence = directoryName == "mods"
                        ? "the instance-local mods directory contains at least one entry"
                        : "the instance-local saves directory contains at least one entry";
                    return true;
                }
            }
            catch (CapabilityBoundaryException exception)
            {
                diagnostics.Add(new Pcl2Diagnostic(
                    exception is CapabilityLimitExceededException
                        ? Pcl2DiagnosticCode.DiscoveryLimitReached
                        : Pcl2DiagnosticCode.IsolationEvidenceUnreadable,
                    Pcl2DiagnosticSeverity.Error,
                    $"Instance-local {DiagnosticText.EscapeTechnicalValue(directoryName)} evidence could not be read: {DiagnosticText.EscapeTechnicalValue(exception.Message)}",
                    access.GetMinecraftAbsolutePath(directory),
                    instanceId));
                return false;
            }
        }

        return false;
    }

    private static bool HasPclEvidence(
        Pcl2ReadPathGuard access,
        IReadOnlyList<FileSystemEntrySnapshot> versionDirectories,
        CancellationToken cancellationToken)
    {
        try
        {
            if (access.ReadMinecraftFile(
                    Pcl2ReadPathGuard.Relative("PCL.ini"),
                    MaximumTextBytes,
                    cancellationToken).Exists)
            {
                return true;
            }
        }
        catch (CapabilityLimitExceededException)
        {
            throw;
        }
        catch (CapabilityBoundaryException)
        {
            return false;
        }

        foreach (var version in versionDirectories)
        {
            try
            {
                var instanceEntries = access.EnumerateMinecraft(
                    version.RelativePath,
                    MaximumInstanceEntries,
                    cancellationToken);
                if (!instanceEntries.Any(entry =>
                        entry.IsDirectory &&
                        entry.RelativePath.Segments[^1].Equals("PCL", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (access.ReadMinecraftFile(
                        Pcl2ReadPathGuard.Combine(version.RelativePath, "PCL", "Setup.ini"),
                        MaximumTextBytes,
                        cancellationToken).Exists)
                {
                    return true;
                }
            }
            catch (CapabilityLimitExceededException)
            {
                throw;
            }
            catch (CapabilityBoundaryException)
            {
                continue;
            }
        }

        return false;
    }

    private static string? ReadSelectedInstance(
        Pcl2ReadPathGuard access,
        List<Pcl2Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var path = Pcl2ReadPathGuard.Relative("PCL.ini");
        try
        {
            var snapshot = access.ReadMinecraftFile(path, MaximumTextBytes, cancellationToken);
            if (!snapshot.Exists)
            {
                return null;
            }

            var settings = ReadColonSettings(DecodeText(snapshot));
            return settings.TryGetValue("Version", out var selected) &&
                   !string.IsNullOrWhiteSpace(selected)
                ? selected.Trim()
                : null;
        }
        catch (CapabilityBoundaryException exception)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                exception is CapabilityLimitExceededException
                    ? Pcl2DiagnosticCode.DiscoveryLimitReached
                    : Pcl2DiagnosticCode.PclIniReadFailed,
                Pcl2DiagnosticSeverity.Warning,
                $"PCL.ini could not be read: {DiagnosticText.EscapeTechnicalValue(exception.Message)}",
                access.GetMinecraftAbsolutePath(path)));
            return null;
        }
    }

    private static Dictionary<string, string> ReadColonSettings(string content)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                settings[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return settings;
    }

    private static Dictionary<string, List<string>> ReadColonSettingValues(string content)
    {
        var settings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!settings.TryGetValue(key, out var values))
            {
                values = [];
                settings.Add(key, values);
            }

            values.Add(line[(separator + 1)..].Trim());
        }

        return settings;
    }

    private static bool TryReadCurrentIsolation(
        string value,
        out Pcl2IsolationMode isolation)
    {
        var normalized = value.Trim();
        if (normalized.Equals("true", StringComparison.OrdinalIgnoreCase) || normalized == "1")
        {
            isolation = Pcl2IsolationMode.Isolated;
            return true;
        }

        if (normalized.Equals("false", StringComparison.OrdinalIgnoreCase) || normalized == "0")
        {
            isolation = Pcl2IsolationMode.SharedMinecraftRoot;
            return true;
        }

        isolation = Pcl2IsolationMode.Unknown;
        return false;
    }

    private static void AddIsolationInferenceDiagnostic(
        string instanceRoot,
        string instanceId,
        string evidence,
        List<Pcl2Diagnostic> diagnostics) =>
        diagnostics.Add(new Pcl2Diagnostic(
            Pcl2DiagnosticCode.IsolationInferredFromContent,
            Pcl2DiagnosticSeverity.Warning,
            $"Isolation was inferred because {evidence}; BlockFerry did not initialize or modify PCL settings.",
            instanceRoot,
            instanceId));

    private static bool IsSupplementalMetadataJson(string fileName) =>
        fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("minecraftinstance.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("profile.json", StringComparison.OrdinalIgnoreCase);

    private static string DecodeText(BoundedFileSnapshot snapshot) =>
        Encoding.UTF8.GetString(snapshot.CopyBytes());

    private static string CreateStableInstanceId(string instanceRoot)
    {
        var normalizedKey = Pcl2PathNormalizer.Normalize(instanceRoot).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey));
        return "pcl2-" + Convert.ToHexString(hash.AsSpan(0, 12));
    }

    private static Pcl2Diagnostic ToPclDiagnostic(DiscoveryDiagnostic diagnostic) =>
        new(
            diagnostic.Code == DiscoveryDiagnosticCode.DiscoveryLimitReached
                ? Pcl2DiagnosticCode.DiscoveryLimitReached
                : diagnostic.Code == DiscoveryDiagnosticCode.CandidateEnumerationFailed
                    ? Pcl2DiagnosticCode.CandidateEnumerationFailed
                : diagnostic.Code == DiscoveryDiagnosticCode.CandidatePathInvalid
                ? Pcl2DiagnosticCode.CandidatePathInvalid
                : diagnostic.Code == DiscoveryDiagnosticCode.CandidateNotRecognized
                    ? Pcl2DiagnosticCode.MinecraftRootInvalid
                    : Pcl2DiagnosticCode.ReparsePointRejected,
            Pcl2DiagnosticSeverity.Error,
            diagnostic.Message,
            diagnostic.Path);

    private static List<Pcl2RootCandidate> TakeDistinctCandidates(
        IEnumerable<Pcl2RootCandidate> candidates)
    {
        var selected = new List<Pcl2RootCandidate>();
        var exactInputs = new HashSet<(string CandidatePath, Pcl2CandidateOrigin Origin)>();
        foreach (var candidate in candidates)
        {
            if (candidate is null ||
                !exactInputs.Add((candidate.CandidatePath, candidate.Origin)))
            {
                continue;
            }

            selected.Add(candidate);
        }

        return selected
            .OrderBy(candidate => candidate.Origin)
            .ThenBy(candidate => candidate.CandidatePath, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class RootAccumulator(Pcl2ResolvedRootAccess access)
    {
        public Pcl2ResolvedRootAccess Access { get; } = access;
        public HashSet<Pcl2CandidateOrigin> Origins { get; } = [];
    }
}
