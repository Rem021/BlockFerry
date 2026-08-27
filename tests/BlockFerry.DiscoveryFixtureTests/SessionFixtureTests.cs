using System.Reflection;
using System.Text;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;
using BlockFerry.TestSupport;

internal static class SessionFixtureTests
{
    private static readonly List<string> AuditRoots = [];
    private static readonly List<CapabilityAuditEvent> AuditEvents = [];

    public static void Run()
    {
        AuditRoots.Clear();
        AuditEvents.Clear();
        ValidPair();
        ForgedProofRejected();
        GenerationMismatchRejected();
        SamePhysicalRootRejected();
        DirectoryIdentityDriftIsStale();
        OpaqueTagStableForRepeatedCallsWithinOneSession();
        SameNumericGenerationDifferentSessionRotatesOpaqueTag();
        NewGenerationRotatesOpaqueTag();
        DisposedSessionZeroesKeyAndRejectsUse();
        SharedAndUnknownIsolationRejected();
        NonLocalAndUnknownVolumeRejected();
        TokenReplayAndCrossSessionEvidenceRejected();
        CancellationIsHonored();
        ConcurrentSessionsRemainIndependent();

        var audit = CapabilityAuditSummary.From(AuditEvents);
        Assert(audit.EventCount > 0, "Session tests must produce capability audit evidence.");
        Assert(audit.WriteCount == 0, "Session tests must perform zero capability writes.");
        Assert(audit.RealRootAccessCount == 0, "Session tests must never access a real root.");
        Console.WriteLine(
            "AUDIT: fixture-roots=" +
            string.Join('|', AuditRoots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)));
        Console.WriteLine(
            $"AUDIT: events={audit.EventCount}; writes={audit.WriteCount}; " +
            $"real-root-access={audit.RealRootAccessCount}");
        Console.WriteLine("PASS: session");
    }

    private static void ValidPair()
    {
        using var fixture = CreateDiscoveryFixture();
        var factory = new DiscoverySessionFactory();
        using var session = factory.Create(1, fixture.Discovery);

        Assert(session.IsActive, "A newly created discovery session must be active.");
        Assert(session.Instances.Count == 2, "The session must retain both isolated instances.");
        Assert(
            session.TryGetPair(
                fixture.Source.Id,
                fixture.Target.Id,
                out var pair),
            "A valid source and target must produce a discovery pair.");
        Assert(pair.Generation == 1, "The pair must be bound to the session generation.");
        Assert(
            pair.Source.GameRoot.Identity != pair.Target.GameRoot.Identity,
            "A valid pair must carry distinct physical game-root identities.");
        Assert(
            pair.Source.ProofToken.Length == 43 &&
            pair.Source.ProofToken.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'),
            "A choice proof must be a full unpadded base64url HMAC-SHA256 value.");

        var validation = factory.Revalidate(
            session,
            fixture.Source.Id,
            fixture.Target.Id);
        AssertValidationAccepted(validation, "A valid retained pair must revalidate.");

        var mutableView = (IList<DiscoveredInstanceChoice>)session.Instances;
        AssertThrows<NotSupportedException>(
            () => mutableView.Add(pair.Source),
            "The public choice collection must be read-only.");
    }

    private static void ForgedProofRejected()
    {
        using var fixture = CreateDiscoveryFixture();
        var forged = fixture.Source with
        {
            Id = "pcl2-000000000000000000000000",
        };
        var discovery = ReplaceInstances(fixture.Discovery, forged, fixture.Target);
        var factory = new DiscoverySessionFactory();
        using var session = factory.Create(2, discovery);

        var validation = factory.Revalidate(session, forged.Id, fixture.Target.Id);
        AssertValidationRejected(
            validation,
            Pcl2DiagnosticCode.DiscoveryProofInvalid,
            isStale: false,
            "A copied instance with a forged current-process proof must be rejected.");
    }

    private static void GenerationMismatchRejected()
    {
        using var fixture = CreateDiscoveryFixture();
        using var session = new DiscoverySessionFactory().Create(3, fixture.Discovery);
        Assert(
            session.TryGetPair(fixture.Source.Id, fixture.Target.Id, out var pair),
            "The generation fixture requires a valid pair.");

        var mismatched = session.ValidateEvidence(pair with { Generation = 4 });
        AssertValidationRejected(
            mismatched,
            Pcl2DiagnosticCode.DiscoveryGenerationMismatch,
            isStale: false,
            "Public evidence from another generation must be rejected.");
    }

    private static void SamePhysicalRootRejected()
    {
        using var fixture = CreateDiscoveryFixture();
        var alias = Pcl2InstanceProof.Stamp(fixture.Source with
        {
            Id = "pcl2-111111111111111111111111",
            DisplayName = "Physical alias",
            MinecraftRoot = fixture.Source.MinecraftRoot.ToUpperInvariant(),
            InstanceRoot = fixture.Source.InstanceRoot.ToUpperInvariant(),
            GameRoot = fixture.Source.GameRoot!.ToUpperInvariant(),
            IsSelected = false,
        });
        var discovery = ReplaceInstances(fixture.Discovery, fixture.Source, alias);
        var factory = new DiscoverySessionFactory();
        using var session = factory.Create(5, discovery);

        var validation = factory.Revalidate(session, fixture.Source.Id, alias.Id);
        AssertValidationRejected(
            validation,
            Pcl2DiagnosticCode.SameSourceAndTarget,
            isStale: false,
            "Different strings and instance IDs must not bypass same-physical-root rejection.");
    }

    private static void DirectoryIdentityDriftIsStale()
    {
        using var fixture = CreateDiscoveryFixture();
        var factory = new DiscoverySessionFactory();
        using var session = factory.Create(6, fixture.Discovery);

        var movedPath = fixture.Sandbox.AllocateGuidPath();
        Directory.Move(fixture.Source.InstanceRoot, movedPath);
        Directory.CreateDirectory(fixture.Source.InstanceRoot);

        var validation = factory.Revalidate(
            session,
            fixture.Source.Id,
            fixture.Target.Id);
        AssertValidationRejected(
            validation,
            Pcl2DiagnosticCode.DiscoveryRootStale,
            isStale: true,
            "Replacing a selected directory at the same pathname must make the pair stale.");
    }

    private static void OpaqueTagStableForRepeatedCallsWithinOneSession()
    {
        using var fixture = CreateDiscoveryFixture();
        using var session = new DiscoverySessionFactory().Create(7, fixture.Discovery);
        var payload = Encoding.UTF8.GetBytes("world\0fixture");

        var first = session.CreateGenerationOpaqueTag(
            "blockferry.content.jei.scope.v1",
            payload);
        var second = session.CreateGenerationOpaqueTag(
            "blockferry.content.jei.scope.v1",
            payload);
        var otherDomain = session.CreateGenerationOpaqueTag(
            "blockferry.content.esm.scope.v1",
            payload);

        Assert(first == second, "Repeated opaque tags in one session must be stable.");
        Assert(first != otherDomain, "Opaque-tag domains must be separated ordinally.");
        Assert(first.Length == 43, "Opaque tags must retain the full HMAC-SHA256 digest.");
    }

    private static void SameNumericGenerationDifferentSessionRotatesOpaqueTag()
    {
        using var fixture = CreateDiscoveryFixture();
        var factory = new DiscoverySessionFactory();
        using var first = factory.Create(8, fixture.Discovery);
        using var second = factory.Create(8, fixture.Discovery);
        var payload = Encoding.UTF8.GetBytes("same-payload");

        Assert(
            first.CreateGenerationOpaqueTag("fixture.domain", payload) !=
            second.CreateGenerationOpaqueTag("fixture.domain", payload),
            "Different sessions with the same numeric generation must rotate the opaque key.");
    }

    private static void NewGenerationRotatesOpaqueTag()
    {
        using var fixture = CreateDiscoveryFixture();
        var factory = new DiscoverySessionFactory();
        using var first = factory.Create(9, fixture.Discovery);
        using var second = factory.Create(10, fixture.Discovery);
        var payload = Encoding.UTF8.GetBytes("same-payload");

        Assert(
            first.CreateGenerationOpaqueTag("fixture.domain", payload) !=
            second.CreateGenerationOpaqueTag("fixture.domain", payload),
            "A new generation must rotate the opaque key.");
    }

    private static void DisposedSessionZeroesKeyAndRejectsUse()
    {
        using var fixture = CreateDiscoveryFixture();
        var factory = new DiscoverySessionFactory();
        var session = factory.Create(11, fixture.Discovery);
        var keyField = typeof(DiscoverySession).GetField(
            "generationKey",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The session must retain one private generation key.");
        var key = (byte[])(keyField.GetValue(session) ??
            throw new InvalidOperationException("The private generation key must exist."));
        Assert(key.Length == 32 && key.Any(value => value != 0), "The active session key must be a nonzero 256-bit value.");

        session.Dispose();
        session.Dispose();

        Assert(key.All(value => value == 0), "Disposal must zero the exact owned generation-key buffer.");
        Assert(!session.IsActive, "A disposed session must be inactive.");
        Assert(
            !session.TryGetPair(fixture.Source.Id, fixture.Target.Id, out _),
            "A disposed session must reject pair creation.");
        AssertValidationRejected(
            factory.Revalidate(session, fixture.Source.Id, fixture.Target.Id),
            Pcl2DiagnosticCode.DiscoverySessionInactive,
            isStale: false,
            "A disposed session must reject revalidation.");
        AssertThrows<ObjectDisposedException>(
            () => session.CreateGenerationOpaqueTag("fixture.domain", [0x01]),
            "A disposed session must reject opaque-tag creation.");
    }

    private static void SharedAndUnknownIsolationRejected()
    {
        using var fixture = CreateDiscoveryFixture(includeIneligibleInstances: true);
        var shared = fixture.Discovery.Instances.Single(instance =>
            Path.GetFileName(instance.InstanceRoot) == "Shared");
        var unknown = fixture.Discovery.Instances.Single(instance =>
            Path.GetFileName(instance.InstanceRoot) == "Unknown");
        var factory = new DiscoverySessionFactory();
        using var session = factory.Create(12, fixture.Discovery);

        AssertValidationRejected(
            factory.Revalidate(session, shared.Id, fixture.Target.Id),
            Pcl2DiagnosticCode.NonIsolatedInstance,
            isStale: false,
            "A shared instance must not enter a writable pair.");
        AssertValidationRejected(
            factory.Revalidate(session, unknown.Id, fixture.Target.Id),
            Pcl2DiagnosticCode.NonIsolatedInstance,
            isStale: false,
            "An unknown-isolation instance must not enter a writable pair.");
    }

    private static void NonLocalAndUnknownVolumeRejected()
    {
        using (var nonLocal = CreateDiscoveryFixture(
                   capability => new VolumeOverrideCapability(
                       capability,
                       volume => volume with
                       {
                           IsLocalVolume = false,
                           IsNetworkRedirected = true,
                       })))
        {
            var factory = new DiscoverySessionFactory();
            using var session = factory.Create(13, nonLocal.Discovery);
            AssertValidationRejected(
                factory.Revalidate(session, nonLocal.Source.Id, nonLocal.Target.Id),
                Pcl2DiagnosticCode.UnsupportedGameRootVolume,
                isStale: false,
                "A non-local volume must remain read-only and unavailable for pairing.");
        }

        using var unknown = CreateDiscoveryFixture(
            capability => new VolumeOverrideCapability(
                capability,
                volume => volume with
                {
                    FileSystemName = string.Empty,
                    SupportsPersistentAcls = false,
                }));
        var unknownFactory = new DiscoverySessionFactory();
        using var unknownSession = unknownFactory.Create(14, unknown.Discovery);
        AssertValidationRejected(
            unknownFactory.Revalidate(
                unknownSession,
                unknown.Source.Id,
                unknown.Target.Id),
            Pcl2DiagnosticCode.UnsupportedGameRootVolume,
            isStale: false,
            "An unknown writer volume must be unavailable for pairing.");
    }

    private static void TokenReplayAndCrossSessionEvidenceRejected()
    {
        using var fixture = CreateDiscoveryFixture();
        var factory = new DiscoverySessionFactory();
        using var first = factory.Create(15, fixture.Discovery);
        using var second = factory.Create(15, fixture.Discovery);
        Assert(
            first.TryGetPair(fixture.Source.Id, fixture.Target.Id, out var firstPair),
            "The replay fixture requires a first-session pair.");

        var forgedChoice = firstPair.Source with
        {
            ProofToken = new string('A', 43),
        };
        AssertValidationRejected(
            first.ValidateEvidence(firstPair with { Source = forgedChoice }),
            Pcl2DiagnosticCode.DiscoveryProofInvalid,
            isStale: false,
            "A forged public proof token must be rejected.");
        AssertValidationRejected(
            first.ValidateEvidence(new DiscoveredInstancePair(
                null!,
                firstPair.Target,
                firstPair.Generation)),
            Pcl2DiagnosticCode.DiscoveryProofInvalid,
            isStale: false,
            "A malformed public evidence record must fail closed without throwing.");
        AssertValidationRejected(
            second.ValidateEvidence(firstPair),
            Pcl2DiagnosticCode.DiscoveryProofInvalid,
            isStale: false,
            "A proof replayed into another session must be rejected.");
    }

    private static void CancellationIsHonored()
    {
        using var fixture = CreateDiscoveryFixture();
        var factory = new DiscoverySessionFactory();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        AssertThrows<OperationCanceledException>(
            () => factory.Create(16, fixture.Discovery, canceled.Token),
            "A pre-canceled session creation must stop before capability access.");

        using var session = factory.Create(16, fixture.Discovery);
        AssertThrows<OperationCanceledException>(
            () => factory.Revalidate(
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                canceled.Token),
            "A pre-canceled revalidation must stop before capability access.");
    }

    private static void ConcurrentSessionsRemainIndependent()
    {
        using var fixture = CreateDiscoveryFixture();
        var factory = new DiscoverySessionFactory();
        using var first = factory.Create(17, fixture.Discovery);
        using var second = factory.Create(17, fixture.Discovery);
        var failures = 0;

        Parallel.For(0, 64, index =>
        {
            var session = index % 2 == 0 ? first : second;
            if (!factory.Revalidate(
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id).IsValid)
            {
                Interlocked.Increment(ref failures);
            }

            _ = session.CreateGenerationOpaqueTag(
                "fixture.concurrent",
                BitConverter.GetBytes(index));
        });

        Assert(failures == 0, "Concurrent sessions must revalidate independently.");
        first.Dispose();
        Assert(
            factory.Revalidate(
                second,
                fixture.Source.Id,
                fixture.Target.Id).IsValid,
            "Disposing one session must not invalidate another concurrent session.");
    }

    private static SessionFixture CreateDiscoveryFixture(
        Func<IFileSystemCapability, IFileSystemCapability>? decorateCapability = null,
        bool includeIneligibleInstances = false)
    {
        var sandbox = FixtureSandbox.Create();
        try
        {
            var minecraftRoot = sandbox.CreateGuidDirectory();
            var minecraftRelative = Path.GetRelativePath(sandbox.RootPath, minecraftRoot);
            WriteInstance(sandbox, minecraftRelative, "Source", "source", "true");
            WriteInstance(sandbox, minecraftRelative, "Target", "target", "true");
            if (includeIneligibleInstances)
            {
                WriteInstance(sandbox, minecraftRelative, "Shared", "shared", "false");
                WriteInstance(sandbox, minecraftRelative, "Unknown", "unknown", "sometimes");
            }

            sandbox.WriteBytes(
                Path.Combine(minecraftRelative, "PCL.ini"),
                Encoding.UTF8.GetBytes("Version:Source\r\n"));

            var audited = new AuditedFileSystemCapability(
                [sandbox.GetRootProof(minecraftRoot)]);
            var capability = decorateCapability?.Invoke(audited) ?? audited;
            var discovery = new Pcl2InstanceDiscovery(capability).Discover(
                Pcl2DiscoveryRequest.Create([minecraftRoot], []));
            var source = discovery.Instances.Single(instance =>
                Path.GetFileName(instance.InstanceRoot) == "Source");
            var target = discovery.Instances.Single(instance =>
                Path.GetFileName(instance.InstanceRoot) == "Target");
            return new SessionFixture(
                sandbox,
                audited,
                minecraftRoot,
                discovery,
                source,
                target);
        }
        catch
        {
            sandbox.Dispose();
            throw;
        }
    }

    private static void WriteInstance(
        FixtureSandbox sandbox,
        string minecraftRelative,
        string directoryName,
        string instanceId,
        string isolationValue)
    {
        sandbox.WriteBytes(
            Path.Combine(
                minecraftRelative,
                "versions",
                directoryName,
                directoryName + ".json"),
            Encoding.UTF8.GetBytes(
                $"{{\"id\":\"{instanceId}\",\"minecraftVersion\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\"}}"));
        sandbox.WriteBytes(
            Path.Combine(
                minecraftRelative,
                "versions",
                directoryName,
                "PCL",
                "Setup.ini"),
            Encoding.UTF8.GetBytes(
                $"VersionArgumentIndieV2:{isolationValue}\r\n"));
    }

    private static Pcl2DiscoveryResult ReplaceInstances(
        Pcl2DiscoveryResult discovery,
        params Pcl2Instance[] instances)
    {
        var root = discovery.Roots.Single();
        return new Pcl2DiscoveryResult(
            [root with { Instances = Array.AsReadOnly(instances) }],
            discovery.Diagnostics);
    }

    private static void AssertValidationAccepted(
        DiscoveryPairValidation validation,
        string message)
    {
        Assert(
            validation.IsValid &&
            !validation.IsStale &&
            validation.Pair is not null &&
            validation.Diagnostics.Count == 0,
            message);
    }

    private static void AssertValidationRejected(
        DiscoveryPairValidation validation,
        Pcl2DiagnosticCode expectedCode,
        bool isStale,
        string message)
    {
        Assert(
            !validation.IsValid &&
            validation.IsStale == isStale &&
            validation.Pair is null &&
            validation.Diagnostics.Count == 1 &&
            validation.Diagnostics[0].Code == expectedCode &&
            validation.Diagnostics[0].Severity == Pcl2DiagnosticSeverity.Error &&
            validation.Diagnostics[0].Path is null,
            message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class SessionFixture(
        FixtureSandbox sandbox,
        AuditedFileSystemCapability auditedCapability,
        string capabilityRoot,
        Pcl2DiscoveryResult discovery,
        Pcl2Instance source,
        Pcl2Instance target) : IDisposable
    {
        public FixtureSandbox Sandbox { get; } = sandbox;
        public AuditedFileSystemCapability AuditedCapability { get; } = auditedCapability;
        public Pcl2DiscoveryResult Discovery { get; } = discovery;
        public Pcl2Instance Source { get; } = source;
        public Pcl2Instance Target { get; } = target;

        public void Dispose()
        {
            Assert(
                AuditedCapability.AuditLog.All(entry => !entry.IsMutation),
                "Session discovery and revalidation must remain read-only.");
            AuditRoots.Add(capabilityRoot);
            AuditEvents.AddRange(AuditedCapability.AuditLog);
            Sandbox.Dispose();
        }
    }

    private sealed class VolumeOverrideCapability(
        IFileSystemCapability inner,
        Func<VolumeCapabilitySnapshot, VolumeCapabilitySnapshot> transform) :
        IFileSystemCapability
    {
        public IVerifiedDirectoryHandle OpenRoot(
            string absolutePath,
            FileSystemOpenPurpose purpose,
            CancellationToken cancellationToken) =>
            inner.OpenRoot(absolutePath, purpose, cancellationToken);

        public IVerifiedDirectoryHandle OpenDirectory(
            IVerifiedDirectoryHandle root,
            NormalizedRelativePath relativePath,
            CancellationToken cancellationToken) =>
            inner.OpenDirectory(root, relativePath, cancellationToken);

        public IReadOnlyList<FileSystemEntrySnapshot> EnumerateEntries(
            IVerifiedDirectoryHandle root,
            NormalizedRelativePath relativePath,
            EnumerationLimits limits,
            CancellationToken cancellationToken) =>
            inner.EnumerateEntries(root, relativePath, limits, cancellationToken);

        public BoundedFileSnapshot ReadFile(
            IVerifiedDirectoryHandle root,
            NormalizedRelativePath relativePath,
            FileReadLimits limits,
            CancellationToken cancellationToken) =>
            inner.ReadFile(root, relativePath, limits, cancellationToken);

        public IReadOnlyDictionary<string, BoundedFileSnapshot> ReadZipEntries(
            IVerifiedDirectoryHandle root,
            NormalizedRelativePath zipPath,
            IReadOnlySet<string> allowedEntryNames,
            ZipReadLimits limits,
            CancellationToken cancellationToken) =>
            inner.ReadZipEntries(
                root,
                zipPath,
                allowedEntryNames,
                limits,
                cancellationToken);

        public VolumeCapabilitySnapshot InspectVolume(
            IVerifiedDirectoryHandle root,
            CancellationToken cancellationToken) =>
            transform(inner.InspectVolume(root, cancellationToken));
    }
}
