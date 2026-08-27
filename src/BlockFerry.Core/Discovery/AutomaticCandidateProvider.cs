using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Discovery;

public sealed class AutomaticCandidateProvider(
    IEnvironmentPaths environment,
    IFileSystemCapability fileSystem,
    IShortcutTargetResolver shortcuts)
{
    private const int HardMaximumShortcutFiles = 256;
    private const int HardMaximumCandidates = 64;
    private const int HardMaximumRememberedRoots = 64;
    private const int HardMaximumShellRoots = 32;
    private const int HardMaximumRootOpenAttempts = 128;
    private const int MaximumShellDirectories = 256;
    private const int MaximumEntriesPerDirectory = 1024;
    private const long MaximumShortcutBytes = 1024 * 1024;

    public IReadOnlyList<DiscoveryCandidate> GetCandidates(
        AutomaticCandidateRequest request,
        CancellationToken cancellationToken = default) =>
        GetCandidateResult(request, cancellationToken).Candidates;

    public AutomaticCandidateResult GetCandidateResult(
        AutomaticCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RememberedRoots);
        cancellationToken.ThrowIfCancellationRequested();

        var currentDiagnostics = new List<DiscoveryDiagnostic>();
        var observations = new List<DiscoveryCandidate>();
        var attemptedCandidatePaths = new HashSet<string>(StringComparer.Ordinal);
        var openBudget = new RootOpenBudget(HardMaximumRootOpenAttempts);
        var maximumShortcutFiles = Math.Clamp(
            request.MaximumShortcutFiles,
            0,
            HardMaximumShortcutFiles);
        var maximumCandidates = Math.Clamp(
            request.MaximumCandidates,
            0,
            HardMaximumCandidates);

        var rememberedPrefix = TakeRawPrefix(
            request.RememberedRoots,
            HardMaximumRememberedRoots,
            cancellationToken);
        var rememberedRoots = rememberedPrefix.Failure is null
            ? TakeDistinctInputs(rememberedPrefix.Values)
            : [];
        if (rememberedPrefix.Failure is not null)
        {
            currentDiagnostics.Add(new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidateEnumerationFailed,
                $"The remembered-root input could not be enumerated: {DiagnosticText.EscapeTechnicalValue(rememberedPrefix.Failure.Message)}"));
        }

        if (rememberedPrefix.LimitReached)
        {
            AddLimitDiagnostic(
                currentDiagnostics,
                $"Only the first {HardMaximumRememberedRoots} raw remembered-root observations were considered.");
        }

        foreach (var rememberedRoot in rememberedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryAddVerifiedCandidate(
                rememberedRoot,
                Pcl2CandidateOrigin.Manual,
                "remembered approved root",
                observations,
                currentDiagnostics,
                attemptedCandidatePaths,
                openBudget,
                maximumCandidates,
                cancellationToken);
        }

        AddEnvironmentMinecraftCandidate(
            environment.RoamingAppData,
            "injected roaming application-data root",
            observations,
            currentDiagnostics,
            attemptedCandidatePaths,
            openBudget,
            maximumCandidates,
            cancellationToken);
        AddEnvironmentMinecraftCandidate(
            environment.UserProfile,
            "injected user-profile root",
            observations,
            currentDiagnostics,
            attemptedCandidatePaths,
            openBudget,
            maximumCandidates,
            cancellationToken);

        var shellPrefix = TakeRawPrefix(
            EnumerateShellRootInputs(),
            HardMaximumShellRoots,
            cancellationToken);
        var shellRoots = shellPrefix.Failure is null
            ? TakeDistinctInputs(shellPrefix.Values)
            : [];
        if (shellPrefix.Failure is not null)
        {
            currentDiagnostics.Add(new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidateEnumerationFailed,
                $"The injected shell-root input could not be enumerated: {DiagnosticText.EscapeTechnicalValue(shellPrefix.Failure.Message)}"));
        }

        if (shellPrefix.LimitReached)
        {
            AddLimitDiagnostic(
                currentDiagnostics,
                $"Only the first {HardMaximumShellRoots} raw injected shell-root observations were considered.");
        }

        var parsedShortcutFiles = 0;
        foreach (var shellRoot in shellRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parsedShortcutFiles >= maximumShortcutFiles)
            {
                break;
            }

            ScanShellRoot(
                shellRoot!,
                maximumShortcutFiles,
                ref parsedShortcutFiles,
                observations,
                currentDiagnostics,
                attemptedCandidatePaths,
                openBudget,
                maximumCandidates,
                cancellationToken);
        }

        var merged = MergeOnlyEqualStrongIdentities(observations);
        if (merged.Count > maximumCandidates)
        {
            currentDiagnostics.Add(new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidateLimitReached,
                $"Discovery retained only the first {maximumCandidates} deterministically ordered candidates."));
            merged = merged.Take(maximumCandidates).ToList();
        }

        return new AutomaticCandidateResult(
            merged.AsReadOnly(),
            currentDiagnostics.AsReadOnly());
    }

    private void AddEnvironmentMinecraftCandidate(
        string? environmentRoot,
        string evidence,
        List<DiscoveryCandidate> observations,
        List<DiscoveryDiagnostic> currentDiagnostics,
        HashSet<string> attemptedCandidatePaths,
        RootOpenBudget openBudget,
        int maximumCandidates,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environmentRoot))
        {
            return;
        }

        string candidate;
        try
        {
            candidate = Path.Combine(environmentRoot, ".minecraft");
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            currentDiagnostics.Add(new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidatePathInvalid,
                $"The injected environment root could not form a .minecraft candidate: {DiagnosticText.EscapeTechnicalValue(exception.Message)}",
                environmentRoot));
            return;
        }

        TryAddVerifiedCandidate(
            candidate,
            Pcl2CandidateOrigin.Automatic,
            evidence,
            observations,
            currentDiagnostics,
            attemptedCandidatePaths,
            openBudget,
            maximumCandidates,
            cancellationToken);
    }

    private void ScanShellRoot(
        string shellRoot,
        int maximumShortcutFiles,
        ref int parsedShortcutFiles,
        List<DiscoveryCandidate> observations,
        List<DiscoveryDiagnostic> currentDiagnostics,
        HashSet<string> attemptedCandidatePaths,
        RootOpenBudget openBudget,
        int maximumCandidates,
        CancellationToken cancellationToken)
    {
        if (!DiscoveryPathPolicy.TryNormalizeLocalAbsolute(
                shellRoot,
                out var normalizedShellRoot,
                out var pathDiagnostic))
        {
            currentDiagnostics.Add(pathDiagnostic);
            return;
        }

        IVerifiedDirectoryHandle? shellHandle = null;
        try
        {
            if (!openBudget.TryReserve())
            {
                AddLimitDiagnostic(
                    currentDiagnostics,
                    $"Automatic discovery stopped at {HardMaximumRootOpenAttempts} total root-open attempts.");
                return;
            }

            shellHandle = fileSystem.OpenRoot(
                normalizedShellRoot,
                FileSystemOpenPurpose.Discovery,
                cancellationToken);
            if (!shellHandle.IsLocalVolume || shellHandle.IsNetworkRedirected)
            {
                currentDiagnostics.Add(new DiscoveryDiagnostic(
                    DiscoveryDiagnosticCode.NetworkOrDeviceTargetRejected,
                    "The injected shell root is not a verified local, non-redirected directory.",
                    normalizedShellRoot));
                return;
            }

            var rootPath = MustRelative(string.Empty);
            var directories = new Queue<NormalizedRelativePath>();
            directories.Enqueue(rootPath);
            var visitedDirectories = 0;
            while (directories.Count > 0 &&
                   visitedDirectories < MaximumShellDirectories &&
                   parsedShortcutFiles < maximumShortcutFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = directories.Dequeue();
                visitedDirectories++;
                IReadOnlyList<FileSystemEntrySnapshot> entries;
                try
                {
                    entries = fileSystem.EnumerateEntries(
                        shellHandle,
                        directory,
                        new EnumerationLimits(MaximumEntriesPerDirectory),
                        cancellationToken);
                }
                catch (CapabilityLimitExceededException exception)
                {
                    currentDiagnostics.Add(new DiscoveryDiagnostic(
                        DiscoveryDiagnosticCode.ShortcutEnumerationLimitReached,
                        DiagnosticText.EscapeTechnicalValue(exception.Message),
                        normalizedShellRoot));
                    continue;
                }
                catch (CapabilityBoundaryException exception)
                {
                    currentDiagnostics.Add(new DiscoveryDiagnostic(
                        DiscoveryDiagnosticCode.CandidateOutsideCapability,
                        DiagnosticText.EscapeTechnicalValue(exception.Message),
                        normalizedShellRoot));
                    continue;
                }

                foreach (var entry in entries.OrderBy(
                             item => item.RelativePath.Value,
                             StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                    {
                        if (directories.Count + visitedDirectories < MaximumShellDirectories)
                        {
                            directories.Enqueue(entry.RelativePath);
                        }

                        continue;
                    }

                    if (!entry.RelativePath.Value.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (parsedShortcutFiles >= maximumShortcutFiles)
                    {
                        break;
                    }

                    parsedShortcutFiles++;
                    BoundedFileSnapshot snapshot;
                    try
                    {
                        snapshot = fileSystem.ReadFile(
                            shellHandle,
                            entry.RelativePath,
                            new FileReadLimits(MaximumShortcutBytes),
                            cancellationToken);
                    }
                    catch (CapabilityLimitExceededException exception)
                    {
                        currentDiagnostics.Add(new DiscoveryDiagnostic(
                            DiscoveryDiagnosticCode.ShortcutTooLarge,
                            DiagnosticText.EscapeTechnicalValue(exception.Message),
                            entry.RelativePath.Value));
                        continue;
                    }
                    catch (CapabilityBoundaryException exception)
                    {
                        currentDiagnostics.Add(new DiscoveryDiagnostic(
                            DiscoveryDiagnosticCode.ShortcutMalformed,
                            DiagnosticText.EscapeTechnicalValue(exception.Message),
                            entry.RelativePath.Value));
                        continue;
                    }

                    var resolution = shortcuts.Parse(snapshot);
                    if (!resolution.IsResolved)
                    {
                        var diagnostic = resolution.Diagnostic ?? new DiscoveryDiagnostic(
                            DiscoveryDiagnosticCode.ShortcutMalformed,
                            "The shortcut parser returned no target.");
                        currentDiagnostics.Add(diagnostic with { Path = entry.RelativePath.Value });
                        continue;
                    }

                    var target = resolution.TargetPath!;
                    if (!DiscoveryPathPolicy.TryNormalizeLocalAbsolute(
                            target,
                            out var normalizedTarget,
                            out var targetDiagnostic))
                    {
                        currentDiagnostics.Add(targetDiagnostic with { Path = entry.RelativePath.Value });
                        continue;
                    }

                    if (resolution.TargetKind == ShortcutTargetKind.File)
                    {
                        normalizedTarget = Path.GetDirectoryName(normalizedTarget) ?? string.Empty;
                    }
                    else if (resolution.TargetKind != ShortcutTargetKind.Directory)
                    {
                        currentDiagnostics.Add(new DiscoveryDiagnostic(
                            DiscoveryDiagnosticCode.ShortcutTargetKindUnknown,
                            "The shortcut target kind was not positively proven by its bounded header.",
                            entry.RelativePath.Value));
                        continue;
                    }

                    TryAddVerifiedCandidate(
                        normalizedTarget,
                        Pcl2CandidateOrigin.Automatic,
                        $"shortcut {entry.RelativePath.Value}",
                        observations,
                        currentDiagnostics,
                        attemptedCandidatePaths,
                        openBudget,
                        maximumCandidates,
                        cancellationToken);
                }
            }
        }
        catch (CapabilityBoundaryException exception)
        {
            currentDiagnostics.Add(new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidateOutsideCapability,
                DiagnosticText.EscapeTechnicalValue(exception.Message),
                normalizedShellRoot));
        }
        finally
        {
            shellHandle?.Dispose();
        }
    }

    private bool TryAddVerifiedCandidate(
        string? candidatePath,
        Pcl2CandidateOrigin origin,
        string evidence,
        List<DiscoveryCandidate> observations,
        List<DiscoveryDiagnostic> currentDiagnostics,
        HashSet<string> attemptedCandidatePaths,
        RootOpenBudget openBudget,
        int maximumCandidates,
        CancellationToken cancellationToken)
    {
        if (!DiscoveryPathPolicy.TryNormalizeLocalAbsolute(
                candidatePath,
                out var normalized,
                out var diagnostic))
        {
            currentDiagnostics.Add(diagnostic);
            return false;
        }

        if (!attemptedCandidatePaths.Add(normalized))
        {
            return true;
        }

        if (observations.Count >= maximumCandidates)
        {
            AddLimitDiagnostic(
                currentDiagnostics,
                $"Discovery reached the requested {maximumCandidates} candidate-attempt limit before another capability open.");
            return false;
        }

        if (!openBudget.TryReserve())
        {
            AddLimitDiagnostic(
                currentDiagnostics,
                $"Automatic discovery stopped at {HardMaximumRootOpenAttempts} total root-open attempts.");
            return false;
        }

        try
        {
            using var handle = fileSystem.OpenRoot(
                normalized,
                FileSystemOpenPurpose.Discovery,
                cancellationToken);
            if (!handle.IsLocalVolume || handle.IsNetworkRedirected)
            {
                currentDiagnostics.Add(new DiscoveryDiagnostic(
                    DiscoveryDiagnosticCode.NetworkOrDeviceTargetRejected,
                    "The candidate is not a verified local, non-redirected directory.",
                    normalized));
                return false;
            }

            observations.Add(new DiscoveryCandidate(normalized, origin, evidence)
            {
                Identity = handle.Identity,
            });
            return true;
        }
        catch (CapabilityBoundaryException exception)
        {
            currentDiagnostics.Add(new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidateOutsideCapability,
                DiagnosticText.EscapeTechnicalValue(exception.Message),
                normalized));
            return false;
        }
    }

    private static List<DiscoveryCandidate> MergeOnlyEqualStrongIdentities(
        IEnumerable<DiscoveryCandidate> observations)
    {
        var ordered = observations
            .OrderBy(candidate => candidate.Origin)
            .ThenBy(candidate => candidate.CandidatePath, StringComparer.Ordinal)
            .ToArray();
        var result = new List<DiscoveryCandidate>(ordered.Length);
        foreach (var candidate in ordered)
        {
            if (candidate.Identity is not PhysicalDirectoryIdentity identity)
            {
                result.Add(candidate);
                continue;
            }

            var existingIndex = result.FindIndex(existing =>
                existing.Identity is PhysicalDirectoryIdentity existingIdentity &&
                existingIdentity == identity);
            if (existingIndex < 0)
            {
                result.Add(candidate);
                continue;
            }

            var existing = result[existingIndex];
            if (!existing.Evidence.Contains(candidate.Evidence, StringComparison.Ordinal))
            {
                result[existingIndex] = existing with
                {
                    Evidence = existing.Evidence + "; " + candidate.Evidence,
                };
            }
        }

        return result;
    }

    private static NormalizedRelativePath MustRelative(string value)
    {
        if (!NormalizedRelativePath.TryCreate(value, out var path, out var rejection) || path is null)
        {
            throw new InvalidOperationException(rejection ?? "A fixed provider-relative path was rejected.");
        }

        return path;
    }

    private IEnumerable<string?> EnumerateShellRootInputs()
    {
        yield return environment.UserDesktop;
        yield return environment.PublicDesktop;
        if (environment.StartMenuRoots is null)
        {
            yield break;
        }

        foreach (var root in environment.StartMenuRoots)
        {
            yield return root;
        }
    }

    private static RawInputPrefix<T> TakeRawPrefix<T>(
        IEnumerable<T> inputs,
        int maximumInputs,
        CancellationToken cancellationToken)
    {
        var values = new List<T>(maximumInputs);
        IEnumerator<T>? enumerator = null;
        Exception? failure = null;
        var limitReached = false;
        try
        {
            enumerator = inputs.GetEnumerator();
            while (values.Count < maximumInputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext())
                {
                    break;
                }

                values.Add(enumerator.Current);
            }

            if (values.Count == maximumInputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                limitReached = enumerator.MoveNext();
            }
        }
        catch (Exception exception) when (IsRecoverableEnumerationFailure(exception, cancellationToken))
        {
            failure = exception;
        }
        finally
        {
            if (enumerator is not null)
            {
                try
                {
                    enumerator.Dispose();
                }
                catch (Exception exception) when (IsRecoverableEnumerationFailure(exception, cancellationToken))
                {
                    failure ??= exception;
                }
            }
        }

        return new RawInputPrefix<T>(
            values.AsReadOnly(),
            limitReached,
            failure);
    }

    private static bool IsRecoverableEnumerationFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is not OutOfMemoryException &&
        !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static List<string> TakeDistinctInputs(IEnumerable<string?> inputs)
    {
        var selected = new List<string>();
        var exactInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input) || !exactInputs.Add(input))
            {
                continue;
            }

            selected.Add(input);
        }

        selected.Sort(StringComparer.Ordinal);
        return selected;
    }

    private sealed record RawInputPrefix<T>(
        IReadOnlyList<T> Values,
        bool LimitReached,
        Exception? Failure);

    private static void AddLimitDiagnostic(
        List<DiscoveryDiagnostic> currentDiagnostics,
        string message)
    {
        if (currentDiagnostics.Any(diagnostic =>
                diagnostic.Code == DiscoveryDiagnosticCode.CandidateLimitReached &&
                diagnostic.Message.Equals(message, StringComparison.Ordinal)))
        {
            return;
        }

        currentDiagnostics.Add(new DiscoveryDiagnostic(
            DiscoveryDiagnosticCode.CandidateLimitReached,
            message));
    }

    private sealed class RootOpenBudget(int remaining)
    {
        public bool TryReserve()
        {
            if (remaining <= 0)
            {
                return false;
            }

            remaining--;
            return true;
        }
    }
}
