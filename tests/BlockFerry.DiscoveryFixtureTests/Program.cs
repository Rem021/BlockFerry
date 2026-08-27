using System.Buffers.Binary;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;
using BlockFerry.Core.Transactions;
using BlockFerry.TestSupport;
using Microsoft.Win32.SafeHandles;

var requestedCase = ReadCase(args);
if (string.Equals(requestedCase, "discovery", StringComparison.Ordinal))
{
    RunDiscoveryCase();
    return;
}

if (string.Equals(requestedCase, "app-storage", StringComparison.Ordinal))
{
    RunAppStorageCase();
    return;
}

if (string.Equals(requestedCase, "session", StringComparison.Ordinal))
{
    SessionFixtureTests.Run();
    return;
}

if (!string.Equals(requestedCase, "capability", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Unknown fixture case: {requestedCase}");
}

var fixtureRoots = new List<string>();
var auditEvents = new List<CapabilityAuditEvent>();

PathValidationRejectsUnsafeNamesBeforeCapabilityAccess();
Full128BitIdentitiesRemainDistinct();
ReadOnlyCapabilityIsBoundedAndAudited(fixtureRoots, auditEvents);
ZipPreflightRejectsEntryCountBeforeArchiveMaterialization(fixtureRoots, auditEvents);
DirectoryRecordParserRejectsMalformedOffsets();
VolumeClassificationUsesRetainedHandleMetadata(fixtureRoots, auditEvents);
AuditedHandlesRejectCrossWrapperAndCrossAllowlistUse(fixtureRoots, auditEvents);
FixtureProofsRejectUnissuedAndReplacedGuidRoots(fixtureRoots, auditEvents);
AuditLogSnapshotsAreImmutableAndThreadSafe(fixtureRoots, auditEvents);
AuditSummaryIsDerivedFromVerifiedEvents();
RetainedRootHandleSurvivesPathReplacement(fixtureRoots, auditEvents);
ReparseSegmentsAreRejected(fixtureRoots, auditEvents);

Assert(auditEvents.Count > 0, "Capability tests must produce an access audit.");
var auditSummary = CapabilityAuditSummary.From(auditEvents);
Assert(auditSummary.WriteCount == 0, "The read-only capability audit must contain zero writes.");
Assert(auditSummary.RealRootAccessCount == 0, "The capability audit must contain zero accepted accesses outside fixture proofs.");
Assert(
    auditEvents.Where(entry => entry.RootId is not null && !entry.WasRejected).All(entry =>
        entry.FinalPath is not null &&
        entry.DirectoryIdentity is not null &&
        entry.DesiredAccess.Length > 0 &&
        entry.ShareMode.Length > 0),
    "Accepted capability events must retain access/share modes, final paths, and full directory identities.");

Console.WriteLine(
    "AUDIT: fixture-roots=" +
    string.Join('|', fixtureRoots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)));
Console.WriteLine(
    $"AUDIT: events={auditSummary.EventCount}; writes={auditSummary.WriteCount}; " +
    $"real-root-access={auditSummary.RealRootAccessCount}");
Console.WriteLine("PASS: capability");

static void PathValidationRejectsUnsafeNamesBeforeCapabilityAccess()
{
    Assert(
        NormalizedRelativePath.TryCreate(
            "config/jei/bookmarks.json",
            out var accepted,
            out var rejection),
        $"A safe relative path must normalize: {rejection}");
    Assert(accepted!.Value == "config\\jei\\bookmarks.json", "Safe separators must normalize deterministically.");
    Assert(
        accepted.Segments.SequenceEqual(["config", "jei", "bookmarks.json"], StringComparer.Ordinal),
        "Safe relative segments must remain immutable and ordered.");
    Assert(
        NormalizedRelativePath.TryCreate(string.Empty, out var root, out _),
        "The empty relative path must represent the retained root.");
    Assert(root!.Segments.Count == 0, "The retained-root path must contain no segments.");

    string[] rejectedPaths =
    [
        "..\\escape",
        ".\\escape",
        "folder\\..\\escape",
        "folder\\.\\file",
        "C:\\absolute",
        "C:drive-relative",
        "\\rooted",
        "\\\\server\\share\\file",
        "\\\\?\\C:\\device",
        "\\\\.\\PhysicalDrive0",
        "file.txt:stream",
        "folder\\trailing.\\file",
        "folder\\trailing \\file",
        "CON",
        "con.txt",
        "folder\\AUX.json",
        "folder\\COM1",
        "folder\\LPT9.txt",
        "folder\\\\file",
    ];

    foreach (var rejectedPath in rejectedPaths)
    {
        AssertPathRejected(rejectedPath, "Unsafe path must be rejected before capability access");
    }

    string[] reservedAliases =
    [
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³", "CONIN$", "CONOUT$",
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];
    foreach (var alias in reservedAliases)
    {
        AssertPathRejected(alias, "A documented Windows device alias must be rejected");
        AssertPathRejected(alias.ToLowerInvariant() + ".json", "Reserved aliases with extensions and casing changes must be rejected");
        AssertPathRejected("folder\\" + alias + ".txt", "Reserved aliases must be rejected in every component");
    }

    Assert(
        NormalizedRelativePath.TryCreate(new string('a', 255), out _, out var componentBoundaryRejection),
        $"A 255 UTF-16-code-unit component must remain valid: {componentBoundaryRejection}");
    AssertPathRejected(new string('a', 256), "A component over 255 UTF-16 code units must be rejected");
    Assert(
        NormalizedRelativePath.TryCreate(string.Concat(Enumerable.Repeat("😀", 127)), out _, out var unicodeBoundaryRejection),
        $"A 254-code-unit Unicode component must remain valid: {unicodeBoundaryRejection}");
    AssertPathRejected(
        string.Concat(Enumerable.Repeat("😀", 128)),
        "A Unicode component over 255 UTF-16 code units must be rejected");

    var maximumTotalPath = string.Join('\\', Enumerable.Repeat(new string('b', 255), 128));
    Assert(maximumTotalPath.Length == 32767, "The hand-derived total-path boundary fixture must be exact.");
    Assert(
        NormalizedRelativePath.TryCreate(maximumTotalPath, out _, out var totalBoundaryRejection),
        $"A 32767-code-unit relative path must remain UNICODE_STRING-safe: {totalBoundaryRejection}");
    AssertPathRejected(
        maximumTotalPath + "\\c",
        "A relative path over 32767 UTF-16 code units must be rejected without throwing");
    AssertPathRejected(
        new string('d', 32768),
        "An oversized raw component must return TryCreate=false rather than escaping OverflowException later");
}

static void Full128BitIdentitiesRemainDistinct()
{
    var directoryLow = new PhysicalDirectoryIdentity(17, 23, 0);
    var directoryHigh = new PhysicalDirectoryIdentity(17, 23, 1);
    var fileLow = new PhysicalFileIdentity(17, 23, 0);
    var fileHigh = new PhysicalFileIdentity(17, 23, 1);

    Assert(directoryLow != directoryHigh, "Directory identity equality must include the high 64 file-ID bits.");
    Assert(fileLow != fileHigh, "File identity equality must include the high 64 file-ID bits.");
}

static void ReadOnlyCapabilityIsBoundedAndAudited(
    ICollection<string> fixtureRoots,
    ICollection<CapabilityAuditEvent> collectedAudit)
{
    using var fixture = FixtureSandbox.Create();
    using var deniedFixture = FixtureSandbox.Create();
    fixtureRoots.Add(fixture.RootPath);
    fixtureRoots.Add(deniedFixture.RootPath);

    fixture.CreateDirectory("config");
    fixture.CreateDirectory("mods");
    fixture.WriteBytes("settings.bin", [0x01, 0x02, 0x03]);
    fixture.WriteBytes("config\\nested.bin", [0x0A]);
    fixture.CreateZip(
        "mods\\declarations.jar",
        ("META-INF/mods.toml", new byte[] { 0x01, 0x02 }),
        ("fabric.mod.json", new byte[] { 0x03, 0x04, 0x05 }),
        ("ignored.txt", new byte[] { 0x06 }));

    var capability = new AuditedFileSystemCapability([fixture.RootProof]);
    AssertThrows<CapabilityBoundaryException>(
        () => capability.OpenRoot(
            deniedFixture.RootPath,
            FileSystemOpenPurpose.Discovery,
            CancellationToken.None),
        "An out-of-allowlist GUID root must be rejected before production access.");

    using var root = capability.OpenRoot(
        fixture.RootPath,
        FileSystemOpenPurpose.Discovery,
        CancellationToken.None);
    var volume = capability.InspectVolume(root, CancellationToken.None);
    Assert(volume.IsLocalVolume && !volume.IsNetworkRedirected, "The local GUID fixture volume must be identified as local.");

    var entries = capability.EnumerateEntries(
        root,
        MustPath(string.Empty),
        new EnumerationLimits(8),
        CancellationToken.None);
    Assert(
        entries.Any(entry => entry.IsDirectory && entry.RelativePath.Value == "config") &&
        entries.Any(entry => !entry.IsDirectory && entry.RelativePath.Value == "settings.bin"),
        "Enumeration must return both verified directories and files.");
    AssertThrows<CapabilityLimitExceededException>(
        () => capability.EnumerateEntries(
            root,
            MustPath(string.Empty),
            new EnumerationLimits(1),
            CancellationToken.None),
        "Directory enumeration must fail closed at its entry bound.");

    var snapshot = capability.ReadFile(
        root,
        MustPath("settings.bin"),
        new FileReadLimits(3),
        CancellationToken.None);
    Assert(snapshot.Exists && snapshot.Length == 3, "A bounded file read must return the exact existing length.");
    Assert(
        snapshot.Sha256 == "039058C6F2C0CB492C533B0A4D14EF77CC0F78ABCCCED5287D84A1A2011CFB81",
        "A bounded file snapshot must hash the exact bytes from its retained handle.");
    var firstCopy = snapshot.CopyBytes();
    firstCopy[0] = 0xFF;
    Assert(snapshot.CopyBytes().SequenceEqual(new byte[] { 0x01, 0x02, 0x03 }), "Snapshot bytes must be immutable by defensive copy.");
    Assert(snapshot.Metadata.Identity is not null, "An existing ordinary file must carry its full physical identity.");

    AssertThrows<CapabilityLimitExceededException>(
        () => capability.ReadFile(
            root,
            MustPath("settings.bin"),
            new FileReadLimits(2),
            CancellationToken.None),
        "File reads must fail before exceeding their byte bound.");

    var missing = capability.ReadFile(
        root,
        MustPath("missing.bin"),
        new FileReadLimits(10),
        CancellationToken.None);
    Assert(!missing.Exists && missing.Length == 0 && missing.CopyBytes().Length == 0, "A missing leaf must return an immutable absent snapshot.");

    var allowedNames = new HashSet<string>(
        ["META-INF/mods.toml", "fabric.mod.json"],
        StringComparer.Ordinal);
    var zipEntries = capability.ReadZipEntries(
        root,
        MustPath("mods\\declarations.jar"),
        allowedNames,
        ZipLimits(),
        CancellationToken.None);
    Assert(
        zipEntries.Count == 2 &&
        zipEntries.ContainsKey("META-INF/mods.toml") &&
        zipEntries.ContainsKey("fabric.mod.json"),
        "ZIP reads must return only exact allowlisted declaration names.");
    Assert(zipEntries["fabric.mod.json"].CopyBytes() is [0x03, 0x04, 0x05], "ZIP bytes must come from the verified archive handle.");

    AssertThrows<CapabilityBoundaryException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("mods\\declarations.jar"),
            new HashSet<string>(["Fabric.mod.json"], StringComparer.Ordinal),
            ZipLimits(),
            CancellationToken.None),
        "A case alias of an allowlisted ZIP declaration must fail closed.");

    AssertThrows<CapabilityBoundaryException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("mods\\declarations.jar"),
            new HashSet<string>(["Fabric.mod.json"], StringComparer.OrdinalIgnoreCase),
            ZipLimits(),
            CancellationToken.None),
        "Caller comparer choice must not weaken case-alias rejection.");

    AssertThrows<CapabilityLimitExceededException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("mods\\declarations.jar"),
            allowedNames,
            ZipLimits(maximumEntries: 2),
            CancellationToken.None),
        "ZIP reads must enforce the archive entry-count bound.");
    AssertThrows<CapabilityLimitExceededException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("mods\\declarations.jar"),
            allowedNames,
            ZipLimits(maximumEntryBytes: 2),
            CancellationToken.None),
        "ZIP reads must enforce the per-entry byte bound.");
    AssertThrows<CapabilityLimitExceededException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("mods\\declarations.jar"),
            allowedNames,
            ZipLimits(maximumTotalBytes: 4),
            CancellationToken.None),
        "ZIP reads must enforce the total uncompressed byte bound.");

    var acceptedAudit = capability.AuditLog.Where(entry => entry.RootId is not null).ToArray();
    Assert(
        acceptedAudit.All(entry => capability.AllowedRootIds.Contains(entry.RootId!.Value)),
        "Every accepted access must carry an allowlisted capability root ID.");
    Assert(capability.AuditLog.Any(entry => entry.WasRejected && entry.RootId is null), "The denied GUID root attempt must be audited without opening it.");
    Assert(capability.AuditLog.All(entry => !entry.IsMutation), "Capability use must record zero write operations.");
    foreach (var entry in capability.AuditLog)
    {
        collectedAudit.Add(entry);
    }
}

static void RetainedRootHandleSurvivesPathReplacement(
    ICollection<string> fixtureRoots,
    ICollection<CapabilityAuditEvent> collectedAudit)
{
    using var fixture = FixtureSandbox.Create();
    fixtureRoots.Add(fixture.RootPath);
    var retainedRoot = fixture.CreateGuidDirectory();
    fixtureRoots.Add(retainedRoot);
    var movedRoot = fixture.AllocateGuidPath();
    File.WriteAllBytes(Path.Combine(retainedRoot, "value.bin"), [0x11]);

    var capability = new AuditedFileSystemCapability([fixture.GetRootProof(retainedRoot)]);
    using (var root = capability.OpenRoot(
               retainedRoot,
               FileSystemOpenPurpose.Discovery,
               CancellationToken.None))
    {
        Directory.Move(retainedRoot, movedRoot);
        Directory.CreateDirectory(retainedRoot);
        File.WriteAllBytes(Path.Combine(retainedRoot, "value.bin"), [0x22]);

        var retained = capability.ReadFile(
            root,
            MustPath("value.bin"),
            new FileReadLimits(1),
            CancellationToken.None);
        Assert(
            retained.CopyBytes().SequenceEqual(new byte[] { 0x11 }),
            "A retained directory handle must not drift to a replacement at the same pathname.");
    }

    foreach (var entry in capability.AuditLog)
    {
        collectedAudit.Add(entry);
    }
}

static void ZipPreflightRejectsEntryCountBeforeArchiveMaterialization(
    ICollection<string> fixtureRoots,
    ICollection<CapabilityAuditEvent> collectedAudit)
{
    using var fixture = FixtureSandbox.Create();
    fixtureRoots.Add(fixture.RootPath);
    fixture.WriteBytes("classic-too-many.zip", CreateClassicEocd(4, 0, 0));
    fixture.WriteBytes("classic-central-too-large.zip", CreateClassicEocd(0, 65, 0));
    fixture.WriteBytes("classic-offset-out-of-range.zip", CreateClassicEocd(0, 1, uint.MaxValue));
    fixture.WriteBytes(
        "classic-malformed-central-record.zip",
        CreateClassicArchiveWithMalformedCentralRecord());
    fixture.WriteBytes("zip64-too-many.zip", CreateZip64Archive(4, 0, 0));
    fixture.WriteBytes("zip64-central-too-large.zip", CreateZip64Archive(0, 65, 0));
    fixture.WriteBytes("zip64-offset-overflow.zip", CreateZip64Archive(0, 1, ulong.MaxValue));
    fixture.WriteBytes("zip64-malformed-eocd.zip", CreateZip64Archive(0, 0, 0, zip64RecordSize: 43));
    fixture.WriteBytes("archive-too-large.zip", new byte[129]);
    fixture.CreateZip("pre-canceled.zip", ("allowed.json", new byte[] { 0x01 }));

    var capability = new AuditedFileSystemCapability([fixture.RootProof]);
    using var root = capability.OpenRoot(
        fixture.RootPath,
        FileSystemOpenPurpose.Discovery,
        CancellationToken.None);
    AssertThrowsExactly<CapabilityLimitExceededException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("classic-too-many.zip"),
            new HashSet<string>(StringComparer.Ordinal),
            ZipLimits(maximumEntryBytes: 1, maximumTotalBytes: 1),
            CancellationToken.None),
        "ZIP entry-count metadata must be rejected by bounded preflight before ZipArchive materialization");
    AssertThrowsExactly<CapabilityLimitExceededException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("classic-central-too-large.zip"),
            new HashSet<string>(StringComparer.Ordinal),
            ZipLimits(maximumCentralDirectoryBytes: 64),
            CancellationToken.None),
        "Classic EOCD central-directory bytes must be bounded before materialization");
    AssertThrowsExactly<CapabilityBoundaryException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("classic-offset-out-of-range.zip"),
            new HashSet<string>(StringComparer.Ordinal),
            ZipLimits(),
            CancellationToken.None),
        "Classic EOCD offsets outside the verified archive must fail closed");
    AssertThrowsExactly<CapabilityBoundaryException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("classic-malformed-central-record.zip"),
            new HashSet<string>(StringComparer.Ordinal),
            ZipLimits(),
            CancellationToken.None),
        "Malformed classic central-directory records must fail checked preflight");
    AssertThrowsExactly<CapabilityLimitExceededException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("zip64-too-many.zip"),
            new HashSet<string>(StringComparer.Ordinal),
            ZipLimits(maximumEntryBytes: 1, maximumTotalBytes: 1),
            CancellationToken.None),
        "ZIP64 entry counts must be bounded before materialization");
    AssertThrowsExactly<CapabilityLimitExceededException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("zip64-central-too-large.zip"),
            new HashSet<string>(StringComparer.Ordinal),
            ZipLimits(maximumCentralDirectoryBytes: 64),
            CancellationToken.None),
        "ZIP64 central-directory bytes must be bounded before materialization");
    AssertThrowsExactly<CapabilityBoundaryException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("zip64-offset-overflow.zip"),
            new HashSet<string>(StringComparer.Ordinal),
            ZipLimits(),
            CancellationToken.None),
        "ZIP64 offset arithmetic must fail closed without raw overflow");
    AssertThrowsExactly<CapabilityBoundaryException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("zip64-malformed-eocd.zip"),
            new HashSet<string>(StringComparer.Ordinal),
            ZipLimits(),
            CancellationToken.None),
        "Malformed ZIP64 EOCD records must fail checked preflight");
    AssertThrowsExactly<CapabilityLimitExceededException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("archive-too-large.zip"),
            new HashSet<string>(StringComparer.Ordinal),
            ZipLimits(maximumArchiveBytes: 128, maximumCentralDirectoryBytes: 64),
            CancellationToken.None),
        "Archive bytes must be bounded before EOCD or central-directory parsing");

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    AssertThrowsExactly<OperationCanceledException>(
        () => capability.ReadZipEntries(
            root,
            MustPath("pre-canceled.zip"),
            new HashSet<string>(["allowed.json"], StringComparer.Ordinal),
            ZipLimits(),
            cancellation.Token),
        "A pre-canceled ZIP fixture must stop before parsing or materialization");

    foreach (var entry in capability.AuditLog)
    {
        collectedAudit.Add(entry);
    }
}

static void DirectoryRecordParserRejectsMalformedOffsets()
{
    var validFirst = CreateDirectoryRecord("a", 72, 72);
    var validSecond = CreateDirectoryRecord("b", 0, 70);
    var validRecords = new byte[validFirst.Length + validSecond.Length];
    validFirst.CopyTo(validRecords, 0);
    validSecond.CopyTo(validRecords, validFirst.Length);
    Assert(
        WindowsDirectoryRecordParser.Parse(validRecords, 4)
            .SequenceEqual(["a", "b"], StringComparer.Ordinal),
        "The isolated parser seam must preserve valid FILE_FULL_DIR_INFO names.");

    AssertThrowsExactly<CapabilityBoundaryException>(
        () => WindowsDirectoryRecordParser.Parse(CreateDirectoryRecord("a", 70, 140), 4),
        "An unaligned NextEntryOffset must fail checked parsing");

    var headerShort = CreateDirectoryRecord("a", 72, 73);
    AssertThrowsExactly<CapabilityBoundaryException>(
        () => WindowsDirectoryRecordParser.Parse(headerShort, 4),
        "A NextEntryOffset leaving only a final byte must fail before reading the next header");

    var overlapsName = CreateDirectoryRecord("abcd", 72, 144);
    AssertThrowsExactly<CapabilityBoundaryException>(
        () => WindowsDirectoryRecordParser.Parse(overlapsName, 4),
        "A NextEntryOffset overlapping the aligned current filename must fail closed");

    var overflowingOffset = CreateDirectoryRecord("a", uint.MaxValue, 72);
    AssertThrowsExactly<CapabilityBoundaryException>(
        () => WindowsDirectoryRecordParser.Parse(overflowingOffset, 4),
        "An overflowing NextEntryOffset must fail without unchecked arithmetic");

    AssertThrowsExactly<CapabilityBoundaryException>(
        () => WindowsDirectoryRecordParser.Parse(new byte[67], 4),
        "A final record shorter than the fixed 68-byte header must fail before any field read");
}

static void VolumeClassificationUsesRetainedHandleMetadata(
    ICollection<string> fixtureRoots,
    ICollection<CapabilityAuditEvent> collectedAudit)
{
    using var fixture = FixtureSandbox.Create();
    fixtureRoots.Add(fixture.RootPath);

    AssertVolumeClassification(
        fixture.RootProof,
        new WindowsHandleVolumeMetadata(
            VolumeInformationSucceeded: true,
            FileSystemName: "NTFS",
            SupportsPersistentAcls: true,
            RemoteProtocol: WindowsRemoteProtocolDisposition.Local),
        expectedLocal: true,
        expectedRemote: false,
        expectedPersistentAcls: true,
        collectedAudit,
        "A positively proven local NTFS handle must retain local and persistent-ACL claims.");

    AssertVolumeClassification(
        fixture.RootProof,
        new WindowsHandleVolumeMetadata(
            VolumeInformationSucceeded: true,
            FileSystemName: "NTFS",
            SupportsPersistentAcls: true,
            RemoteProtocol: WindowsRemoteProtocolDisposition.Unknown),
        expectedLocal: false,
        expectedRemote: false,
        expectedPersistentAcls: false,
        collectedAudit,
        "Unknown remote-protocol metadata must fail closed without local or persistent-ACL claims.");

    AssertVolumeClassification(
        fixture.RootProof,
        new WindowsHandleVolumeMetadata(
            VolumeInformationSucceeded: false,
            FileSystemName: "NTFS",
            SupportsPersistentAcls: true,
            RemoteProtocol: WindowsRemoteProtocolDisposition.Local),
        expectedLocal: false,
        expectedRemote: false,
        expectedPersistentAcls: false,
        collectedAudit,
        "A volume-metadata error must fail closed even if the remote query reports local.");

    AssertVolumeClassification(
        fixture.RootProof,
        new WindowsHandleVolumeMetadata(
            VolumeInformationSucceeded: true,
            FileSystemName: "NTFS",
            SupportsPersistentAcls: true,
            RemoteProtocol: WindowsRemoteProtocolDisposition.Remote),
        expectedLocal: false,
        expectedRemote: true,
        expectedPersistentAcls: false,
        collectedAudit,
        "A retained handle with remote protocol metadata must be classified as redirected and never claim persistent ACLs.");
}

static void AssertVolumeClassification(
    FixtureRootProof rootProof,
    WindowsHandleVolumeMetadata metadata,
    bool expectedLocal,
    bool expectedRemote,
    bool expectedPersistentAcls,
    ICollection<CapabilityAuditEvent> collectedAudit,
    string message)
{
    var inner = new WindowsFileSystemCapability(new StubWindowsHandleVolumeMetadataReader(metadata));
    var capability = new AuditedFileSystemCapability(inner, [rootProof]);
    using var root = capability.OpenRoot(
        rootProof.RootPath,
        FileSystemOpenPurpose.Discovery,
        CancellationToken.None);
    var volume = capability.InspectVolume(root, CancellationToken.None);

    Assert(root.IsLocalVolume == expectedLocal, $"{message} Root local classification differed.");
    Assert(root.IsNetworkRedirected == expectedRemote, $"{message} Root remote classification differed.");
    Assert(volume.IsLocalVolume == expectedLocal, $"{message} Snapshot local classification differed.");
    Assert(volume.IsNetworkRedirected == expectedRemote, $"{message} Snapshot remote classification differed.");
    Assert(volume.SupportsPersistentAcls == expectedPersistentAcls, $"{message} Persistent-ACL classification differed.");

    foreach (var entry in capability.AuditLog)
    {
        collectedAudit.Add(entry);
    }
}

static void AuditedHandlesRejectCrossWrapperAndCrossAllowlistUse(
    ICollection<string> fixtureRoots,
    ICollection<CapabilityAuditEvent> collectedAudit)
{
    using var firstFixture = FixtureSandbox.Create();
    using var secondFixture = FixtureSandbox.Create();
    fixtureRoots.Add(firstFixture.RootPath);
    fixtureRoots.Add(secondFixture.RootPath);
    firstFixture.WriteBytes("payload.bin", [0x41]);

    var sharedInner = new WindowsFileSystemCapability();
    var issuer = new AuditedFileSystemCapability(sharedInner, [firstFixture.RootProof]);
    var sameAllowlistOtherWrapper = new AuditedFileSystemCapability(sharedInner, [firstFixture.RootProof]);
    var otherAllowlistWrapper = new AuditedFileSystemCapability(sharedInner, [secondFixture.RootProof]);
    using var issuedHandle = issuer.OpenRoot(
        firstFixture.RootPath,
        FileSystemOpenPurpose.Discovery,
        CancellationToken.None);

    AssertThrowsExactly<CapabilityBoundaryException>(
        () => sameAllowlistOtherWrapper.ReadFile(
            issuedHandle,
            MustPath("payload.bin"),
            new FileReadLimits(1),
            CancellationToken.None),
        "An audited handle must be rejected by a different wrapper even when both wrappers share the same inner capability and allowlist");
    AssertThrowsExactly<CapabilityBoundaryException>(
        () => otherAllowlistWrapper.InspectVolume(issuedHandle, CancellationToken.None),
        "An audited handle must be rejected by a wrapper whose fixture allowlist does not contain its issuing root");
    Assert(
        sameAllowlistOtherWrapper.AuditLog.Count == 0 && otherAllowlistWrapper.AuditLog.Count == 0,
        "Cross-wrapper handle rejection must occur before an inner capability access is audited.");

    foreach (var entry in issuer.AuditLog)
    {
        collectedAudit.Add(entry);
    }
}

static void FixtureProofsRejectUnissuedAndReplacedGuidRoots(
    ICollection<string> fixtureRoots,
    ICollection<CapabilityAuditEvent> collectedAudit)
{
    using var fixture = FixtureSandbox.Create();
    fixtureRoots.Add(fixture.RootPath);

    var realLookingParent = fixture.CreateDirectory(".minecraft");
    var unissuedGuidRoot = Path.Combine(realLookingParent, Guid.NewGuid().ToString("D"));
    Directory.CreateDirectory(unissuedGuidRoot);
    AssertThrowsExactly<InvalidOperationException>(
        () => fixture.GetRootProof(unissuedGuidRoot),
        "A GUID-named real-looking directory created under controlled temp must not gain fixture authority without sandbox issuance");

    Assert(
        !typeof(AuditedFileSystemCapability)
            .GetConstructors()
            .Any(constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(IEnumerable<string>))),
        "Audited fixture allowlists must not retain a forgeable path-only constructor.");

    var replaceableRoot = fixture.CreateGuidDirectory();
    var replaceableProof = fixture.GetRootProof(replaceableRoot);
    var displacedRoot = fixture.AllocateGuidPath();
    Directory.Move(replaceableRoot, displacedRoot);
    Directory.CreateDirectory(replaceableRoot);

    var capability = new AuditedFileSystemCapability([replaceableProof]);
    AssertThrowsExactly<CapabilityBoundaryException>(
        () => capability.OpenRoot(
            replaceableRoot,
            FileSystemOpenPurpose.Discovery,
            CancellationToken.None),
        "A path replacement with a different physical root identity must invalidate the issued fixture proof");
    Assert(capability.AuditLog.Any(entry => entry.Operation == "OpenRoot" && entry.WasRejected), "Physical-root proof rejection must be audited.");

    foreach (var entry in capability.AuditLog)
    {
        collectedAudit.Add(entry);
    }
}

static void AuditLogSnapshotsAreImmutableAndThreadSafe(
    ICollection<string> fixtureRoots,
    ICollection<CapabilityAuditEvent> collectedAudit)
{
    using var fixture = FixtureSandbox.Create();
    fixtureRoots.Add(fixture.RootPath);
    var capability = new AuditedFileSystemCapability([fixture.RootProof]);
    using var root = capability.OpenRoot(
        fixture.RootPath,
        FileSystemOpenPurpose.Discovery,
        CancellationToken.None);

    var immutableSnapshot = capability.AuditLog;
    var snapshotCount = immutableSnapshot.Count;
    _ = capability.InspectVolume(root, CancellationToken.None);
    Assert(
        immutableSnapshot.Count == snapshotCount,
        "An audit-log snapshot must not change when later capability events are recorded.");
    Assert(
        immutableSnapshot is not List<CapabilityAuditEvent>,
        "An audit-log snapshot must not expose the mutable backing list.");

    const int concurrentReads = 16;
    var beforeConcurrentReads = capability.AuditLog.Count;
    Parallel.For(
        0,
        concurrentReads,
        _ => capability.InspectVolume(root, CancellationToken.None));
    Assert(
        capability.AuditLog.Count == beforeConcurrentReads + concurrentReads,
        "Concurrent read observations must each produce one synchronized audit event.");

    foreach (var entry in capability.AuditLog)
    {
        collectedAudit.Add(entry);
    }
}

static void AuditSummaryIsDerivedFromVerifiedEvents()
{
    var fixtureRootId = Guid.NewGuid();
    CapabilityAuditEvent[] events =
    [
        CreateSyntheticAuditEvent(fixtureRootId, wasRejected: false, isMutation: true),
        CreateSyntheticAuditEvent(rootId: null, wasRejected: false, isMutation: false),
        CreateSyntheticAuditEvent(rootId: null, wasRejected: true, isMutation: false),
    ];

    var summary = CapabilityAuditSummary.From(events);
    Assert(summary.EventCount == 3, "The audit summary event count must be derived from its input snapshot.");
    Assert(summary.WriteCount == 1, "The audit summary write count must be derived from mutation events.");
    Assert(
        summary.RealRootAccessCount == 1,
        "Only an accepted event without a verified fixture root ID must count as real-root access.");
}

static CapabilityAuditEvent CreateSyntheticAuditEvent(
    Guid? rootId,
    bool wasRejected,
    bool isMutation) =>
    new(
        Operation: "Synthetic",
        RootId: rootId,
        RequestedPath: string.Empty,
        DesiredAccess: "READ",
        ShareMode: "READ",
        FinalPath: null,
        DirectoryIdentity: null,
        FileIdentity: null,
        WasRejected: wasRejected,
        IsMutation: isMutation);

static void ReparseSegmentsAreRejected(
    ICollection<string> fixtureRoots,
    ICollection<CapabilityAuditEvent> collectedAudit)
{
    using var fixture = FixtureSandbox.Create();
    fixtureRoots.Add(fixture.RootPath);
    var target = fixture.CreateGuidDirectory();
    File.WriteAllBytes(Path.Combine(target, "outside.bin"), [0x44]);
    var junction = fixture.AllocateGuidPath();
    Assert(
        fixture.TryCreateDirectoryJunction(junction, target),
        "The Windows GUID fixture must be able to create a temporary junction.");

    try
    {
        var capability = new AuditedFileSystemCapability([fixture.RootProof]);
        using var root = capability.OpenRoot(
            fixture.RootPath,
            FileSystemOpenPurpose.Discovery,
            CancellationToken.None);
        AssertThrows<CapabilityBoundaryException>(
            () => capability.OpenDirectory(
                root,
                MustPath(Path.GetFileName(junction)),
                CancellationToken.None),
            "A reparse directory segment must be rejected rather than followed.");
        Assert(capability.AuditLog.Any(entry => entry.Operation == "OpenDirectory" && entry.WasRejected), "Reparse rejection must be audited.");
        foreach (var entry in capability.AuditLog)
        {
            collectedAudit.Add(entry);
        }
    }
    finally
    {
        fixture.DeleteLink(junction);
    }
}

static void RunAppStorageCase()
{
    var fixtureRoots = new List<string>();
    var capabilityEvents = new List<CapabilityAuditEvent>();
    var storageEvents = new List<AppStorageAuditEvent>();

    RenameAbiLayoutsAreLiteral();
    GlobalStorageMutexIsCrossSessionAndAclBound(fixtureRoots, capabilityEvents, storageEvents);
    RememberedRootsRoundTrip(fixtureRoots, capabilityEvents, storageEvents);
    PreAllocationBoundsAreEnforced(fixtureRoots, capabilityEvents, storageEvents);
    CrossUserOrCorruptPayloadFailsClosed(fixtureRoots, capabilityEvents, storageEvents);
    ReparseAppStorageNeverWrites(fixtureRoots, capabilityEvents, storageEvents);
    OversizeAndUnexpectedSchemaFailClosed(fixtureRoots, capabilityEvents, storageEvents);
    LegacyRootHardeningPreservesUnrelatedChildren(fixtureRoots, capabilityEvents, storageEvents);
    DaclDriftFailsBeforeMutation(fixtureRoots, capabilityEvents, storageEvents);
    AppRootIdentityDriftFailsBeforeMutation(fixtureRoots, capabilityEvents, storageEvents);
    AtomicReplaceAndCancellationAreFailClosed(fixtureRoots, capabilityEvents, storageEvents);
    NamespaceInterleavingsRetainExclusiveObjects(fixtureRoots, capabilityEvents, storageEvents);
    DeleteDispositionRequiresNamespaceVerification(fixtureRoots, capabilityEvents, storageEvents);
    CommitStageCancellationAndFaultsHaveExplicitOutcomes(fixtureRoots, capabilityEvents, storageEvents);
    ClearStageCancellationAndFaultsHaveExplicitOutcomes(fixtureRoots, capabilityEvents, storageEvents);
    InterloperAfterTargetTombstoneRequiresRecovery(fixtureRoots, capabilityEvents, storageEvents);
    CrashRecoveryProtocolIsAuthenticatedAndIdempotent(fixtureRoots, capabilityEvents, storageEvents);
    ConcurrentGuardsSerializeMutations(fixtureRoots, capabilityEvents, storageEvents);
    OnlyCurrentProvenManualRootsAndInstancesCanPersist(fixtureRoots, capabilityEvents, storageEvents);
    RootApprovalIsReprovedAfterStorageMutexWait(fixtureRoots, capabilityEvents, storageEvents);

    var capabilitySummary = CapabilityAuditSummary.From(capabilityEvents);
    Assert(capabilitySummary.WriteCount == 0, "The read-only capability audit must still contain zero writes.");
    Assert(
        capabilitySummary.RealRootAccessCount == 0,
        "Every accepted LocalAppData capability access must carry a GUID fixture proof.");
    Assert(
        storageEvents.All(entry =>
            !entry.Operation.Contains("C:\\", StringComparison.OrdinalIgnoreCase) &&
            !entry.OpaqueObject.Contains("C:\\", StringComparison.OrdinalIgnoreCase)),
        "App-storage audit events must not record plaintext absolute paths.");

    Console.WriteLine(
        "AUDIT: fixture-roots=" +
        string.Join('|', fixtureRoots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)));
    Console.WriteLine(
        $"AUDIT: capability-events={capabilitySummary.EventCount}; capability-writes={capabilitySummary.WriteCount}; " +
        $"real-root-access={capabilitySummary.RealRootAccessCount}; storage-events={storageEvents.Count}; " +
        $"storage-mutations={storageEvents.Count(entry => entry.IsMutation)}");
    Console.WriteLine("PASS: app-storage");
}

static void RootApprovalIsReprovedAfterStorageMutexWait(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    using var selectedRoot = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    fixtureRoots.Add(selectedRoot.RootPath);
    var selectedMinecraft = selectedRoot.CreateDirectory(".minecraft");
    selectedRoot.CreateDirectory(".minecraft\\versions");
    var rootCapability = new AuditedFileSystemCapability(
        [IssueNestedRootProof(selectedRoot, selectedMinecraft)]);
    var manualCandidate = new InstanceCandidateResolver(rootCapability)
        .ResolveManualSelection(selectedMinecraft, "manual mutex fixture")
        .Single();

    var firstStorageCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    var secondStorageCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var firstEntered = new ManualResetEventSlim();
    using var releaseFirst = new ManualResetEventSlim();
    var firstArmed = false;
    var secondReachedPrecommit = false;
    var firstProbe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (firstArmed && point == AppStorageInterleavingPoint.BeforeCommitRename)
        {
            firstEntered.Set();
            Assert(releaseFirst.Wait(TimeSpan.FromSeconds(10)), "The mutex-window fixture timed out holding the first mutation.");
        }
    });
    var secondProbe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (point == AppStorageInterleavingPoint.BeforeCommitRename)
        {
            secondReachedPrecommit = true;
        }
    });
    using var firstGuard = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        firstStorageCapability,
        firstProbe);
    using var secondGuard = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        secondStorageCapability,
        secondProbe);
    var relative = MustPath("discovery-roots.json");
    Assert(
        firstGuard.TryAtomicReplace(relative, [0x70], CancellationToken.None).State ==
        AppStorageMutationState.CommittedVerified,
        "The mutex-window fixture must establish a target payload.");
    var store = new DiscoveryRootStore(secondGuard, new TestProtectedData(new byte[32]));
    var approval = store.ApproveManualRoot(manualCandidate) ??
        throw new InvalidOperationException("The mutex-window fixture must obtain a manual approval token.");
    var approved = new RememberedDiscoveryRoots(1, [selectedMinecraft], null, null);

    firstArmed = true;
    var firstMutation = Task.Run(() =>
        firstGuard.TryAtomicReplace(relative, [0x71], CancellationToken.None));
    Assert(firstEntered.Wait(TimeSpan.FromSeconds(10)), "The first guard did not retain the global mutex at precommit.");
    var rootAuditBeforeSave = rootCapability.AuditLog.Count;
    var secondMutation = Task.Run(() => store.Save(approved, [approval]));
    Assert(
        SpinWait.SpinUntil(
            () => rootCapability.AuditLog.Count >= rootAuditBeforeSave + 2,
            TimeSpan.FromSeconds(10)),
        "The second Save did not complete its early approval reproof before waiting on storage.");

    var displaced = selectedRoot.AllocateGuidPath();
    Directory.Move(selectedMinecraft, displaced);
    Directory.CreateDirectory(selectedMinecraft);
    Directory.CreateDirectory(Path.Combine(selectedMinecraft, "versions"));
    releaseFirst.Set();
    Assert(Task.WaitAll([firstMutation, secondMutation], TimeSpan.FromSeconds(15)),
        "The mutex-window fixture mutations did not reach terminal results.");
    Assert(firstMutation.Result.State == AppStorageMutationState.CommittedVerified,
        "The first serialized mutation must commit before the late root reproof.");
    Assert(
        secondMutation.Result.State == AppStorageMutationState.NotCommitted && !secondReachedPrecommit,
        "A root replaced while Save waits on the global mutex must fail its after-mutex authority lease before the first namespace mutation.");
    var payload = Path.Combine(localAppData.RootPath, "BlockFerry", "discovery-roots.json");
    Assert(File.ReadAllBytes(payload).SequenceEqual(new byte[] { 0x71 }),
        "Late root reproof failure must preserve the preceding committed payload bytes.");
    Assert(
        !Directory.EnumerateFiles(Path.GetDirectoryName(payload)!, ".bf-*", SearchOption.TopDirectoryOnly).Any(),
        "Late root reproof failure must roll back its stage and authenticated manifest completely.");
    capabilityEvents.AddRange(rootCapability.AuditLog);
    capabilityEvents.AddRange(firstStorageCapability.AuditLog);
    capabilityEvents.AddRange(secondStorageCapability.AuditLog);
    storageEvents.AddRange(firstGuard.AuditLog);
    storageEvents.AddRange(secondGuard.AuditLog);
}

static void CrashRecoveryProtocolIsAuthenticatedAndIdempotent(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    RecoverCrashedReplace(
        AppStorageInterleavingPoint.RecoveryManifestDurable,
        expectedBytes: [0x31],
        fixtureRoots,
        capabilityEvents,
        storageEvents);
    RecoverCrashedReplace(
        AppStorageInterleavingPoint.DirectoryDurableAfterTargetTombstone,
        expectedBytes: [0x31],
        fixtureRoots,
        capabilityEvents,
        storageEvents);
    RecoverCrashedReplace(
        AppStorageInterleavingPoint.FinalDurable,
        expectedBytes: [0x32],
        fixtureRoots,
        capabilityEvents,
        storageEvents);
    RecoverCrashedClear(
        AppStorageInterleavingPoint.ClearRecoveryManifestDurable,
        expectedMissing: false,
        fixtureRoots,
        capabilityEvents,
        storageEvents);
    RecoverCrashedClear(
        AppStorageInterleavingPoint.ClearDirectoryDurableAfterTombstone,
        expectedMissing: true,
        fixtureRoots,
        capabilityEvents,
        storageEvents);
    RecoverCrashedClear(
        AppStorageInterleavingPoint.AfterClearDelete,
        expectedMissing: true,
        fixtureRoots,
        capabilityEvents,
        storageEvents);
    InterloperRecoveryIsPreserved(fixtureRoots, capabilityEvents, storageEvents);
    MultipleRecoveryTransactionsAreAmbiguous(fixtureRoots, capabilityEvents, storageEvents);
    TamperedRecoveryContentIsRejected(fixtureRoots, capabilityEvents, storageEvents);
    AbandonedGlobalMutexTriggersRecoveryScan(fixtureRoots, capabilityEvents, storageEvents);
}

static void RecoverCrashedClear(
    AppStorageInterleavingPoint crashPoint,
    bool expectedMissing,
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var firstCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    var armed = false;
    var probe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (armed && point == crashPoint)
        {
            throw new AppStorageCrashSimulationException(point);
        }
    });
    var relative = MustPath("discovery-roots.json");
    using (var crashed = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        firstCapability,
        probe))
    {
        Assert(
            crashed.TryAtomicReplace(relative, [0x35], CancellationToken.None).State ==
            AppStorageMutationState.CommittedVerified,
            "The Clear crash-recovery fixture must establish a payload.");
        armed = true;
        AssertThrows<AppStorageCrashSimulationException>(
            () => _ = crashed.TryDelete(relative, CancellationToken.None),
            $"The production Clear crash seam {crashPoint} must escape in-process rollback.");
    }

    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    Assert(
        Directory.EnumerateFiles(appRoot, ".bf-*", SearchOption.TopDirectoryOnly).Any(),
        $"The simulated Clear crash at {crashPoint} must leave an authenticated recovery transaction.");
    var recoveryCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var recovered = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        recoveryCapability);
    var read = recovered.TryRead(relative, 16, CancellationToken.None);
    Assert(
        expectedMissing
            ? read.State == AppStorageReadState.Missing
            : read.State == AppStorageReadState.Read && read.Bytes!.SequenceEqual(new byte[] { 0x35 }),
        $"Recovered Clear {crashPoint} must prove its unique terminal namespace state; " +
        $"state={read.State}; diagnostic={recovered.LastDiagnostic}.");
    Assert(
        !Directory.EnumerateFiles(appRoot, ".bf-*", SearchOption.TopDirectoryOnly).Any(),
        $"Recovered Clear {crashPoint} must leave no DPAPI manifest or data tombstone.");
    capabilityEvents.AddRange(firstCapability.AuditLog);
    capabilityEvents.AddRange(recoveryCapability.AuditLog);
    storageEvents.AddRange(recovered.AuditLog);
}

static void RecoverCrashedReplace(
    AppStorageInterleavingPoint crashPoint,
    byte[] expectedBytes,
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var firstCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    var armed = false;
    var probe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (armed && point == crashPoint)
        {
            throw new AppStorageCrashSimulationException(crashPoint);
        }
    });
    var relative = MustPath("discovery-roots.json");
    using (var crashed = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        firstCapability,
        probe))
    {
        Assert(
            crashed.TryAtomicReplace(relative, [0x31], CancellationToken.None).State ==
            AppStorageMutationState.CommittedVerified,
            "The crash-recovery fixture must establish its old payload.");
        armed = true;
        AssertThrows<AppStorageCrashSimulationException>(
            () => _ = crashed.TryAtomicReplace(relative, [0x32], CancellationToken.None),
            $"The production crash seam {crashPoint} must escape ordinary in-process rollback.");
    }

    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    Assert(
        Directory.EnumerateFiles(appRoot, ".bf-*", SearchOption.TopDirectoryOnly).Any(),
        $"The simulated crash at {crashPoint} must leave a linked transaction for startup recovery.");

    var recoveryCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using (var recovered = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        recoveryCapability))
    {
        var read = recovered.TryRead(relative, 16, CancellationToken.None);
        Assert(
            read.State == AppStorageReadState.Read && read.Bytes!.SequenceEqual(expectedBytes),
            $"Recovery after {crashPoint} must choose the uniquely proved old/new terminal state; " +
            $"state={read.State}; diagnostic={recovered.LastDiagnostic}; entries=" +
            string.Join(',', Directory.EnumerateFileSystemEntries(appRoot).Select(Path.GetFileName)) + ".");
        Assert(
            !Directory.EnumerateFiles(appRoot, ".bf-*", SearchOption.TopDirectoryOnly).Any(),
            $"Successful recovery after {crashPoint} must remove every linked transaction leaf.");
        Assert(
            recovered.AuditLog.Any(entry => entry.Operation.StartsWith("Recovery", StringComparison.Ordinal)),
            $"Recovery after {crashPoint} must emit an opaque recovery audit event.");
        storageEvents.AddRange(recovered.AuditLog);
    }

    var idempotenceCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using (var idempotent = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        idempotenceCapability))
    {
        var reread = idempotent.TryRead(relative, 16, CancellationToken.None);
        Assert(
            reread.State == AppStorageReadState.Read && reread.Bytes!.SequenceEqual(expectedBytes),
            "A second startup recovery pass must be a no-op with the same terminal bytes.");
        storageEvents.AddRange(idempotent.AuditLog);
    }

    capabilityEvents.AddRange(firstCapability.AuditLog);
    capabilityEvents.AddRange(recoveryCapability.AuditLog);
    capabilityEvents.AddRange(idempotenceCapability.AuditLog);
}

static void InterloperRecoveryIsPreserved(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    var armed = false;
    var probe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (armed && point == AppStorageInterleavingPoint.DirectoryDurableAfterTargetTombstone)
        {
            throw new AppStorageCrashSimulationException(point);
        }
    });
    var relative = MustPath("discovery-roots.json");
    using (var crashed = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability,
        probe))
    {
        Assert(crashed.TryAtomicReplace(relative, [0x41], CancellationToken.None).State == AppStorageMutationState.CommittedVerified,
            "The interloper recovery fixture must establish its old payload.");
        armed = true;
        AssertThrows<AppStorageCrashSimulationException>(
            () => _ = crashed.TryAtomicReplace(relative, [0x42], CancellationToken.None),
            "The interloper fixture must crash after the old target is durably displaced.");
    }

    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var payload = Path.Combine(appRoot, "discovery-roots.json");
    File.WriteAllBytes(payload, [0x66]);
    var before = CaptureTree(localAppData.RootPath);
    var recoveryCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var recovery = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        recoveryCapability);
    var read = recovery.TryRead(relative, 16, CancellationToken.None);
    Assert(
        read.State == AppStorageReadState.RecoveryRequired,
        "A target interloper beside a linked old/stage transaction must never become a clean read or Missing.");
    var store = new DiscoveryRootStore(recovery, new TestProtectedData(new byte[32]));
    _ = store.Load();
    Assert(
        store.LastDiagnostic?.Code == DiscoveryRootStoreDiagnosticCode.RecoveryRequired,
        "The public remembered-root Load diagnostic must expose a blocked recovery state.");
    Assert(
        recovery.TryDelete(relative, CancellationToken.None).State == AppStorageMutationState.RecoveryRequired,
        "Clear must not delete a target interloper while a linked transaction is ambiguous.");
    var after = CaptureTree(localAppData.RootPath);
    Assert(
        before.SequenceEqual(after),
        "Ambiguous recovery and Clear must preserve the interloper and every recovery artifact exactly.\n" +
        "Only before:\n" + string.Join("\n", before.Except(after)) + "\n" +
        "Only after:\n" + string.Join("\n", after.Except(before)));
    capabilityEvents.AddRange(capability.AuditLog);
    capabilityEvents.AddRange(recoveryCapability.AuditLog);
    storageEvents.AddRange(recovery.AuditLog);
}

static void MultipleRecoveryTransactionsAreAmbiguous(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    var armed = false;
    var probe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (armed && point == AppStorageInterleavingPoint.RecoveryManifestDurable)
        {
            throw new AppStorageCrashSimulationException(point);
        }
    });
    var relative = MustPath("discovery-roots.json");
    using (var crashed = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability,
        probe))
    {
        Assert(crashed.TryAtomicReplace(relative, [0x51], CancellationToken.None).State == AppStorageMutationState.CommittedVerified,
            "The multiple-transaction fixture must establish its old payload.");
        armed = true;
        AssertThrows<AppStorageCrashSimulationException>(
            () => _ = crashed.TryAtomicReplace(relative, [0x52], CancellationToken.None),
            "The multiple-transaction fixture must crash with a durable manifest.");
    }

    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var manifest = Directory.EnumerateFiles(appRoot, ".bf-*.txn", SearchOption.TopDirectoryOnly).Single();
    File.Copy(manifest, Path.Combine(appRoot, $".bf-{Guid.NewGuid():N}.txn"));
    var before = CaptureTree(localAppData.RootPath);
    var recoveryCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var recovery = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        recoveryCapability);
    Assert(
        recovery.TryRead(relative, 16, CancellationToken.None).State == AppStorageReadState.RecoveryRequired,
        "Multiple recovery manifests must be ambiguous rather than selected by enumeration order.");
    Assert(
        before.SequenceEqual(CaptureTree(localAppData.RootPath)),
        "Ambiguous multiple transactions must remain byte/identity/metadata unchanged.");
    capabilityEvents.AddRange(capability.AuditLog);
    capabilityEvents.AddRange(recoveryCapability.AuditLog);
    storageEvents.AddRange(recovery.AuditLog);
}

static void TamperedRecoveryContentIsRejected(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    var armed = false;
    var probe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (armed && point == AppStorageInterleavingPoint.RecoveryManifestDurable)
        {
            throw new AppStorageCrashSimulationException(point);
        }
    });
    var relative = MustPath("discovery-roots.json");
    using (var crashed = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability,
        probe))
    {
        Assert(crashed.TryAtomicReplace(relative, [0x61], CancellationToken.None).State == AppStorageMutationState.CommittedVerified,
            "The tamper fixture must establish its old payload.");
        armed = true;
        AssertThrows<AppStorageCrashSimulationException>(
            () => _ = crashed.TryAtomicReplace(relative, [0x62], CancellationToken.None),
            "The tamper fixture must crash before its first rename.");
    }

    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var staged = Directory.EnumerateFiles(appRoot, ".bf-*.tmp", SearchOption.TopDirectoryOnly).Single();
    File.WriteAllBytes(staged, [0x7F]);
    var before = CaptureTree(localAppData.RootPath);
    var recoveryCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var recovery = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        recoveryCapability);
    Assert(
        recovery.TryRead(relative, 16, CancellationToken.None).State == AppStorageReadState.RecoveryRequired,
        "A same-identity staged-content change must fail authenticated transaction linkage.");
    Assert(
        before.SequenceEqual(CaptureTree(localAppData.RootPath)),
        "Content-linkage rejection must not clean or mutate any transaction leaf.");
    capabilityEvents.AddRange(capability.AuditLog);
    capabilityEvents.AddRange(recoveryCapability.AuditLog);
    storageEvents.AddRange(recovery.AuditLog);
}

static void AbandonedGlobalMutexTriggersRecoveryScan(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
    var currentSid = identity.User?.Value ?? throw new InvalidOperationException("The abandoned mutex fixture requires a SID.");
    var name = SynchronizationNative.DeriveStorageMutexName(currentSid, localAppData.RootProof.PhysicalIdentity);
    using var abandoned = SynchronizationNative.CreateAbandonedMutex(
        name,
        $"D:P(A;;0x001F0001;;;SY)(A;;0x001F0001;;;{currentSid})");
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var recovery = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    Assert(recovery.IsAvailable, $"An abandoned but correctly secured mutex must recover safely: {recovery.LastDiagnostic}.");
    Assert(
        recovery.AuditLog.Any(entry => entry.Operation == "RecoveryScan" && entry.OpaqueObject == "abandoned-mutex"),
        "WAIT_ABANDONED must be surfaced as an explicit startup recovery trigger.");
    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(recovery.AuditLog);
}

static void GlobalStorageMutexIsCrossSessionAndAclBound(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
    var currentSid = identity.User?.Value ??
        throw new InvalidOperationException("The mutex fixture requires a current Windows user SID.");

    using (var wrongAclRoot = FixtureSandbox.Create())
    {
        fixtureRoots.Add(wrongAclRoot.RootPath);
        var name = SynchronizationNative.DeriveStorageMutexName(
            currentSid,
            wrongAclRoot.RootProof.PhysicalIdentity);
        Assert(
            name.StartsWith("Global\\BlockFerry.AppStorage.", StringComparison.Ordinal),
            "The storage serialization object must use the literal cross-session Global namespace.");
        using var hostile = SynchronizationNative.CreateMutex(
            name,
            "D:P(A;;0x001F0001;;;SY)(A;;0x001F0001;;;BU)");
        var capability = new AuditedFileSystemCapability([wrongAclRoot.RootProof]);
        using var rejected = new AppStorageGuard(
            new FakeEnvironmentPaths { LocalAppData = wrongAclRoot.RootPath },
            capability);
        Assert(
            !rejected.IsAvailable && rejected.LastDiagnostic?.Code == AppStorageDiagnosticCode.DaclRejected,
            "A pre-existing global mutex with a wrong principal must be rejected instead of reused.");
        Assert(
            !Directory.Exists(Path.Combine(wrongAclRoot.RootPath, "BlockFerry")),
            "Wrong-ACL synchronization rejection must happen before app-storage creation.");
        capabilityEvents.AddRange(capability.AuditLog);
        storageEvents.AddRange(rejected.AuditLog);
    }

    using (var wrongTypeRoot = FixtureSandbox.Create())
    {
        fixtureRoots.Add(wrongTypeRoot.RootPath);
        var name = SynchronizationNative.DeriveStorageMutexName(
            currentSid,
            wrongTypeRoot.RootProof.PhysicalIdentity);
        using var hostile = SynchronizationNative.CreateEvent(
            name,
            $"D:P(A;;0x001F0003;;;SY)(A;;0x001F0003;;;{currentSid})");
        var capability = new AuditedFileSystemCapability([wrongTypeRoot.RootProof]);
        using var rejected = new AppStorageGuard(
            new FakeEnvironmentPaths { LocalAppData = wrongTypeRoot.RootPath },
            capability);
        Assert(
            !rejected.IsAvailable && rejected.LastDiagnostic?.Code == AppStorageDiagnosticCode.IoFailure,
            "A same-name non-mutex kernel object must fail closed without a Local-namespace fallback.");
        Assert(
            !Directory.Exists(Path.Combine(wrongTypeRoot.RootPath, "BlockFerry")),
            "Synchronization-object creation failure must happen before app-storage creation.");
        capabilityEvents.AddRange(capability.AuditLog);
        storageEvents.AddRange(rejected.AuditLog);
    }

    using (var acceptedRoot = FixtureSandbox.Create())
    {
        fixtureRoots.Add(acceptedRoot.RootPath);
        var name = SynchronizationNative.DeriveStorageMutexName(
            currentSid,
            acceptedRoot.RootProof.PhysicalIdentity);
        var capability = new AuditedFileSystemCapability([acceptedRoot.RootProof]);
        using var accepted = new AppStorageGuard(
            new FakeEnvironmentPaths { LocalAppData = acceptedRoot.RootPath },
            capability);
        Assert(accepted.IsAvailable, $"A newly secured global mutex must establish storage: {accepted.LastDiagnostic}.");
        using var opened = SynchronizationNative.OpenMutexForSecurity(name);
        var dacl = SynchronizationNative.ReadDacl(opened);
        Assert(
            dacl.IsProtected &&
            dacl.Rules.Count == 2 &&
            dacl.Rules.All(rule => rule.AccessMask == SynchronizationNative.MutexAllAccess) &&
            dacl.Rules.Select(rule => rule.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(["S-1-5-18", currentSid]),
            "The live global mutex DACL must be protected and grant exactly MUTEX_ALL_ACCESS to SYSTEM and the current SID.");
        capabilityEvents.AddRange(capability.AuditLog);
        storageEvents.AddRange(accepted.AuditLog);
    }
}

static void PreAllocationBoundsAreEnforced(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    var protectedData = new RejectingProtectedData();
    var store = new DiscoveryRootStore(appStorage, protectedData);
    var mutableRoots = new List<string> { "C:\\first" };
    var detachedRoots = new RememberedDiscoveryRoots(1, mutableRoots, null, null);
    mutableRoots[0] = "C:\\changed";
    mutableRoots.Add("C:\\later");
    Assert(
        detachedRoots.ApprovedRoots.Count == 1 &&
        detachedRoots.ApprovedRoots[0] == "C:\\first",
        "Remembered roots must detach from public caller-owned input during construction.");
    AssertThrows<NotSupportedException>(
        () => ((IList<string>)detachedRoots.ApprovedRoots).Add("C:\\mutation"),
        "The detached remembered-root snapshot must expose an immutable collection.");
    var hostileRoots = new HostileReadOnlyList<string>(["C:\\approved"], 65);
    var mutationsBefore = appStorage.AuditLog.Count(entry => entry.IsMutation);

    AssertThrows<ArgumentOutOfRangeException>(
        () => store.Save(new RememberedDiscoveryRoots(1, hostileRoots, null, null)),
        "Raw roots must stop after observing maximum+1 items without Count, indexing, or materialization.");
    Assert(
        hostileRoots.CountAccesses == 0 &&
        hostileRoots.MoveNextCalls == 65 &&
        hostileRoots.SuccessfulObservations == 65,
        "Raw root acquisition must consume exactly maximum+1 hostile items before rejection.");
    Assert(
        protectedData.CallCount == 0 &&
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBefore,
        "Hostile raw root rejection must happen before protection or guarded storage mutation.");
    var hostileApprovals = new HostileReadOnlyList<ManualRootApprovalToken>([null!], 65);
    AssertThrows<ArgumentOutOfRangeException>(
        () => store.Save(RememberedDiscoveryRoots.Empty, hostileApprovals),
        "Raw approvals must stop after observing maximum+1 items without materialization.");
    Assert(
        hostileApprovals.CountAccesses == 0 && hostileApprovals.MoveNextCalls == 65,
        "Raw approval acquisition must consume exactly maximum+1 hostile items before rejection.");

    using var acquisitionCancellation = new CancellationTokenSource();
    var cancelingApprovals = new CancelingReadOnlyList<ManualRootApprovalToken>(
        [null!],
        acquisitionCancellation);
    AssertThrows<OperationCanceledException>(
        () => store.Save(
            RememberedDiscoveryRoots.Empty,
            cancelingApprovals,
            acquisitionCancellation.Token),
        "Cancellation during raw approval enumeration must stop before validation or mutation.");
    Assert(
        cancelingApprovals.MoveNextCalls == 1 && protectedData.CallCount == 0,
        "Canceled raw approval acquisition must not continue or reach protection.");

    const int payloadMaximum = 31;
    var fixedWriter = new FixedCapacityBufferWriter(payloadMaximum + 1);
    var first = fixedWriter.GetSpan(payloadMaximum + 1);
    first.Fill(0x41);
    fixedWriter.Advance(payloadMaximum + 1);
    Assert(
        fixedWriter.Capacity == payloadMaximum + 1 &&
        fixedWriter.WrittenCount == payloadMaximum + 1,
        "The production buffer writer must retain exactly the caller-provided maximum+1 capacity.");
    AssertThrows<PayloadLimitException>(
        () => fixedWriter.GetMemory(1),
        "The fixed-capacity writer must reject growth instead of allocating another buffer.");
    fixedWriter.Dispose();
    Assert(
        fixedWriter.WrittenSpan.ToArray().All(value => value == 0),
        "Disposing the fixed-capacity writer must zero its managed plaintext buffer.");
    var canonicalEmptyJson = Encoding.UTF8.GetBytes(
        "{\"schemaVersion\":1,\"approvedRoots\":[],\"lastSourceInstanceId\":null,\"lastTargetInstanceId\":null}");
    var exactPayload = DiscoveryRootPayloadCodec.Serialize(
        RememberedDiscoveryRoots.Empty,
        1024,
        CancellationToken.None);
    Assert(
        exactPayload.SequenceEqual(canonicalEmptyJson),
        "The bounded writer must accept the exact payload maximum.");
    using var writerCancellation = new CancellationTokenSource();
    var cancelingRoots = new CancelingReadOnlyList<string>(["C:\\approved"], writerCancellation);
    AssertThrows<OperationCanceledException>(
        () => DiscoveryRootPayloadCodec.Serialize(
            new RememberedDiscoveryRoots(1, cancelingRoots, null, null),
            1024,
            writerCancellation.Token),
        "Cancellation during JSON enumeration must stop before writing the observed string.");
    Assert(
        cancelingRoots.MoveNextCalls == 2,
        "Construction must finish its bounded detached snapshot, then the writer must observe cancellation before output.");

    const int nativeMaximum = 73;
    using var oversizedNative = new OversizedWindowsProtectedDataNative(nativeMaximum + 1);
    var boundedDpapi = new WindowsCurrentUserProtectedData(oversizedNative);
    AssertThrows<ProtectedDataLimitException>(
        () => boundedDpapi.Unprotect([0x01], [0x02], nativeMaximum),
        "DPAPI must reject native cbData over the caller bound before managed output allocation.");
    Assert(
        oversizedNative.ManagedCopyCount == 0 &&
        oversizedNative.ZeroCount == 1 &&
        oversizedNative.LastZeroLength == nativeMaximum + 1 &&
        oversizedNative.FreeCount == 1 &&
        oversizedNative.ZeroSequence < oversizedNative.FreeSequence,
        "An oversized native plaintext must be zeroed before LocalFree without a managed plaintext copy.");

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static void RenameAbiLayoutsAreLiteral()
{
    Assert(
        AppStorageRenameLayout.ForArchitecture(AppStorageNativeArchitecture.X86) ==
        new AppStorageRenameLayout(0, 4, 8, 12, 4),
        "The x86 FILE_RENAME_INFO_EX layout must use the literal 32-bit ABI offsets.");
    Assert(
        AppStorageRenameLayout.ForArchitecture(AppStorageNativeArchitecture.X64) ==
        new AppStorageRenameLayout(0, 8, 16, 20, 8),
        "The x64 FILE_RENAME_INFO_EX layout must use the literal 64-bit ABI offsets.");
    Assert(
        AppStorageRenameLayout.ForArchitecture(AppStorageNativeArchitecture.Arm64) ==
        new AppStorageRenameLayout(0, 8, 16, 20, 8),
        "The ARM64 FILE_RENAME_INFO_EX layout must use the literal 64-bit ABI offsets.");
}

static void RememberedRootsRoundTrip(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    using var selectedRoot = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    fixtureRoots.Add(selectedRoot.RootPath);

    var selectedMinecraft = selectedRoot.CreateDirectory(".minecraft");
    selectedRoot.CreateDirectory(".minecraft\\versions");
    var candidateCapability = new AuditedFileSystemCapability(
        [IssueNestedRootProof(selectedRoot, selectedMinecraft)]);
    var manualCandidate = new InstanceCandidateResolver(candidateCapability).ResolveManualSelection(
        selectedMinecraft,
        "manual fixture")
        .Single();

    var storageCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        storageCapability);
    Assert(appStorage.IsAvailable, "A normal local GUID LocalAppData fixture must establish guarded storage.");

    var protectedData = new WindowsCurrentUserProtectedData();
    var dpapiProbe = protectedData.Protect([0x71, 0x72], [0x21, 0x22], 1024);
    Assert(
        protectedData.Unprotect(dpapiProbe, [0x21, 0x22], 1024).SequenceEqual(new byte[] { 0x71, 0x72 }),
        "Windows DPAPI must round-trip under CurrentUser with matching application entropy.");
    AssertThrows<CryptographicException>(
        () => protectedData.Unprotect(dpapiProbe, [0x21, 0x23], 1024),
        "Windows DPAPI must reject the same ciphertext under different entropy.");
    var store = new DiscoveryRootStore(appStorage, protectedData);
    var approval = store.ApproveManualRoot(manualCandidate);
    Assert(approval is not null, "A handle-proven manual root must produce an opaque store-owned approval token.");

    var expected = new RememberedDiscoveryRoots(
        1,
        [selectedMinecraft],
        null,
        null);
    var saveResult = store.Save(expected, [approval!]);
    Assert(
        saveResult.State == AppStorageMutationState.CommittedVerified && store.LastDiagnostic is null,
        $"A normal protected save must persist; store={store.LastDiagnostic}; storage={appStorage.LastDiagnostic}.");

    var persistedPath = Path.Combine(localAppData.RootPath, "BlockFerry", "discovery-roots.json");
    var ciphertext = File.ReadAllBytes(persistedPath);
    Assert(ciphertext.Length > 0, "The guarded remembered-root payload must be persisted as ciphertext.");
    Assert(
        !ContainsUtf8(ciphertext, selectedMinecraft) &&
        !ContainsUtf8(ciphertext, "lastSourceInstanceId"),
        "DPAPI ciphertext must not contain plaintext roots or JSON field names.");

    var reloadedStore = new DiscoveryRootStore(appStorage, protectedData);
    var actual = reloadedStore.Load();
    Assert(actual.SchemaVersion == 1, "The remembered-root schema version must round-trip.");
    Assert(
        actual.ApprovedRoots.SequenceEqual([selectedMinecraft], StringComparer.OrdinalIgnoreCase),
        "Only the approved root must round-trip.");
    Assert(actual.LastSourceInstanceId is null && actual.LastTargetInstanceId is null, "Task 3 must persist no instance IDs before session-owned Task 4 approvals exist.");
    Assert(reloadedStore.LastDiagnostic is null, "A valid CurrentUser DPAPI payload must load without a diagnostic.");

    capabilityEvents.AddRange(storageCapability.AuditLog);
    capabilityEvents.AddRange(candidateCapability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static void CrossUserOrCorruptPayloadFailsClosed(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    var firstUser = new TestProtectedData(Enumerable.Repeat((byte)0x11, 32).ToArray());
    var otherUser = new TestProtectedData(Enumerable.Repeat((byte)0x22, 32).ToArray());
    var writer = new DiscoveryRootStore(appStorage, firstUser);
    writer.Save(RememberedDiscoveryRoots.Empty);

    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var beforeWrongUser = CaptureTree(appRoot);
    var mutationsBeforeWrongUser = appStorage.AuditLog.Count(entry => entry.IsMutation);
    var wrongUserStore = new DiscoveryRootStore(appStorage, otherUser);
    var wrongUser = wrongUserStore.Load();
    Assert(wrongUser == RememberedDiscoveryRoots.Empty, "A cross-user payload must fail closed to an empty in-memory value.");
    Assert(
        wrongUserStore.LastDiagnostic?.Code == DiscoveryRootStoreDiagnosticCode.ProtectedPayloadRejected,
        "A cross-user payload must expose only a structured protected-payload diagnostic.");
    Assert(CaptureTree(appRoot).SequenceEqual(beforeWrongUser), "A failed cross-user load must not mutate guarded storage.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBeforeWrongUser,
        "A failed cross-user load must record zero mutation events.");

    var payloadPath = Path.Combine(appRoot, "discovery-roots.json");
    File.WriteAllBytes(payloadPath, [0x01, 0x02, 0x03, 0x04]);
    var beforeCorrupt = CaptureTree(appRoot);
    var mutationsBeforeCorrupt = appStorage.AuditLog.Count(entry => entry.IsMutation);
    var corruptStore = new DiscoveryRootStore(appStorage, firstUser);
    var corrupt = corruptStore.Load();
    Assert(corrupt == RememberedDiscoveryRoots.Empty, "A corrupt payload must fail closed to an empty in-memory value.");
    Assert(
        corruptStore.LastDiagnostic?.Code == DiscoveryRootStoreDiagnosticCode.ProtectedPayloadRejected,
        "A corrupt payload must expose a structured diagnostic without plaintext.");
    Assert(CaptureTree(appRoot).SequenceEqual(beforeCorrupt), "A corrupt load must leave exact bytes, metadata, ACLs, and identities unchanged.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBeforeCorrupt,
        "A corrupt load must record zero mutation events.");

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static void ReparseAppStorageNeverWrites(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var target = localAppData.CreateDirectory("junction-target");
    localAppData.WriteBytes("junction-target\\sentinel.bin", [0x41, 0x42, 0x43]);
    var junction = Path.Combine(localAppData.RootPath, "BlockFerry");
    if (!localAppData.TryCreateDirectoryJunction(junction, target))
    {
        throw new InvalidOperationException("The app-storage reparse fixture could not be created.");
    }

    var beforeTarget = CaptureTree(target);
    var beforeLocalAppData = CaptureTree(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    Assert(!appStorage.IsAvailable, "A reparse-point BlockFerry app root must disable persistence.");
    Assert(
        appStorage.LastDiagnostic?.Code == AppStorageDiagnosticCode.ReparseRejected,
        "A reparse app root must return a structured in-memory-only diagnostic.");

    var store = new DiscoveryRootStore(appStorage, new TestProtectedData(new byte[32]));
    var mutationsBefore = appStorage.AuditLog.Count(entry => entry.IsMutation);
    store.Save(RememberedDiscoveryRoots.Empty);
    store.Clear();
    Assert(
        CaptureTree(target).SequenceEqual(beforeTarget),
        "Save/Clear through a parent junction must leave the reparse target's bytes, identities, attributes, ACLs, and mtimes exact.");
    Assert(
        CaptureTree(localAppData.RootPath).SequenceEqual(beforeLocalAppData),
        "A parent-junction rejection must leave the fixture LocalAppData tree exact.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBefore,
        "A rejected reparse app root must record zero mutation events.");
    Assert(
        store.LastDiagnostic?.Code == DiscoveryRootStoreDiagnosticCode.InMemoryOnly,
        "Unavailable guarded storage must remain explicitly in-memory only.");

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
    localAppData.DeleteLink(junction);

    var localRootTarget = localAppData.CreateDirectory("local-root-target");
    localAppData.WriteBytes("local-root-target\\sentinel.bin", [0x51, 0x52]);
    var localRootJunction = Path.Combine(localAppData.RootPath, "local-root-link");
    Assert(
        localAppData.TryCreateDirectoryJunction(localRootJunction, localRootTarget),
        "The LocalAppData-root reparse fixture must be created.");
    var beforeLocalRootTarget = CaptureTree(localRootTarget);
    var beforeLocalRootFixture = CaptureTree(localAppData.RootPath);
    using (var localRootGuard = new AppStorageGuard(
               new FakeEnvironmentPaths { LocalAppData = localRootJunction },
               new WindowsFileSystemCapability()))
    {
        Assert(!localRootGuard.IsAvailable, "A reparse-point LocalAppData root must disable persistence.");
        var localRootStore = new DiscoveryRootStore(localRootGuard, new TestProtectedData(new byte[32]));
        var localRootMutations = localRootGuard.AuditLog.Count(entry => entry.IsMutation);
        localRootStore.Save(RememberedDiscoveryRoots.Empty);
        localRootStore.Clear();
        Assert(
            CaptureTree(localRootTarget).SequenceEqual(beforeLocalRootTarget),
            "A reparse LocalAppData root must leave its target exact.");
        Assert(
            CaptureTree(localAppData.RootPath).SequenceEqual(beforeLocalRootFixture),
            "A reparse LocalAppData root rejection must leave the containing GUID fixture exact.");
        Assert(
            localRootGuard.AuditLog.Count(entry => entry.IsMutation) == localRootMutations,
            "A reparse LocalAppData root must record zero mutation events.");
        storageEvents.AddRange(localRootGuard.AuditLog);
    }

    localAppData.DeleteLink(localRootJunction);

    var leafCapability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using (var leafGuard = new AppStorageGuard(
               new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
               leafCapability))
    {
        Assert(leafGuard.IsAvailable, "The leaf-reparse fixture must begin with normal guarded storage.");
        var leafTarget = localAppData.CreateDirectory("leaf-target");
        localAppData.WriteBytes("leaf-target\\sentinel.bin", [0x61, 0x62]);
        var leafJunction = Path.Combine(
            localAppData.RootPath,
            "BlockFerry",
            "discovery-roots.json");
        Assert(
            localAppData.TryCreateDirectoryJunction(leafJunction, leafTarget),
            "The remembered-root leaf reparse fixture must be created.");
        var beforeLeafTarget = CaptureTree(leafTarget);
        var beforeLeafFixture = CaptureTree(localAppData.RootPath);
        var leafMutations = leafGuard.AuditLog.Count(entry => entry.IsMutation);
        var leafStore = new DiscoveryRootStore(leafGuard, new TestProtectedData(new byte[32]));
        leafStore.Save(RememberedDiscoveryRoots.Empty);
        Assert(
            leafGuard.LastDiagnostic?.Code == AppStorageDiagnosticCode.ReparseRejected,
            $"A reparse remembered-root leaf must fail closed; actual={leafGuard.LastDiagnostic}.");
        Assert(CaptureTree(leafTarget).SequenceEqual(beforeLeafTarget), "A reparse leaf target must remain exact.");
        Assert(
            CaptureTree(localAppData.RootPath).SequenceEqual(beforeLeafFixture),
            "A reparse leaf rejection must leave the fixture LocalAppData tree exact.");
        Assert(
            leafGuard.AuditLog.Count(entry => entry.IsMutation) == leafMutations,
            "A reparse leaf must be rejected before sibling-temp creation.");
        localAppData.DeleteLink(leafJunction);
        capabilityEvents.AddRange(leafCapability.AuditLog);
        storageEvents.AddRange(leafGuard.AuditLog);
    }
}

static void OversizeAndUnexpectedSchemaFailClosed(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    var store = new DiscoveryRootStore(appStorage, new TestProtectedData(new byte[32]));
    var mutationsBefore = appStorage.AuditLog.Count(entry => entry.IsMutation);

    AssertThrows<ArgumentOutOfRangeException>(
        () => store.Save(new RememberedDiscoveryRoots(2, [], null, null)),
        "An unexpected schema version must be rejected before persistence.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => store.Save(new RememberedDiscoveryRoots(
            1,
            Enumerable.Range(0, 65).Select(index => $"C:\\approved-{index}").ToArray(),
            null,
            null)),
        "More than 64 remembered roots must be rejected before persistence.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => store.Save(new RememberedDiscoveryRoots(1, ["C:\\" + new string('r', 4097)], null, null)),
        "A remembered root over 4096 UTF-16 units must be rejected before persistence.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBefore,
        "Schema/list/string validation failures must record zero mutations.");

    var expandingStore = new DiscoveryRootStore(
        appStorage,
        new ExpandingProtectedData(1024 * 1024 + 1));
    expandingStore.Save(RememberedDiscoveryRoots.Empty);
    Assert(
        expandingStore.LastDiagnostic?.Code == DiscoveryRootStoreDiagnosticCode.PayloadLimitExceeded,
        "Ciphertext over 1 MiB must fail closed with a structured limit diagnostic.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBefore,
        "Oversized ciphertext must be rejected before a storage mutation.");

    var boundedProtectedData = new TestProtectedData(new byte[32]);
    var boundedWriter = new DiscoveryRootStore(appStorage, boundedProtectedData);
    boundedWriter.Save(RememberedDiscoveryRoots.Empty);
    var payloadPath = Path.Combine(localAppData.RootPath, "BlockFerry", "discovery-roots.json");
    var entropy = Encoding.UTF8.GetBytes("BlockFerry/discovery-roots/schema-1/current-user");
    var unexpectedSchemaCiphertext = boundedProtectedData.Protect(
        Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2,\"approvedRoots\":[],\"lastSourceInstanceId\":null,\"lastTargetInstanceId\":null}"),
        entropy,
        1024 * 1024);
    File.WriteAllBytes(payloadPath, unexpectedSchemaCiphertext);
    var beforeUnexpectedSchema = CaptureTree(Path.GetDirectoryName(payloadPath)!);
    var mutationsBeforeUnexpectedSchema = appStorage.AuditLog.Count(entry => entry.IsMutation);
    var unexpectedSchemaStore = new DiscoveryRootStore(appStorage, boundedProtectedData);
    Assert(
        unexpectedSchemaStore.Load() == RememberedDiscoveryRoots.Empty &&
        unexpectedSchemaStore.LastDiagnostic?.Code == DiscoveryRootStoreDiagnosticCode.UnexpectedSchema,
        "An authenticated but unexpected JSON schema must fail closed.");
    Assert(
        CaptureTree(Path.GetDirectoryName(payloadPath)!).SequenceEqual(beforeUnexpectedSchema) &&
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBeforeUnexpectedSchema,
        "Unexpected-schema parsing must leave exact storage state and record zero mutations.");

    string[] malformedAuthenticatedPayloads =
    [
        "{\"schemaVersion\":1,\"schemaVersion\":1,\"approvedRoots\":[],\"lastSourceInstanceId\":null,\"lastTargetInstanceId\":null}",
        "{\"schemaVersion\":1,\"approvedRoots\":[],\"lastSourceInstanceId\":null,\"lastTargetInstanceId\":null,\"unknown\":0}",
        "{\"schemaVersion\":1,\"approvedRoots\":[],\"lastSourceInstanceId\":\"task-3-must-reject\",\"lastTargetInstanceId\":null}",
    ];
    foreach (var json in malformedAuthenticatedPayloads)
    {
        var malformedCiphertext = boundedProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            entropy,
            1024 * 1024);
        File.WriteAllBytes(payloadPath, malformedCiphertext);
        var malformedStore = new DiscoveryRootStore(appStorage, boundedProtectedData);
        Assert(
            malformedStore.Load() == RememberedDiscoveryRoots.Empty &&
            malformedStore.LastDiagnostic?.Code == DiscoveryRootStoreDiagnosticCode.MalformedPayload,
            "Duplicate, unknown, or Task-3-unauthorized authenticated JSON fields must fail closed as malformed.");
    }

    var mutationsBeforeOversizedPlaintext = appStorage.AuditLog.Count(entry => entry.IsMutation);
    var oversizedPlaintextStore = new DiscoveryRootStore(
        appStorage,
        new OversizedPlaintextProtectedData(256 * 1024 + 1));
    var oversizedPlaintext = oversizedPlaintextStore.Load();
    Assert(
        oversizedPlaintext == RememberedDiscoveryRoots.Empty &&
        oversizedPlaintextStore.LastDiagnostic?.Code == DiscoveryRootStoreDiagnosticCode.PayloadLimitExceeded,
        "DPAPI plaintext over 256 KiB must fail closed.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBeforeOversizedPlaintext,
        "Oversized plaintext parsing must record zero storage mutations.");

    File.WriteAllBytes(payloadPath, new byte[1024 * 1024 + 1]);
    var beforeOversizedCiphertext = CaptureTree(Path.GetDirectoryName(payloadPath)!);
    var mutationsBeforeOversizedCiphertext = appStorage.AuditLog.Count(entry => entry.IsMutation);
    var oversizedCiphertextStore = new DiscoveryRootStore(appStorage, boundedProtectedData);
    Assert(
        oversizedCiphertextStore.Load() == RememberedDiscoveryRoots.Empty &&
        oversizedCiphertextStore.LastDiagnostic?.Code == DiscoveryRootStoreDiagnosticCode.PayloadLimitExceeded,
        "An on-disk ciphertext payload over 1 MiB must be a bounded nonfatal read result.");
    Assert(
        CaptureTree(Path.GetDirectoryName(payloadPath)!).SequenceEqual(beforeOversizedCiphertext) &&
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBeforeOversizedCiphertext,
        "Oversized on-disk ciphertext must leave exact storage state and record zero mutations.");
    Assert(
        appStorage.IsAvailable,
        "An oversized leaf must not disable a still-proven app-storage capability.");
    var oversizedClear = oversizedCiphertextStore.Clear();
    Assert(
        oversizedClear.State == AppStorageMutationState.CommittedVerified &&
        !File.Exists(payloadPath),
        "Guarded Clear must remain available after a bounded oversized-ciphertext load; " +
        $"state={oversizedClear.State}; store={oversizedCiphertextStore.LastDiagnostic}; " +
        $"storage={appStorage.LastDiagnostic}; entries=" +
        string.Join(',', Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(payloadPath)!).Select(Path.GetFileName)) +
        "; audit=" + string.Join(',', appStorage.AuditLog.TakeLast(8).Select(entry => entry.Operation)) + ".");

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static void LegacyRootHardeningPreservesUnrelatedChildren(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var appRoot = Directory.CreateDirectory(Path.Combine(localAppData.RootPath, "BlockFerry"));
    var unrelated = Directory.CreateDirectory(Path.Combine(appRoot.FullName, "unrelated"));
    File.WriteAllBytes(Path.Combine(unrelated.FullName, "payload.bin"), [0x31, 0x32, 0x33]);
    var before = CaptureTree(unrelated.FullName);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);

    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    Assert(appStorage.IsAvailable,
        "An owned normal legacy app root must be available after non-propagating root hardening.");
    Assert(
        appStorage.AuditLog.Any(entry =>
            entry.Operation == "HardenLegacyDacl" &&
            entry.OpaqueObject == "app-root"),
        "The unrelated-child fixture must exercise legacy app-root DACL hardening.");
    var after = CaptureTree(unrelated.FullName);
    Assert(
        before.SequenceEqual(after),
        "Legacy app-root hardening must preserve every unrelated child property exactly.\n" +
        "Only before:\n" + string.Join("\n", before.Except(after)) + "\n" +
        "Only after:\n" + string.Join("\n", after.Except(before)));

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static void DaclDriftFailsBeforeMutation(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    Assert(appStorage.IsAvailable, "The DACL fixture must start with proven app storage.");
    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    AddWorldReadAce(appRoot);
    var before = CaptureTree(appRoot);
    var mutationsBefore = appStorage.AuditLog.Count(entry => entry.IsMutation);

    var store = new DiscoveryRootStore(appStorage, new TestProtectedData(new byte[32]));
    store.Save(RememberedDiscoveryRoots.Empty);
    Assert(
        appStorage.LastDiagnostic?.Code == AppStorageDiagnosticCode.DaclRejected,
        "A drifted app-root DACL must fail closed.");
    Assert(CaptureTree(appRoot).SequenceEqual(before), "DACL drift must be detected before any file or namespace mutation.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBefore,
        "DACL drift must record zero mutation events.");

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static void AppRootIdentityDriftFailsBeforeMutation(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    Assert(appStorage.IsAvailable, "The identity-drift fixture must start with proven app storage.");
    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var displaced = Path.Combine(localAppData.RootPath, "BlockFerry-displaced");
    Directory.Move(appRoot, displaced);
    Directory.CreateDirectory(appRoot);
    var beforeReplacement = CaptureTree(appRoot);
    var beforeDisplaced = CaptureTree(displaced);
    var mutationsBefore = appStorage.AuditLog.Count(entry => entry.IsMutation);

    var store = new DiscoveryRootStore(appStorage, new TestProtectedData(new byte[32]));
    store.Save(RememberedDiscoveryRoots.Empty);
    Assert(
        appStorage.LastDiagnostic?.Code == AppStorageDiagnosticCode.IdentityDrift,
        "An ordinary same-name app-root replacement must fail closed on full identity drift.");
    Assert(CaptureTree(appRoot).SequenceEqual(beforeReplacement), "The replacement app root must remain exact after rejection.");
    Assert(CaptureTree(displaced).SequenceEqual(beforeDisplaced), "The retained displaced app root must remain exact after rejection.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBefore,
        "App-root identity drift must be detected before any mutation event.");

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static void AtomicReplaceAndCancellationAreFailClosed(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    var protectedData = new TestProtectedData(Enumerable.Repeat((byte)0x33, 32).ToArray());
    var store = new DiscoveryRootStore(appStorage, protectedData);
    store.Save(RememberedDiscoveryRoots.Empty);
    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var payloadPath = Path.Combine(appRoot, "discovery-roots.json");
    var firstBytes = File.ReadAllBytes(payloadPath);
    var firstIdentity = SnapshotNative.GetIdentity(payloadPath, isDirectory: false);

    using var cancellation = new CancellationTokenSource();
    var cancelingStore = new DiscoveryRootStore(
        appStorage,
        new CancelingProtectedData(protectedData, cancellation));
    var mutationsBeforeCancel = appStorage.AuditLog.Count(entry => entry.IsMutation);
    var canceledSave = cancelingStore.Save(RememberedDiscoveryRoots.Empty, cancellation.Token);
    Assert(
        canceledSave.State == AppStorageMutationState.NotCommitted,
        "Cancellation after protection but before staging must return an explicit NotCommitted result.");
    Assert(File.ReadAllBytes(payloadPath).SequenceEqual(firstBytes), "A canceled save must preserve the exact prior ciphertext.");
    Assert(SnapshotNative.GetIdentity(payloadPath, false) == firstIdentity, "A canceled save must preserve the prior file identity.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBeforeCancel,
        "A canceled save must record zero mutation events.");

    store.Save(RememberedDiscoveryRoots.Empty);
    var secondBytes = File.ReadAllBytes(payloadPath);
    var secondIdentity = SnapshotNative.GetIdentity(payloadPath, isDirectory: false);
    Assert(
        !secondBytes.SequenceEqual(firstBytes),
        $"A second protected save must replace the ciphertext; store={store.LastDiagnostic}; storage={appStorage.LastDiagnostic}.");
    Assert(secondIdentity != firstIdentity, "Atomic sibling replacement must install the staged file object, not rewrite in place.");
    Assert(
        Directory.EnumerateFileSystemEntries(appRoot).Select(Path.GetFileName).SequenceEqual(["discovery-roots.json"]),
        "Successful atomic replacement must leave no sibling temporary file.");
    Assert(store.Load() == RememberedDiscoveryRoots.Empty, "The atomically replaced ciphertext must load successfully.");

    store.Clear();
    Assert(
        !File.Exists(payloadPath),
        $"Clear must remove only the guarded remembered-root leaf; store={store.LastDiagnostic}; storage={appStorage.LastDiagnostic}.");

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static void NamespaceInterleavingsRetainExclusiveObjects(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var payloadPath = Path.Combine(appRoot, "discovery-roots.json");
    var movedPath = Path.Combine(appRoot, "race-moved.bin");
    var stageWriteOpened = false;
    var stageMoveSucceeded = false;
    var targetWriteOpened = false;
    var targetMoveSucceeded = false;
    var clearMoveSucceeded = false;
    var probe = new AppStorageInterleavingProbe((point, context) =>
    {
        var path = Path.Combine(appRoot, context.RelativeName);
        if (point == AppStorageInterleavingPoint.StageCreated)
        {
            stageWriteOpened = CanOpenForExternalWrite(path);
            stageMoveSucceeded = CanMoveAndRestore(path, movedPath);
        }
        else if (point == AppStorageInterleavingPoint.BeforeCommitRename && File.Exists(payloadPath))
        {
            targetWriteOpened = CanOpenForExternalWrite(payloadPath);
            targetMoveSucceeded = CanMoveAndRestore(payloadPath, movedPath);
        }
        else if (point == AppStorageInterleavingPoint.BeforeClearTombstoneRename)
        {
            clearMoveSucceeded = CanMoveAndRestore(payloadPath, movedPath);
        }
    });

    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability,
        probe);
    var path = MustPath("discovery-roots.json");
    Assert(
        appStorage.TryAtomicReplace(path, [0x11], CancellationToken.None).State ==
        AppStorageMutationState.CommittedVerified,
        "The missing-leaf branch must establish the initial guarded payload.");
    Assert(
        appStorage.TryAtomicReplace(path, [0x22], CancellationToken.None).State ==
        AppStorageMutationState.CommittedVerified,
        "The existing-leaf branch must commit through the retained staged and target handles.");
    Assert(
        appStorage.TryDelete(path, CancellationToken.None).State ==
        AppStorageMutationState.CommittedVerified,
        "Clear must commit through an exclusively retained target handle.");

    Assert(!stageWriteOpened, "The retained staging handle must deny an external write open.");
    Assert(!stageMoveSucceeded, "The retained staging handle must deny an external namespace move.");
    Assert(!targetWriteOpened, "The retained replacement target must deny an external write open.");
    Assert(!targetMoveSucceeded, "The retained replacement target must deny an external namespace move.");
    Assert(!clearMoveSucceeded, "Clear must deny moving the retained leaf before its tombstone rename.");
    Assert(!File.Exists(payloadPath), "Clear must leave the guarded payload name absent.");
    Assert(
        !Directory.EnumerateFileSystemEntries(appRoot).Any(),
        "The contained namespace-race probes must leave no stage, moved leaf, or tombstone.");

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static void DeleteDispositionRequiresNamespaceVerification(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var guard = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    var relative = MustPath("discovery-roots.json");
    Assert(
        guard.TryAtomicReplace(relative, [0x81], CancellationToken.None).State ==
        AppStorageMutationState.CommittedVerified,
        "The compatible-reader deletion fixture must establish an initial payload.");
    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var payload = Path.Combine(appRoot, "discovery-roots.json");
    using var compatibleReader = new FileStream(
        payload,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    var auditBefore = guard.AuditLog.Count;

    var pending = guard.TryDelete(relative, CancellationToken.None);
    Assert(
        pending.State == AppStorageMutationState.RecoveryRequired,
        "A delete disposition whose namespace leaf remains held by a compatible reader must require recovery.");
    var pendingAudit = guard.AuditLog.Skip(auditBefore).ToArray();
    Assert(
        pendingAudit.Any(entry =>
            entry.Operation == "DeleteDispositionSet" &&
            entry.IsMutation &&
            !entry.WasCommitted) &&
        pendingAudit.All(entry => entry.Operation != "DeletionVerified"),
        "The audit must record delete disposition without claiming namespace deletion verification.");
    var pendingNames = Directory.EnumerateFileSystemEntries(appRoot)
        .Select(Path.GetFileName)
        .ToArray();
    Assert(
        pendingNames.Count(name => name?.EndsWith(".txn", StringComparison.Ordinal) == true) == 1 &&
        pendingNames.Count(name => name?.EndsWith(".clear", StringComparison.Ordinal) == true) == 1,
        "RecoveryRequired must retain the authenticated manifest and compatible-reader tombstone.");

    compatibleReader.Dispose();
    var recovered = guard.TryDelete(relative, CancellationToken.None);
    Assert(
        recovered.State == AppStorageMutationState.CommittedVerified,
        "After the compatible reader closes, guarded recovery must verify namespace absence and finish Clear.");
    Assert(
        guard.AuditLog.Skip(auditBefore).Any(entry =>
            entry.Operation == "DeletionVerified" &&
            entry.IsMutation &&
            entry.WasCommitted),
        "A successful retry must audit namespace deletion verification separately from disposition.");
    Assert(
        !Directory.EnumerateFileSystemEntries(appRoot).Any(),
        "A recovered Clear must leave neither DPAPI manifest nor data tombstone.");

    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(guard.AuditLog);
}

static bool CanOpenForExternalWrite(string path)
{
    try
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        return true;
    }
    catch (IOException)
    {
        return false;
    }
}

static bool CanMoveAndRestore(string path, string movedPath)
{
    try
    {
        File.Move(path, movedPath);
        File.Move(movedPath, path);
        return true;
    }
    catch (IOException)
    {
        return false;
    }
}

static void CommitStageCancellationAndFaultsHaveExplicitOutcomes(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    var preCommitPoints = new[]
    {
        AppStorageInterleavingPoint.TargetRetainedDurable,
        AppStorageInterleavingPoint.StageCreated,
        AppStorageInterleavingPoint.StageDurable,
        AppStorageInterleavingPoint.BeforeCommitRename,
    };
    var postCommitPoints = new[]
    {
        AppStorageInterleavingPoint.AfterTargetTombstoneRename,
        AppStorageInterleavingPoint.DirectoryDurableAfterTargetTombstone,
        AppStorageInterleavingPoint.AfterCommitRename,
        AppStorageInterleavingPoint.DirectoryDurableAfterCommit,
        AppStorageInterleavingPoint.FinalDurable,
    };

    foreach (var point in preCommitPoints.Concat(postCommitPoints))
    {
        RunReplaceInterruption(point, cancel: false, fixtureRoots, capabilityEvents, storageEvents);
        RunReplaceInterruption(point, cancel: true, fixtureRoots, capabilityEvents, storageEvents);
    }

    RunReplaceInterruption(
        AppStorageInterleavingPoint.OldTombstoneDeleted,
        cancel: false,
        fixtureRoots,
        capabilityEvents,
        storageEvents);
}

static void RunReplaceInterruption(
    AppStorageInterleavingPoint interruptedPoint,
    bool cancel,
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var cancellation = new CancellationTokenSource();
    var armed = false;
    var probe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (!armed || point != interruptedPoint)
        {
            return;
        }

        if (cancel)
        {
            cancellation.Cancel();
            return;
        }

        throw new IOException("Injected deterministic app-storage fault.");
    });
    using var guard = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability,
        probe);
    var relative = MustPath("discovery-roots.json");
    Assert(
        guard.TryAtomicReplace(relative, [0x31], CancellationToken.None).State ==
        AppStorageMutationState.CommittedVerified,
        "The interruption fixture must establish an existing target first.");
    var payload = Path.Combine(localAppData.RootPath, "BlockFerry", "discovery-roots.json");
    var oldIdentity = SnapshotNative.GetIdentity(payload, false);
    armed = true;
    var result = guard.TryAtomicReplace(relative, [0x42], cancellation.Token);

    var afterCommitPoint = interruptedPoint is
        AppStorageInterleavingPoint.AfterTargetTombstoneRename or
        AppStorageInterleavingPoint.DirectoryDurableAfterTargetTombstone or
        AppStorageInterleavingPoint.AfterCommitRename or
        AppStorageInterleavingPoint.DirectoryDurableAfterCommit or
        AppStorageInterleavingPoint.FinalDurable or
        AppStorageInterleavingPoint.OldTombstoneDeleted;
    var expectedCommitted = cancel && afterCommitPoint ||
        interruptedPoint == AppStorageInterleavingPoint.OldTombstoneDeleted;
    Assert(
        result.State == (expectedCommitted
            ? AppStorageMutationState.CommittedVerified
            : AppStorageMutationState.NotCommitted),
        $"{interruptedPoint} {(cancel ? "cancellation" : "fault")} must have an explicit, exact commit outcome; actual={result.State}.");
    Assert(
        File.ReadAllBytes(payload).SequenceEqual(expectedCommitted ? new byte[] { 0x42 } : [0x31]),
        $"{interruptedPoint} must leave bytes matching its explicit commit outcome.");
    if (!expectedCommitted)
    {
        Assert(
            SnapshotNative.GetIdentity(payload, false) == oldIdentity,
            $"{interruptedPoint} rollback must restore the exact old target identity.");
    }

    Assert(
        Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(payload)!)
            .Select(Path.GetFileName)
            .SequenceEqual(["discovery-roots.json"]),
        $"{interruptedPoint} must leave no stage, old, failed, or clear tombstone after a proved terminal result.");
    var deletionLifecycle = guard.AuditLog
        .Where(entry => entry.Operation is "DeleteDispositionSet" or "DeletionVerified")
        .ToArray();
    Assert(
        deletionLifecycle.Any(entry => entry.Operation == "DeleteDispositionSet") &&
        deletionLifecycle
            .Where(entry => entry.Operation == "DeleteDispositionSet")
            .All(entry => entry.IsMutation && !entry.WasCommitted) &&
        deletionLifecycle
            .Where(entry => entry.Operation == "DeletionVerified")
            .All(entry => entry.IsMutation && entry.WasCommitted),
        $"{interruptedPoint} cleanup audit must distinguish pending disposition from verified namespace deletion.");
    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(guard.AuditLog);
}

static void InterloperAfterTargetTombstoneRequiresRecovery(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var payload = Path.Combine(appRoot, "discovery-roots.json");
    var armed = false;
    var probe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (armed && point == AppStorageInterleavingPoint.AfterTargetTombstoneRename)
        {
            File.WriteAllBytes(payload, [0x77]);
        }
    });
    using var guard = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability,
        probe);
    var relative = MustPath("discovery-roots.json");
    Assert(
        guard.TryAtomicReplace(relative, [0x51], CancellationToken.None).State ==
        AppStorageMutationState.CommittedVerified,
        "The interloper fixture must establish the old retained target.");
    var oldIdentity = SnapshotNative.GetIdentity(payload, false);
    armed = true;
    var result = guard.TryAtomicReplace(relative, [0x62], CancellationToken.None);

    Assert(
        result.State == AppStorageMutationState.RecoveryRequired,
        "An interloper that occupies target after the old tombstone rename must require recovery.");
    Assert(File.ReadAllBytes(payload).SequenceEqual(new byte[] { 0x77 }), "Recovery must never overwrite the interloper target.");
    var oldTombstones = Directory.EnumerateFiles(appRoot, ".bf-*.old").ToArray();
    var stages = Directory.EnumerateFiles(appRoot, ".bf-*.tmp").ToArray();
    Assert(oldTombstones.Length == 1 && stages.Length == 1, "RecoveryRequired must preserve one old tombstone and one staged payload.");
    Assert(File.ReadAllBytes(oldTombstones[0]).SequenceEqual(new byte[] { 0x51 }), "The recovery tombstone must preserve exact old bytes.");
    Assert(SnapshotNative.GetIdentity(oldTombstones[0], false) == oldIdentity, "The recovery tombstone must retain the exact old identity.");
    Assert(File.ReadAllBytes(stages[0]).SequenceEqual(new byte[] { 0x62 }), "The recovery stage must preserve exact proposed bytes.");
    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(guard.AuditLog);
}

static void ClearStageCancellationAndFaultsHaveExplicitOutcomes(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    var points = new[]
    {
        AppStorageInterleavingPoint.BeforeClearTombstoneRename,
        AppStorageInterleavingPoint.AfterClearTombstoneRename,
        AppStorageInterleavingPoint.ClearDirectoryDurableAfterTombstone,
        AppStorageInterleavingPoint.BeforeClearDelete,
        AppStorageInterleavingPoint.AfterClearDelete,
        AppStorageInterleavingPoint.ClearDirectoryDurableAfterDelete,
    };
    foreach (var point in points)
    {
        RunClearInterruption(point, cancel: false, fixtureRoots, capabilityEvents, storageEvents);
        RunClearInterruption(point, cancel: true, fixtureRoots, capabilityEvents, storageEvents);
    }
}

static void RunClearInterruption(
    AppStorageInterleavingPoint interruptedPoint,
    bool cancel,
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var cancellation = new CancellationTokenSource();
    var armed = false;
    var probe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (!armed || point != interruptedPoint)
        {
            return;
        }

        if (cancel)
        {
            cancellation.Cancel();
        }
        else
        {
            throw new IOException("Injected deterministic clear fault.");
        }
    });
    using var guard = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability,
        probe);
    var relative = MustPath("discovery-roots.json");
    Assert(
        guard.TryAtomicReplace(relative, [0x71], CancellationToken.None).State ==
        AppStorageMutationState.CommittedVerified,
        "The clear interruption fixture must establish an initial payload.");
    var appRoot = Path.Combine(localAppData.RootPath, "BlockFerry");
    var payload = Path.Combine(appRoot, "discovery-roots.json");
    var oldIdentity = SnapshotNative.GetIdentity(payload, false);
    armed = true;
    var result = guard.TryDelete(relative, cancellation.Token);
    var afterRename = interruptedPoint != AppStorageInterleavingPoint.BeforeClearTombstoneRename;
    var expectedRecovery = false;
    var expectedCommitted = cancel && afterRename ||
        interruptedPoint == AppStorageInterleavingPoint.AfterClearDelete ||
        interruptedPoint == AppStorageInterleavingPoint.ClearDirectoryDurableAfterDelete;
    var expectedState = expectedRecovery
        ? AppStorageMutationState.RecoveryRequired
        : expectedCommitted
            ? AppStorageMutationState.CommittedVerified
            : AppStorageMutationState.NotCommitted;
    Assert(
        result.State == expectedState,
        $"Clear {interruptedPoint} {(cancel ? "cancellation" : "fault")} must have an exact terminal outcome; actual={result.State}.");
    Assert(
        File.Exists(payload) == (!expectedCommitted && !expectedRecovery),
        $"Clear {interruptedPoint} namespace state must match the explicit result.");
    if (!expectedCommitted && !expectedRecovery)
    {
        Assert(File.ReadAllBytes(payload).SequenceEqual(new byte[] { 0x71 }), "A noncommitted Clear must preserve old bytes.");
        Assert(SnapshotNative.GetIdentity(payload, false) == oldIdentity, "A noncommitted Clear must restore the old identity.");
    }

    Assert(
        Directory.EnumerateFileSystemEntries(appRoot).Count() ==
        (expectedCommitted || expectedRecovery ? 0 : 1),
        "A terminal Clear result must leave no tombstone or unrelated namespace entry.");
    capabilityEvents.AddRange(capability.AuditLog);
    storageEvents.AddRange(guard.AuditLog);
}

static void ConcurrentGuardsSerializeMutations(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    using var localAppData = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    var capabilityOne = new AuditedFileSystemCapability([localAppData.RootProof]);
    var capabilityTwo = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var firstEntered = new ManualResetEventSlim();
    using var releaseFirst = new ManualResetEventSlim();
    using var secondEntered = new ManualResetEventSlim();
    var firstArmed = false;
    var firstProbe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (firstArmed && point == AppStorageInterleavingPoint.BeforeCommitRename)
        {
            firstEntered.Set();
            Assert(releaseFirst.Wait(TimeSpan.FromSeconds(5)), "The serialized first mutation timed out.");
        }
    });
    var secondProbe = new AppStorageInterleavingProbe((point, _) =>
    {
        if (point == AppStorageInterleavingPoint.StageCreated)
        {
            secondEntered.Set();
        }
    });
    using var guardOne = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capabilityOne,
        firstProbe);
    using var guardTwo = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capabilityTwo,
        secondProbe);
    var relative = MustPath("discovery-roots.json");
    Assert(
        guardOne.TryAtomicReplace(relative, [0x11], CancellationToken.None).State ==
        AppStorageMutationState.CommittedVerified,
        "The serialization fixture must establish its initial payload.");
    firstArmed = true;
    var first = Task.Run(() => guardOne.TryAtomicReplace(relative, [0x22], CancellationToken.None));
    Assert(firstEntered.Wait(TimeSpan.FromSeconds(5)), "The first serialized mutation did not reach its commit seam.");
    var second = Task.Run(() => guardTwo.TryAtomicReplace(relative, [0x33], CancellationToken.None));
    Assert(!secondEntered.Wait(TimeSpan.FromMilliseconds(250)), "A second guard must not stage while the current-user mutex is held.");
    releaseFirst.Set();
    Assert(Task.WaitAll([first, second], TimeSpan.FromSeconds(10)), "Serialized guard mutations timed out.");
    Assert(
        first.Result.State == AppStorageMutationState.CommittedVerified &&
        second.Result.State == AppStorageMutationState.CommittedVerified,
        "Both serialized mutations must reach proved terminal results.");
    Assert(
        File.ReadAllBytes(Path.Combine(localAppData.RootPath, "BlockFerry", "discovery-roots.json")).SequenceEqual(new byte[] { 0x33 }),
        "The second serialized mutation must commit after the first releases the mutex.");
    capabilityEvents.AddRange(capabilityOne.AuditLog);
    capabilityEvents.AddRange(capabilityTwo.AuditLog);
    storageEvents.AddRange(guardOne.AuditLog);
    storageEvents.AddRange(guardTwo.AuditLog);
}

static void OnlyCurrentProvenManualRootsAndInstancesCanPersist(
    ICollection<string> fixtureRoots,
    List<CapabilityAuditEvent> capabilityEvents,
    List<AppStorageAuditEvent> storageEvents)
{
    var publicManualAuthorityMethods = typeof(InstanceCandidateResolver)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(method => method.Name.StartsWith("ResolveManualSelection", StringComparison.Ordinal))
        .ToArray();
    Assert(
        publicManualAuthorityMethods.Length == 0,
        "A public string-path resolver must not be able to mint manual-selection persistence authority.");

    using var localAppData = FixtureSandbox.Create();
    using var selectedRoot = FixtureSandbox.Create();
    fixtureRoots.Add(localAppData.RootPath);
    fixtureRoots.Add(selectedRoot.RootPath);
    var selectedMinecraft = selectedRoot.CreateDirectory(".minecraft");
    selectedRoot.CreateDirectory(".minecraft\\versions");
    var rootCapability = new AuditedFileSystemCapability([IssueNestedRootProof(selectedRoot, selectedMinecraft)]);
    var firstResolver = new InstanceCandidateResolver(rootCapability);
    var provenManual = firstResolver.ResolveManualSelection(selectedMinecraft, "manual fixture")
        .Single();
    var manualDisguisedAsAutomatic = provenManual with { Origin = Pcl2CandidateOrigin.Automatic };
    var resolvedAutomatic = firstResolver.Resolve(
        new DiscoveryCandidate(selectedMinecraft, Pcl2CandidateOrigin.Automatic, "automatic fixture"))
        .Single();
    var automaticDisguisedAsManual = resolvedAutomatic with { Origin = Pcl2CandidateOrigin.Manual };
    var unprovenManual = new Pcl2RootCandidate(selectedMinecraft, Pcl2CandidateOrigin.Manual);

    var capability = new AuditedFileSystemCapability([localAppData.RootProof]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData.RootPath },
        capability);
    var protectedData = new TestProtectedData(new byte[32]);
    var store = new DiscoveryRootStore(appStorage, protectedData);
    Assert(
        store.ApproveManualRoot(automaticDisguisedAsManual) is null,
        "Changing an automatic candidate's public Origin to Manual must not create persistence authority.");
    Assert(
        store.ApproveManualRoot(manualDisguisedAsAutomatic) is not null,
        "Changing a manually selected candidate's public Origin must not erase its internal manual-selection authority.");
    Assert(store.ApproveManualRoot(unprovenManual) is null, "A manual path without a current capability proof must not gain persistence approval.");

    var secondResolver = new InstanceCandidateResolver(rootCapability);
    var secondAutomatic = secondResolver.Resolve(
        new DiscoveryCandidate(selectedMinecraft, Pcl2CandidateOrigin.Automatic, "second automatic fixture"))
        .Single();
    var crossResolverSplice = secondAutomatic with
    {
        ResolvedAccess = secondAutomatic.ResolvedAccess! with
        {
            ManualSelectionProvenance = provenManual.ResolvedAccess!.ManualSelectionProvenance,
        },
    };
    Assert(
        store.ApproveManualRoot(crossResolverSplice) is null,
        "Manual-selection provenance from one resolver must not authorize another resolver's resolved access.");
    var mutationsBefore = appStorage.AuditLog.Count(entry => entry.IsMutation);
    AssertThrows<InvalidOperationException>(
        () => store.Save(new RememberedDiscoveryRoots(1, [selectedMinecraft], null, null)),
        "An unapproved root must not be persisted.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBefore,
        "Approval rejection must happen before storage mutation.");

    var first = store.ApproveManualRoot(provenManual);
    var sameGeneration = store.ApproveManualRoot(provenManual);
    Assert(first is not null && sameGeneration is not null, "A current handle-proven manual candidate must receive opaque generation-bound tokens.");
    var otherStore = new DiscoveryRootStore(appStorage, protectedData);
    AssertThrows<InvalidOperationException>(
        () => otherStore.Save(new RememberedDiscoveryRoots(1, [selectedMinecraft], null, null), [first!]),
        "An approval token must be rejected by another store even for the same root and guard.");
    var approvedValue = new RememberedDiscoveryRoots(1, [selectedMinecraft], null, null);
    Assert(
        store.Save(approvedValue, [first!]).State == AppStorageMutationState.CommittedVerified,
        "The issuing store must accept a current opaque token exactly once.");
    AssertThrows<InvalidOperationException>(
        () => store.Save(approvedValue, [first!]),
        "A consumed approval token must not replay.");
    AssertThrows<InvalidOperationException>(
        () => store.Save(approvedValue, [sameGeneration!]),
        "A token from the prior store generation must be rejected even when it was not presented before.");

    var forgedInstance = CreateInstance("forged-id", selectedMinecraft);
    AssertThrows<InvalidOperationException>(
        () => store.Save(new RememberedDiscoveryRoots(1, [], forgedInstance.Id, null)),
        "Task 3 must reject every non-null last-used ID until Task 4 provides session-owned approvals.");

    var identityToken = store.ApproveManualRoot(provenManual);
    Assert(identityToken is not null, "The current generation must issue a fresh token before identity drift.");
    var displacedRoot = selectedMinecraft + "-displaced";
    Directory.Move(selectedMinecraft, displacedRoot);
    Directory.CreateDirectory(selectedMinecraft);
    var mutationsBeforeIdentityDrift = appStorage.AuditLog.Count(entry => entry.IsMutation);
    AssertThrows<InvalidOperationException>(
        () => store.Save(approvedValue, [identityToken!]),
        "Save must immediately reopen and reject a manual root whose full physical identity drifted.");
    Assert(
        appStorage.AuditLog.Count(entry => entry.IsMutation) == mutationsBeforeIdentityDrift,
        "Approval identity drift must fail before any app-storage mutation.");
    var diagnosticText = string.Join(
        '|',
        appStorage.AuditLog.Select(entry => entry.Operation + ":" + entry.OpaqueObject)
            .Append(store.LastDiagnostic?.Message ?? string.Empty));
    Assert(
        !diagnosticText.Contains(selectedMinecraft, StringComparison.OrdinalIgnoreCase),
        "Storage audit and diagnostics must not contain plaintext approved roots.");

    capabilityEvents.AddRange(capability.AuditLog);
    capabilityEvents.AddRange(rootCapability.AuditLog);
    storageEvents.AddRange(appStorage.AuditLog);
}

static Pcl2Instance CreateInstance(string id, string gameRoot) =>
    new(
        id,
        id,
        Path.GetDirectoryName(gameRoot) ?? gameRoot,
        gameRoot,
        gameRoot,
        null,
        Path.Combine(gameRoot, "PCL", "Setup.ini"),
        Pcl2IsolationMode.Isolated,
        "1.21.1",
        [],
        new Pcl2ModpackIdentity("fixture", null, Pcl2IdentityConfidence.High, Pcl2IdentitySource.InstanceJson, "fixture"),
        true,
        false,
        []);

static bool ContainsUtf8(byte[] haystack, string needle)
{
    var bytes = Encoding.UTF8.GetBytes(needle);
    return haystack.AsSpan().IndexOf(bytes) >= 0;
}

#pragma warning disable CA1416
static IReadOnlyList<TreeSnapshotEntry> CaptureTree(string root)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("The app-storage fixture requires Windows ACLs.");
    }

    var paths = Directory.Exists(root)
        ? new[] { root }.Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        : [];
    const AccessControlSections snapshotSections =
        AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
    return paths
        .Select(path =>
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var relative = Path.GetRelativePath(root, path);
            FileSystemSecurity security = isDirectory
                ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), snapshotSections)
                : FileSystemAclExtensions.GetAccessControl(new FileInfo(path), snapshotSections);
            return new TreeSnapshotEntry(
                relative,
                isDirectory,
                isDirectory ? string.Empty : Convert.ToBase64String(File.ReadAllBytes(path)),
                attributes,
                File.GetLastWriteTimeUtc(path).Ticks,
                security.GetSecurityDescriptorSddlForm(snapshotSections),
                SnapshotNative.GetIdentity(path, isDirectory));
        })
        .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
        .ToArray();
}

static void AddWorldReadAce(string directoryPath)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("The app-storage fixture requires Windows ACLs.");
    }

    var directory = new DirectoryInfo(directoryPath);
    var security = FileSystemAclExtensions.GetAccessControl(directory, AccessControlSections.Access);
    security.AddAccessRule(new FileSystemAccessRule(
        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        FileSystemRights.ReadAndExecute,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AccessControlType.Allow));
    FileSystemAclExtensions.SetAccessControl(directory, security);
}
#pragma warning restore CA1416

static void RunDiscoveryCase()
{
    var fixtureRoots = new List<string>();
    var auditEvents = new List<CapabilityAuditEvent>();

    ProviderRawInputsAreBoundedBeforeFiltering(fixtureRoots, auditEvents);
    PclRawInputsAreBoundedBeforeFiltering(fixtureRoots, auditEvents);
    AutomaticCandidatesAreBoundedDeterministicAndPure(fixtureRoots, auditEvents);
    ProviderPreWorkLimitsBoundCapabilityCalls(fixtureRoots, auditEvents);
    ShortcutTargetKindComesFromBoundedHeader(fixtureRoots, auditEvents);
    DiagnosticsAreRequestLocalAndSanitized(fixtureRoots, auditEvents);
    FourSelectionLevelsResolveThroughOwnedHandles(fixtureRoots, auditEvents);
    RetainedMinecraftRootSurvivesOrdinaryRenameReplacement(fixtureRoots, auditEvents);
    PclDiscoveryBudgetsBoundCapabilityCalls(fixtureRoots, auditEvents);
    InjectedPclReadsRemainCapabilityBound(fixtureRoots, auditEvents);

    var summary = CapabilityAuditSummary.From(auditEvents);
    Assert(summary.EventCount > 0, "Discovery tests must produce an access audit.");
    Assert(summary.WriteCount == 0, "Discovery, metadata, isolation, and preview must issue zero writes.");
    Assert(summary.RealRootAccessCount == 0, "Accepted discovery operations must always carry a fixture root proof.");

    Console.WriteLine(
        "AUDIT: fixture-roots=" +
        string.Join('|', fixtureRoots.OrderBy(path => path, StringComparer.Ordinal)));
    Console.WriteLine(
        $"AUDIT: events={summary.EventCount}; writes={summary.WriteCount}; " +
        $"real-root-access={summary.RealRootAccessCount}");
    Console.WriteLine("PASS: discovery");
}

static void PclRawInputsAreBoundedBeforeFiltering(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);
    var candidateRoot = sandbox.CreateGuidDirectory();
    var candidateProof = sandbox.GetRootProof(candidateRoot);
    var candidate = new Pcl2RootCandidate(candidateRoot, Pcl2CandidateOrigin.Manual);

    var rawCandidates = new HostileReadOnlyList<Pcl2RootCandidate>(
        [candidate, null!, candidate, candidate],
        successfulObservationsBeforeThrow: 65);
    var rawCapability = new AuditedFileSystemCapability([candidateProof]);
    Pcl2DiscoveryResult? rawResult = null;
    Exception? rawFailure = null;
    try
    {
        rawResult = new Pcl2InstanceDiscovery(rawCapability).Discover(
            new Pcl2DiscoveryRequest(rawCandidates));
    }
    catch (Exception exception)
    {
        rawFailure = exception;
    }

    string[] manualInputs =
    [
        candidateRoot, string.Empty, null!, candidateRoot, candidateRoot,
        string.Empty, null!, candidateRoot, candidateRoot, candidateRoot,
        candidateRoot, string.Empty, null!, candidateRoot, candidateRoot,
        string.Empty, null!, candidateRoot, candidateRoot, candidateRoot,
        candidateRoot, string.Empty, null!, candidateRoot, candidateRoot,
        string.Empty, null!, candidateRoot, candidateRoot, candidateRoot,
        candidateRoot, string.Empty, null!, candidateRoot, candidateRoot,
        string.Empty, null!, candidateRoot, candidateRoot, candidateRoot,
    ];
    var automaticInputs = new HostileReadOnlyList<string>(
        [candidateRoot, string.Empty, null!, candidateRoot],
        successfulObservationsBeforeThrow: 25);
    Pcl2DiscoveryRequest? createdRequest = null;
    Exception? createFailure = null;
    try
    {
        createdRequest = Pcl2DiscoveryRequest.Create(manualInputs, automaticInputs);
    }
    catch (Exception exception)
    {
        createFailure = exception;
    }

    Assert(
        rawFailure is null &&
        createFailure is null &&
        rawCandidates.MoveNextCalls <= 65 &&
        automaticInputs.MoveNextCalls <= 25,
        "Raw PCL request/Create inputs must stop at the shared literal cap plus at most one overflow observation before blanks, nulls, or duplicates are filtered; " +
        $"request MoveNext={rawCandidates.MoveNextCalls}, failure={rawFailure?.GetType().Name ?? "none"}; " +
        $"Create automatic MoveNext={automaticInputs.MoveNextCalls}, failure={createFailure?.GetType().Name ?? "none"}.");
    Assert(
        rawCandidates.CountAccesses == 0 && automaticInputs.CountAccesses == 0,
        "PCL request/Create bounding must not inspect an untrusted IReadOnlyList.Count before enumeration.");
    Assert(
        HasPclDiagnostic(rawResult!.Diagnostics, Pcl2DiagnosticCode.DiscoveryLimitReached),
        "A raw PCL request overflow must return a structured discovery-limit diagnostic.");

    var createCapability = new AuditedFileSystemCapability([candidateProof]);
    var createResult = new Pcl2InstanceDiscovery(createCapability).Discover(createdRequest!);
    Assert(
        HasPclDiagnostic(createResult.Diagnostics, Pcl2DiagnosticCode.DiscoveryLimitReached),
        "Pcl2DiscoveryRequest.Create must preserve its shared raw-input truncation as a structured discovery diagnostic.");

    var rawThrowing = new HostileReadOnlyList<Pcl2RootCandidate>(
        [candidate],
        successfulObservationsBeforeThrow: 2);
    var rawThrowingCapability = new AuditedFileSystemCapability([candidateProof]);
    Pcl2DiscoveryResult? rawThrowingResult = null;
    Exception? rawThrowingFailure = null;
    try
    {
        rawThrowingResult = new Pcl2InstanceDiscovery(rawThrowingCapability).Discover(
            new Pcl2DiscoveryRequest(rawThrowing));
    }
    catch (Exception exception)
    {
        rawThrowingFailure = exception;
    }

    Assert(
        rawThrowingFailure is null &&
        rawThrowingResult!.Roots.Count == 0 &&
        HasPclDiagnostic(rawThrowingResult.Diagnostics, Pcl2DiagnosticCode.CandidateEnumerationFailed) &&
        rawThrowingCapability.AuditLog.Count == 0,
        "A raw PCL candidate enumerator exception must fail closed before capability work with a structured diagnostic; " +
        $"failure={rawThrowingFailure?.GetType().Name ?? "none"}.");

    var manualThrowing = new HostileReadOnlyList<string>(
        [candidateRoot],
        successfulObservationsBeforeThrow: 2);
    Exception? manualCreateFailure = null;
    try
    {
        _ = Pcl2DiscoveryRequest.Create(manualThrowing, []);
    }
    catch (Exception exception)
    {
        manualCreateFailure = exception;
    }

    var automaticThrowing = new HostileReadOnlyList<string>(
        [candidateRoot],
        successfulObservationsBeforeThrow: 2);
    Exception? automaticCreateFailure = null;
    try
    {
        _ = Pcl2DiscoveryRequest.Create([], automaticThrowing);
    }
    catch (Exception exception)
    {
        automaticCreateFailure = exception;
    }

    Assert(
        manualCreateFailure is ArgumentException { ParamName: "manualCandidatePaths" } &&
        automaticCreateFailure is ArgumentException { ParamName: "automaticCandidatePaths" } &&
        manualThrowing.CountAccesses == 0 &&
        automaticThrowing.CountAccesses == 0,
        "Enumerator exceptions from each Pcl2DiscoveryRequest.Create input must become parameter-specific argument errors; " +
        $"manual={manualCreateFailure?.GetType().Name ?? "none"}/{(manualCreateFailure as ArgumentException)?.ParamName ?? "none"}; " +
        $"automatic={automaticCreateFailure?.GetType().Name ?? "none"}/{(automaticCreateFailure as ArgumentException)?.ParamName ?? "none"}.");

    AssertAcceptedEventsCarryAllowedRoots(rawCapability);
    AssertAcceptedEventsCarryAllowedRoots(createCapability);
    AssertAcceptedEventsCarryAllowedRoots(rawThrowingCapability);
    auditEvents.AddRange(rawCapability.AuditLog);
    auditEvents.AddRange(createCapability.AuditLog);
    auditEvents.AddRange(rawThrowingCapability.AuditLog);
}

static void ProviderRawInputsAreBoundedBeforeFiltering(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);
    var candidateRoot = sandbox.CreateGuidDirectory();
    var candidateProof = sandbox.GetRootProof(candidateRoot);

    var rememberedInputs = new HostileReadOnlyList<string>(
        [candidateRoot, string.Empty, null!, candidateRoot],
        successfulObservationsBeforeThrow: 65);
    var rememberedCapability = new AuditedFileSystemCapability([candidateProof]);
    var rememberedProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths(),
        rememberedCapability,
        new WindowsShortcutTargetResolver());
    AutomaticCandidateResult? rememberedResult = null;
    Exception? rememberedFailure = null;
    try
    {
        rememberedResult = rememberedProvider.GetCandidateResult(
            new AutomaticCandidateRequest(
                rememberedInputs,
                MaximumShortcutFiles: 0,
                MaximumCandidates: 64));
    }
    catch (Exception exception)
    {
        rememberedFailure = exception;
    }

    var shellInputs = new HostileReadOnlyList<string>(
        [candidateRoot, string.Empty, null!, candidateRoot],
        successfulObservationsBeforeThrow: 33);
    var shellCapability = new AuditedFileSystemCapability([candidateProof]);
    var shellProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths { StartMenuRoots = shellInputs },
        shellCapability,
        new WindowsShortcutTargetResolver());
    AutomaticCandidateResult? shellResult = null;
    Exception? shellFailure = null;
    try
    {
        shellResult = shellProvider.GetCandidateResult(
            new AutomaticCandidateRequest(
                [],
                MaximumShortcutFiles: 1,
                MaximumCandidates: 64));
    }
    catch (Exception exception)
    {
        shellFailure = exception;
    }

    Assert(
        rememberedFailure is null &&
        shellFailure is null &&
        rememberedInputs.MoveNextCalls <= 65 &&
        shellInputs.MoveNextCalls <= 33,
        "Raw provider inputs must stop at the literal cap plus at most one overflow observation before blanks, nulls, or duplicates are filtered; " +
        $"remembered MoveNext={rememberedInputs.MoveNextCalls}, failure={rememberedFailure?.GetType().Name ?? "none"}; " +
        $"shell MoveNext={shellInputs.MoveNextCalls}, failure={shellFailure?.GetType().Name ?? "none"}.");
    Assert(
        rememberedInputs.CountAccesses == 0 && shellInputs.CountAccesses == 0,
        "Provider input bounding must not inspect an untrusted IReadOnlyList.Count before enumeration.");
    Assert(
        rememberedResult!.Diagnostics.Any(diagnostic =>
            diagnostic.Code == DiscoveryDiagnosticCode.CandidateLimitReached) &&
        shellResult!.Diagnostics.Any(diagnostic =>
            diagnostic.Code == DiscoveryDiagnosticCode.CandidateLimitReached),
        "Each raw provider input overflow must return a structured limit diagnostic.");

    var rememberedThrowing = new HostileReadOnlyList<string>(
        [candidateRoot],
        successfulObservationsBeforeThrow: 2);
    AutomaticCandidateResult? rememberedThrowingResult = null;
    Exception? rememberedThrowingFailure = null;
    try
    {
        rememberedThrowingResult = rememberedProvider.GetCandidateResult(
            new AutomaticCandidateRequest(
                rememberedThrowing,
                MaximumShortcutFiles: 0,
                MaximumCandidates: 64));
    }
    catch (Exception exception)
    {
        rememberedThrowingFailure = exception;
    }

    var shellThrowing = new HostileReadOnlyList<string>(
        [candidateRoot],
        successfulObservationsBeforeThrow: 2);
    AutomaticCandidateResult? shellThrowingResult = null;
    Exception? shellThrowingFailure = null;
    try
    {
        shellThrowingResult = new AutomaticCandidateProvider(
            new FakeEnvironmentPaths { StartMenuRoots = shellThrowing },
            shellCapability,
            new WindowsShortcutTargetResolver()).GetCandidateResult(
                new AutomaticCandidateRequest(
                    [],
                    MaximumShortcutFiles: 1,
                    MaximumCandidates: 64));
    }
    catch (Exception exception)
    {
        shellThrowingFailure = exception;
    }

    Assert(
        rememberedThrowingFailure is null &&
        shellThrowingFailure is null &&
        rememberedThrowingResult!.Candidates.Count == 0 &&
        shellThrowingResult!.Candidates.Count == 0 &&
        rememberedThrowingResult.Diagnostics.Any(diagnostic =>
            diagnostic.Code == DiscoveryDiagnosticCode.CandidateEnumerationFailed) &&
        shellThrowingResult.Diagnostics.Any(diagnostic =>
            diagnostic.Code == DiscoveryDiagnosticCode.CandidateEnumerationFailed),
        "Provider enumerator exceptions must fail the affected raw source closed with a structured diagnostic; " +
        $"remembered failure={rememberedThrowingFailure?.GetType().Name ?? "none"}; " +
        $"shell failure={shellThrowingFailure?.GetType().Name ?? "none"}.");

    AssertAcceptedEventsCarryAllowedRoots(rememberedCapability);
    AssertAcceptedEventsCarryAllowedRoots(shellCapability);
    auditEvents.AddRange(rememberedCapability.AuditLog);
    auditEvents.AddRange(shellCapability.AuditLog);
}

static void AutomaticCandidatesAreBoundedDeterministicAndPure(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);

    var rememberedA = sandbox.CreateGuidDirectory();
    var rememberedB = sandbox.CreateGuidDirectory();
    var shellRoot = sandbox.CreateGuidDirectory();
    var shortcutTarget = sandbox.CreateGuidDirectory();
    var roamingRoot = sandbox.CreateGuidDirectory();
    var roamingMinecraft = sandbox.CreateDirectory(
        Path.Combine(Path.GetRelativePath(sandbox.RootPath, roamingRoot), ".minecraft"));
    var roamingMinecraftProof = IssueNestedRootProof(sandbox, roamingMinecraft);

    var shellRelative = Path.GetRelativePath(sandbox.RootPath, shellRoot);
    sandbox.WriteBytes(
        Path.Combine(shellRelative, "00-good.lnk"),
        CreateLocalShellLink(shortcutTarget));
    sandbox.WriteBytes(
        Path.Combine(shellRelative, "01-malformed.lnk"),
        [0x4c, 0x00, 0x00]);
    sandbox.WriteBytes(
        Path.Combine(shellRelative, "02-oversized.lnk"),
        new byte[(1024 * 1024) + 1]);

    var proofs = new[]
    {
        sandbox.GetRootProof(rememberedA),
        sandbox.GetRootProof(rememberedB),
        sandbox.GetRootProof(shellRoot),
        sandbox.GetRootProof(shortcutTarget),
        sandbox.GetRootProof(roamingRoot),
        roamingMinecraftProof,
    };

    var orderingCapability = new AuditedFileSystemCapability(proofs);
    var orderingProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths { RoamingAppData = roamingRoot },
        orderingCapability,
        new WindowsShortcutTargetResolver());
    var ordered = orderingProvider.GetCandidates(
        new AutomaticCandidateRequest([rememberedB, rememberedA, rememberedA], MaximumCandidates: 8));
    var manualPaths = ordered
        .Where(candidate => candidate.Origin == Pcl2CandidateOrigin.Manual)
        .Select(candidate => candidate.CandidatePath)
        .ToArray();
    Assert(
        manualPaths.SequenceEqual(
            new[] { rememberedA, rememberedB }
                .Select(Pcl2PathNormalizer.Normalize)
                .OrderBy(path => path, StringComparer.Ordinal),
            StringComparer.Ordinal),
        "Remembered candidates must deduplicate only their equal strong identities and sort by ordinal canonical path.");
    Assert(
        ordered.Select(candidate => candidate.Origin).SequenceEqual(
            ordered.Select(candidate => candidate.Origin).OrderBy(origin => origin)),
        "Candidates must sort by origin before their ordinal canonical path.");
    Assert(
        ordered.Any(candidate => Pcl2PathNormalizer.AreEquivalent(candidate.CandidatePath, roamingMinecraft)),
        "The injected roaming-app-data root may contribute only its bounded .minecraft child.");

    var parser = new RecordingShortcutResolver(shortcutTarget);
    var parserCapability = new AuditedFileSystemCapability(proofs);
    var parserProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths { UserDesktop = shellRoot },
        parserCapability,
        parser);
    var parserCandidates = parserProvider.GetCandidates(
        new AutomaticCandidateRequest([], MaximumShortcutFiles: 1, MaximumCandidates: 8));
    Assert(parser.ParseCount == 1, "The shortcut parser must receive exactly the bounded first shortcut snapshot.");
    Assert(parser.LastLength == CreateLocalShellLink(shortcutTarget).LongLength, "The parser must receive immutable shortcut bytes read by the capability.");
    Assert(
        parserCandidates.Any(candidate => Pcl2PathNormalizer.AreEquivalent(candidate.CandidatePath, shortcutTarget)),
        "A pure parser result may become a candidate only after capability verification.");
    Assert(
        parserCapability.AuditLog.Count(entry => entry.Operation == "ReadFile") == 1,
        "MaximumShortcutFiles must cap shortcut reads before parser invocation.");

    var realParser = new WindowsShortcutTargetResolver();
    var parsed = realParser.Parse(CreateSnapshot(CreateLocalShellLink(shortcutTarget)));
    Assert(
        parsed.IsResolved && string.Equals(parsed.TargetPath, shortcutTarget, StringComparison.Ordinal),
        "The pure Windows parser must resolve the hand-derived local LinkInfo target without reopening a path.");
    var malformed = realParser.Parse(CreateSnapshot([0x4c, 0x00, 0x00]));
    Assert(
        !malformed.IsResolved && malformed.Diagnostic?.Code == DiscoveryDiagnosticCode.ShortcutMalformed,
        "A truncated shell link must return a structured malformed diagnostic.");

    var hostileCapability = new AuditedFileSystemCapability(proofs);
    var hostileProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths { PublicDesktop = shellRoot },
        hostileCapability,
        realParser);
    var hostileResult = hostileProvider.GetCandidateResult(
        new AutomaticCandidateRequest([], MaximumShortcutFiles: 3, MaximumCandidates: 8));
    Assert(
        hostileResult.Diagnostics.Any(diagnostic => diagnostic.Code == DiscoveryDiagnosticCode.ShortcutMalformed),
        "A malformed .lnk must become a structured discovery diagnostic.");
    Assert(
        hostileResult.Diagnostics.Any(diagnostic => diagnostic.Code == DiscoveryDiagnosticCode.ShortcutTooLarge),
        "An oversized .lnk must be rejected by the bounded capability read and diagnosed.");

    var networkProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths { UserDesktop = shellRoot },
        new AuditedFileSystemCapability(proofs),
        new RecordingShortcutResolver(@"\\server\share\PCL2.exe"));
    var networkResult = networkProvider.GetCandidateResult(
        new AutomaticCandidateRequest([], MaximumShortcutFiles: 1));
    Assert(
        networkResult.Candidates.Count == 0 &&
        networkResult.Diagnostics.Any(diagnostic => diagnostic.Code == DiscoveryDiagnosticCode.NetworkOrDeviceTargetRejected),
        "A network shortcut target must be rejected before any target open.");

    var deviceProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths { UserDesktop = shellRoot },
        new AuditedFileSystemCapability(proofs),
        new RecordingShortcutResolver(@"\\?\C:\device\PCL2.exe"));
    var deviceResult = deviceProvider.GetCandidateResult(
        new AutomaticCandidateRequest([], MaximumShortcutFiles: 1));
    Assert(
        deviceResult.Candidates.Count == 0 &&
        deviceResult.Diagnostics.Any(diagnostic => diagnostic.Code == DiscoveryDiagnosticCode.NetworkOrDeviceTargetRejected),
        "A device shortcut target must be rejected before any target open.");

    foreach (var audit in new[] { orderingCapability, parserCapability, hostileCapability })
    {
        AssertAcceptedEventsCarryAllowedRoots(audit);
        auditEvents.AddRange(audit.AuditLog);
    }
}

static void ProviderPreWorkLimitsBoundCapabilityCalls(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);
    var remembered = Enumerable.Range(0, 70)
        .Select(_ => sandbox.CreateGuidDirectory())
        .ToArray();
    var rememberedProofs = remembered.Select(sandbox.GetRootProof).ToArray();
    var rememberedCapability = new AuditedFileSystemCapability(rememberedProofs);
    var rememberedProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths(),
        rememberedCapability,
        new WindowsShortcutTargetResolver());
    var rememberedResult = rememberedProvider.GetCandidateResult(
        new AutomaticCandidateRequest(remembered, MaximumShortcutFiles: 0, MaximumCandidates: 64));
    Assert(
        rememberedCapability.AuditLog.Count(entry => entry.Operation == "OpenRoot") <= 64,
        "Remembered discovery roots must be capped at 64 before any capability open.");
    Assert(
        rememberedResult.Diagnostics.Any(diagnostic => diagnostic.Code == DiscoveryDiagnosticCode.CandidateLimitReached),
        "A pre-work remembered-root truncation must produce a structured limit diagnostic.");

    var duplicateCapability = new AuditedFileSystemCapability([rememberedProofs[0]]);
    var duplicateProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths(),
        duplicateCapability,
        new WindowsShortcutTargetResolver());
    _ = duplicateProvider.GetCandidates(
        new AutomaticCandidateRequest(
            Enumerable.Repeat(remembered[0], 100).ToArray(),
            MaximumShortcutFiles: 0,
            MaximumCandidates: 64));
    Assert(
        duplicateCapability.AuditLog.Count(entry => entry.Operation == "OpenRoot") == 1,
        "Exact duplicate remembered-root strings must be removed before capability work without serving as identity proof.");

    var shellRoots = Enumerable.Range(0, 40)
        .Select(_ => sandbox.CreateGuidDirectory())
        .ToArray();
    var shellCapability = new AuditedFileSystemCapability(
        shellRoots.Select(sandbox.GetRootProof));
    var shellProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths { StartMenuRoots = shellRoots },
        shellCapability,
        new WindowsShortcutTargetResolver());
    _ = shellProvider.GetCandidates(
        new AutomaticCandidateRequest([], MaximumShortcutFiles: 1, MaximumCandidates: 64));
    Assert(
        shellCapability.AuditLog.Count(entry => entry.Operation == "OpenRoot") <= 32,
        "Injected shell roots must be capped before any capability open.");

    var shortcutShell = sandbox.CreateGuidDirectory();
    var shortcutTarget = sandbox.CreateGuidDirectory();
    var shortcutShellRelative = Path.GetRelativePath(sandbox.RootPath, shortcutShell);
    var shortcutBytes = CreateLocalShellLink(shortcutTarget);
    foreach (var index in Enumerable.Range(0, 140))
    {
        sandbox.WriteBytes(
            Path.Combine(shortcutShellRelative, $"{index:D3}.lnk"),
            shortcutBytes);
    }

    var shortcutCapability = new AuditedFileSystemCapability(
        [sandbox.GetRootProof(shortcutShell), sandbox.GetRootProof(shortcutTarget)]);
    var shortcutProvider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths { UserDesktop = shortcutShell },
        shortcutCapability,
        new RecordingShortcutResolver(shortcutTarget));
    _ = shortcutProvider.GetCandidates(
        new AutomaticCandidateRequest([], MaximumShortcutFiles: 256, MaximumCandidates: 64));
    Assert(
        shortcutCapability.AuditLog.Count(entry => entry.Operation == "OpenRoot") <= 128,
        "Automatic discovery must cap total root-open attempts before capability work.");
    Assert(
        shortcutCapability.AuditLog.Count(entry =>
            entry.Operation == "OpenRoot" &&
            entry.RequestedPath.Equals(shortcutTarget, StringComparison.OrdinalIgnoreCase)) == 1,
        "Exact duplicate shortcut targets must not trigger repeated capability opens.");

    foreach (var capability in new[]
             {
                 rememberedCapability,
                 duplicateCapability,
                 shellCapability,
                 shortcutCapability,
             })
    {
        AssertAcceptedEventsCarryAllowedRoots(capability);
        auditEvents.AddRange(capability.AuditLog);
    }
}

static void ShortcutTargetKindComesFromBoundedHeader(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);
    var shellRoot = sandbox.CreateGuidDirectory();
    var dottedDirectory = sandbox.CreateDirectory("Games.v1");
    var dottedProof = IssueNestedRootProof(sandbox, dottedDirectory);
    var fileParent = sandbox.CreateGuidDirectory();
    var extensionlessFile = sandbox.WriteBytes(
        Path.Combine(Path.GetRelativePath(sandbox.RootPath, fileParent), "launcher"),
        [0x01]);
    var shellRelative = Path.GetRelativePath(sandbox.RootPath, shellRoot);
    sandbox.WriteBytes(
        Path.Combine(shellRelative, "00-dotted-directory.lnk"),
        CreateLocalShellLink(dottedDirectory, FileAttributes.Directory));
    sandbox.WriteBytes(
        Path.Combine(shellRelative, "01-extensionless-file.lnk"),
        CreateLocalShellLink(extensionlessFile, FileAttributes.Normal));
    sandbox.WriteBytes(
        Path.Combine(shellRelative, "02-unknown-kind.lnk"),
        CreateLocalShellLink(dottedDirectory, 0));

    var capability = new AuditedFileSystemCapability(
        [sandbox.GetRootProof(shellRoot), dottedProof, sandbox.GetRootProof(fileParent)]);
    var provider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths { UserDesktop = shellRoot },
        capability,
        new WindowsShortcutTargetResolver());
    var result = provider.GetCandidateResult(
        new AutomaticCandidateRequest([], MaximumShortcutFiles: 3, MaximumCandidates: 8));
    var candidates = result.Candidates;
    Assert(
        candidates.Any(candidate => Pcl2PathNormalizer.AreEquivalent(candidate.CandidatePath, dottedDirectory)),
        "A shortcut target positively marked as a directory must remain intact even when its directory name contains a dot.");
    Assert(
        candidates.Any(candidate => Pcl2PathNormalizer.AreEquivalent(candidate.CandidatePath, fileParent)),
        "A shortcut target positively marked as a file must contribute its parent even when the filename has no extension.");
    Assert(
        result.Diagnostics.Any(diagnostic => diagnostic.Code == DiscoveryDiagnosticCode.ShortcutTargetKindUnknown),
        "A shortcut with no trustworthy target-kind evidence must fail closed with a structured diagnostic.");
    AssertAcceptedEventsCarryAllowedRoots(capability);
    auditEvents.AddRange(capability.AuditLog);
}

static void DiagnosticsAreRequestLocalAndSanitized(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);
    var capability = new AuditedFileSystemCapability([sandbox.RootProof]);
    var provider = new AutomaticCandidateProvider(
        new FakeEnvironmentPaths(),
        capability,
        new WindowsShortcutTargetResolver());
    var overLimitRequest = new AutomaticCandidateRequest(
        Enumerable.Range(0, 65).Select(index => $"relative-{index}").ToArray(),
        MaximumShortcutFiles: 0,
        MaximumCandidates: 64);
    var emptyRequest = new AutomaticCandidateRequest(
        [],
        MaximumShortcutFiles: 0,
        MaximumCandidates: 64);
    var providerCalls = new[]
    {
        Task.Run(() => provider.GetCandidateResult(overLimitRequest)),
        Task.Run(() => provider.GetCandidateResult(emptyRequest)),
    };
    Task.WaitAll(providerCalls);
    Assert(
        providerCalls[0].Result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == DiscoveryDiagnosticCode.CandidateLimitReached) &&
        providerCalls[1].Result.Diagnostics.Count == 0,
        "Overlapping provider calls must return their own immutable diagnostics instead of sharing mutable last-call state.");

    var resolver = new InstanceCandidateResolver(capability);
    var resolverCalls = new[]
    {
        Task.Run(() => resolver.ResolveResult(new DiscoveryCandidate(
            @"\\server\share",
            Pcl2CandidateOrigin.Automatic,
            "network"))),
        Task.Run(() => resolver.ResolveResult(new DiscoveryCandidate(
            "relative-path",
            Pcl2CandidateOrigin.Manual,
            "relative"))),
    };
    Task.WaitAll(resolverCalls);
    Assert(
        resolverCalls[0].Result.Diagnostics.Single().Code == DiscoveryDiagnosticCode.NetworkOrDeviceTargetRejected &&
        resolverCalls[1].Result.Diagnostics.Single().Code == DiscoveryDiagnosticCode.CandidatePathInvalid,
        "Overlapping resolver calls must retain only their own structured diagnostics.");

    var minecraftRoot = sandbox.CreateGuidDirectory();
    var minecraftRelative = Path.GetRelativePath(sandbox.RootPath, minecraftRoot);
    sandbox.WriteBytes(
        Path.Combine(minecraftRelative, "PCL.ini"),
        Encoding.UTF8.GetBytes("Version:unsafe\n"));
    sandbox.WriteBytes(
        Path.Combine(minecraftRelative, "versions", "unsafe", "PCL", "Setup.ini"),
        Encoding.UTF8.GetBytes("VersionArgumentIndieV2:evil\tvalue\n"));
    sandbox.WriteBytes(
        Path.Combine(minecraftRelative, "versions", "unsafe", "unsafe.json"),
        Encoding.UTF8.GetBytes("{\"id\":\"unsafe\",\"mainClass\":\"net.minecraft.client.main.Main\"}"));
    var pclCapability = new AuditedFileSystemCapability([sandbox.GetRootProof(minecraftRoot)]);
    var pclResult = new Pcl2InstanceDiscovery(pclCapability).Discover(
        Pcl2DiscoveryRequest.Create([minecraftRoot], []));
    var unsafeDiagnostic = pclResult.Instances.Single().Diagnostics.Single(diagnostic =>
        diagnostic.Code == Pcl2DiagnosticCode.IsolationSettingUnknown);
    Assert(
        unsafeDiagnostic.Message.Contains("evil\\tvalue", StringComparison.Ordinal) &&
        !unsafeDiagnostic.Message.Any(char.IsControl) &&
        unsafeDiagnostic.Message.Length <= 256,
        "Attacker-controlled technical values must be escaped and capped before entering diagnostics.");

    AssertAcceptedEventsCarryAllowedRoots(capability);
    AssertAcceptedEventsCarryAllowedRoots(pclCapability);
    auditEvents.AddRange(capability.AuditLog);
    auditEvents.AddRange(pclCapability.AuditLog);
}

static void FourSelectionLevelsResolveThroughOwnedHandles(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);

    var launcherRoot = sandbox.CreateGuidDirectory();
    var launcherRelative = Path.GetRelativePath(sandbox.RootPath, launcherRoot);
    var minecraftRoot = sandbox.CreateDirectory(Path.Combine(launcherRelative, ".minecraft"));
    var versionsRoot = sandbox.CreateDirectory(Path.Combine(launcherRelative, ".minecraft", "versions"));
    var instanceRoot = sandbox.CreateDirectory(Path.Combine(launcherRelative, ".minecraft", "versions", "Direct Instance"));
    sandbox.WriteBytes(
        Path.Combine(launcherRelative, ".minecraft", "versions", "Direct Instance", "Direct Instance.json"),
        Encoding.UTF8.GetBytes("{\"id\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\"}"));
    sandbox.WriteBytes(
        Path.Combine(launcherRelative, ".minecraft", "versions", "Direct Instance", "PCL", "Setup.ini"),
        Encoding.UTF8.GetBytes("VersionArgumentIndieV2:true\r\n"));

    var proofs = new[]
    {
        sandbox.GetRootProof(launcherRoot),
        IssueNestedRootProof(sandbox, minecraftRoot),
        IssueNestedRootProof(sandbox, versionsRoot),
        IssueNestedRootProof(sandbox, instanceRoot),
    };
    var capability = new AuditedFileSystemCapability(proofs);
    var resolver = new InstanceCandidateResolver(capability);

    foreach (var selection in new[] { launcherRoot, minecraftRoot, versionsRoot, instanceRoot })
    {
        var resolved = resolver.Resolve(new DiscoveryCandidate(
            selection,
            Pcl2CandidateOrigin.Manual,
            "fixture selection"));
        Assert(resolved.Count == 1, $"The approved selection level must resolve exactly once: {selection}");
        Assert(
            Pcl2PathNormalizer.AreEquivalent(resolved[0].CandidatePath, minecraftRoot),
            "PCL root, .minecraft, versions, and direct-instance selections must prove the same owning Minecraft root.");
    }

    var networkResult = resolver.ResolveResult(
        new DiscoveryCandidate(@"\\server\share", Pcl2CandidateOrigin.Manual, "network"));
    Assert(
        networkResult.Candidates.Count == 0 &&
        networkResult.Diagnostics.Any(diagnostic => diagnostic.Code == DiscoveryDiagnosticCode.NetworkOrDeviceTargetRejected),
        "A network selection must be rejected structurally without capability access.");
    var deviceResult = resolver.ResolveResult(
        new DiscoveryCandidate(@"\\?\C:\device", Pcl2CandidateOrigin.Manual, "device"));
    Assert(
        deviceResult.Candidates.Count == 0 &&
        deviceResult.Diagnostics.Any(diagnostic => diagnostic.Code == DiscoveryDiagnosticCode.NetworkOrDeviceTargetRejected),
        "A device selection must be rejected structurally without capability access.");

    AssertAcceptedEventsCarryAllowedRoots(capability);
    auditEvents.AddRange(capability.AuditLog);
}

static void RetainedMinecraftRootSurvivesOrdinaryRenameReplacement(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);
    var minecraftRoot = sandbox.CreateDirectory(Path.Combine("launcher", ".minecraft"));
    sandbox.WriteBytes(Path.Combine("launcher", ".minecraft", "original.txt"), [0x01]);
    var movedMinecraftRoot = minecraftRoot + "-moved";
    var capability = new AuditedFileSystemCapability([sandbox.RootProof]);
    Pcl2ReadPathGuard? guard = null;
    try
    {
        PhysicalDirectoryIdentity minecraftIdentity;
        PhysicalDirectoryIdentity approvedIdentity;
        using (var approved = capability.OpenRoot(
                   sandbox.RootPath,
                   FileSystemOpenPurpose.Discovery,
                   CancellationToken.None))
        {
            approvedIdentity = approved.Identity;
            using var minecraft = capability.OpenDirectory(
                approved,
                MustPath(Path.Combine("launcher", ".minecraft")),
                CancellationToken.None);
            minecraftIdentity = minecraft.Identity;
        }

        guard = new Pcl2ReadPathGuard(
            capability,
            new Pcl2ResolvedRootAccess(
                sandbox.RootPath,
                approvedIdentity,
                MustPath(Path.Combine("launcher", ".minecraft")),
                minecraftRoot,
                minecraftIdentity,
                new ManualSelectionAuthority()),
            CancellationToken.None);

        Directory.Move(minecraftRoot, movedMinecraftRoot);
        Directory.CreateDirectory(minecraftRoot);
        File.WriteAllBytes(Path.Combine(minecraftRoot, "replacement.txt"), [0x02]);

        var entries = guard.EnumerateMinecraft(
            MustPath(string.Empty),
            8,
            CancellationToken.None);
        Assert(
            entries.Any(entry => entry.RelativePath.Value == "original.txt") &&
            entries.All(entry => entry.RelativePath.Value != "replacement.txt"),
            "Minecraft reads must remain relative to the retained verified nested-root handle after an ordinary rename/replacement race.");

        guard.Dispose();
        AssertThrows<ObjectDisposedException>(
            () => guard.EnumerateMinecraft(MustPath(string.Empty), 8, CancellationToken.None),
            "A disposed retained Minecraft-root guard must reject later observations.");
        guard = null;
    }
    finally
    {
        guard?.Dispose();
        auditEvents.AddRange(capability.AuditLog);
    }
}

static void PclDiscoveryBudgetsBoundCapabilityCalls(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);

    var candidateRoots = Enumerable.Range(0, 6)
        .Select(_ => sandbox.CreateGuidDirectory())
        .ToArray();
    var candidateCapability = new AuditedFileSystemCapability(
        candidateRoots.Select(sandbox.GetRootProof));
    var candidateResult = new Pcl2InstanceDiscovery(candidateCapability).Discover(
        new Pcl2DiscoveryRequest(candidateRoots
            .Select(path => new Pcl2RootCandidate(path, Pcl2CandidateOrigin.Manual))
            .ToArray())
        {
            Limits = new Pcl2DiscoveryLimits { MaximumCandidates = 3 },
        });
    Assert(
        candidateCapability.AuditLog.Count(entry => entry.Operation == "OpenRoot") <= 3,
        "PCL request candidates must be capped before capability work.");
    Assert(
        HasPclDiagnostic(candidateResult.Diagnostics, Pcl2DiagnosticCode.DiscoveryLimitReached),
        "PCL candidate truncation must return a structured discovery-limit diagnostic.");

    var minecraftRoot = sandbox.CreateGuidDirectory();
    var minecraftProof = sandbox.GetRootProof(minecraftRoot);
    var minecraftRelative = Path.GetRelativePath(sandbox.RootPath, minecraftRoot);
    sandbox.WriteBytes(
        Path.Combine(minecraftRelative, "PCL.ini"),
        Encoding.UTF8.GetBytes("Version:a\n"));
    foreach (var name in new[] { "a", "b", "c" })
    {
        sandbox.WriteBytes(
            Path.Combine(minecraftRelative, "versions", name, "PCL", "Setup.ini"),
            Encoding.UTF8.GetBytes("VersionArgumentIndieV2:1\n"));
        sandbox.WriteBytes(
            Path.Combine(minecraftRelative, "versions", name, name + ".json"),
            Encoding.UTF8.GetBytes($"{{\"id\":\"{name}\",\"mainClass\":\"net.minecraft.client.main.Main\"}}"));
    }

    var instanceCapability = new AuditedFileSystemCapability([minecraftProof]);
    var instanceResult = new Pcl2InstanceDiscovery(instanceCapability).Discover(
        new Pcl2DiscoveryRequest(
            [new Pcl2RootCandidate(minecraftRoot, Pcl2CandidateOrigin.Manual)])
        {
            Limits = new Pcl2DiscoveryLimits { MaximumInstances = 2 },
        });
    Assert(
        !instanceCapability.AuditLog.Any(entry =>
            entry.Operation == "OpenDirectory" &&
            entry.RequestedPath.Contains("versions\\", StringComparison.OrdinalIgnoreCase)),
        "An over-limit versions directory must be rejected before any instance is opened.");
    Assert(
        HasPclDiagnostic(instanceResult.Diagnostics, Pcl2DiagnosticCode.DiscoveryLimitReached),
        "An instance-enumeration bound must return a structured discovery-limit diagnostic.");

    var enumerationCapability = new AuditedFileSystemCapability([minecraftProof]);
    var enumerationResult = new Pcl2InstanceDiscovery(enumerationCapability).Discover(
        new Pcl2DiscoveryRequest(
            [new Pcl2RootCandidate(minecraftRoot, Pcl2CandidateOrigin.Manual)])
        {
            Limits = new Pcl2DiscoveryLimits { MaximumEnumerationOperations = 1 },
        });
    Assert(
        enumerationCapability.AuditLog.Count(entry => entry.Operation == "EnumerateEntries") <= 1,
        "The aggregate PCL enumeration-operation budget must stop capability calls at its limit.");
    Assert(
        HasPclDiagnostic(enumerationResult.Diagnostics, Pcl2DiagnosticCode.DiscoveryLimitReached),
        "Enumeration-operation exhaustion must return a structured discovery-limit diagnostic.");

    var readCapability = new AuditedFileSystemCapability([minecraftProof]);
    var readResult = new Pcl2InstanceDiscovery(readCapability).Discover(
        new Pcl2DiscoveryRequest(
            [new Pcl2RootCandidate(minecraftRoot, Pcl2CandidateOrigin.Manual)])
        {
            Limits = new Pcl2DiscoveryLimits { MaximumReadOperations = 2 },
        });
    Assert(
        readCapability.AuditLog.Count(entry => entry.Operation == "ReadFile") <= 2,
        "The aggregate PCL metadata-read budget must stop capability calls at its limit.");
    Assert(
        HasPclDiagnostic(readResult.Diagnostics, Pcl2DiagnosticCode.DiscoveryLimitReached),
        "Metadata-read exhaustion must return a structured discovery-limit diagnostic.");

    var byteCapability = new AuditedFileSystemCapability([minecraftProof]);
    var byteResult = new Pcl2InstanceDiscovery(byteCapability).Discover(
        new Pcl2DiscoveryRequest(
            [new Pcl2RootCandidate(minecraftRoot, Pcl2CandidateOrigin.Manual)])
        {
            Limits = new Pcl2DiscoveryLimits
            {
                MaximumReadOperations = 8,
                MaximumTotalReadBytes = 4,
                MaximumFileReadBytes = 4,
            },
        });
    Assert(
        byteCapability.AuditLog.Count(entry => entry.Operation == "ReadFile") == 1,
        "The remaining aggregate byte allowance must bound the first capability read and stop later reads.");
    Assert(
        HasPclDiagnostic(byteResult.Diagnostics, Pcl2DiagnosticCode.DiscoveryLimitReached),
        "Aggregate byte exhaustion must return a structured discovery-limit diagnostic.");

    foreach (var capability in new[]
             {
                 candidateCapability,
                 instanceCapability,
                 enumerationCapability,
                 readCapability,
                 byteCapability,
             })
    {
        AssertAcceptedEventsCarryAllowedRoots(capability);
        auditEvents.AddRange(capability.AuditLog);
    }
}

static void InjectedPclReadsRemainCapabilityBound(
    List<string> fixtureRoots,
    List<CapabilityAuditEvent> auditEvents)
{
    using var sandbox = FixtureSandbox.Create();
    fixtureRoots.Add(sandbox.RootPath);

    var launcherRoot = sandbox.CreateGuidDirectory();
    var launcherRelative = Path.GetRelativePath(sandbox.RootPath, launcherRoot);
    var minecraftRelative = Path.Combine(launcherRelative, ".minecraft");
    var minecraftRoot = sandbox.CreateDirectory(minecraftRelative);
    var versionsRoot = sandbox.CreateDirectory(Path.Combine(minecraftRelative, "versions"));
    var baseRoot = sandbox.CreateDirectory(Path.Combine(minecraftRelative, "versions", "1.21.1"));
    var sourceRoot = sandbox.CreateDirectory(Path.Combine(minecraftRelative, "versions", "Source"));
    var targetRoot = sandbox.CreateDirectory(Path.Combine(minecraftRelative, "versions", "Target"));

    sandbox.WriteBytes(
        Path.Combine(minecraftRelative, "PCL.ini"),
        Encoding.UTF8.GetBytes("Version:Source\r\n"));
    WritePclInstance(sandbox, minecraftRelative, "1.21.1", "{\"id\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\"}", null);
    WritePclInstance(
        sandbox,
        minecraftRelative,
        "Source",
        "{\"id\":\"source\",\"inheritsFrom\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\"}",
        "version:3955\nlang:zh_cn\nkey_key.jump:key.keyboard.space\n");
    WritePclInstance(
        sandbox,
        minecraftRelative,
        "Target",
        "{\"id\":\"target\",\"inheritsFrom\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\"}",
        "version:3955\nlang:en_us\nkey_key.jump:key.keyboard.j\n");

    var proofs = new[]
    {
        sandbox.GetRootProof(launcherRoot),
        IssueNestedRootProof(sandbox, minecraftRoot),
        IssueNestedRootProof(sandbox, versionsRoot),
        IssueNestedRootProof(sandbox, baseRoot),
        IssueNestedRootProof(sandbox, sourceRoot),
        IssueNestedRootProof(sandbox, targetRoot),
    };
    var capability = new AuditedFileSystemCapability(proofs);
    var resolver = new InstanceCandidateResolver(capability);
    var roots = resolver.Resolve(new DiscoveryCandidate(
        sourceRoot,
        Pcl2CandidateOrigin.Manual,
        "direct instance"));
    var discovery = new Pcl2InstanceDiscovery(capability).Discover(new Pcl2DiscoveryRequest(roots));
    var root = discovery.Roots.Single();
    var source = root.Instances.Single(instance => Path.GetFileName(instance.InstanceRoot) == "Source");
    var target = root.Instances.Single(instance => Path.GetFileName(instance.InstanceRoot) == "Target");
    Assert(source.MinecraftVersion == "1.21.1", "Inherited metadata must be read through the injected capability.");
    Assert(source.Isolation == Pcl2IsolationMode.Isolated, "Isolation metadata must be read through the injected capability.");

    var previewer = new Pcl2OptionsMigrationPreviewer(capability);
    var preparation = previewer.PrepareSelection(source, target);
    Assert(!preparation.IsBlocked && preparation.Session is not null, "Capability-backed options reads must prepare a selection.");
    var selected = previewer.PreviewSelected(
        preparation.Session!,
        new HashSet<string>(["lang"], StringComparer.Ordinal));
    Assert(
        !selected.IsBlocked && selected.PlannedChanges.Select(item => item.Key).SequenceEqual(["lang"]),
        "Selected options preview must reread both snapshots through the injected capability.");

    Assert(
        capability.AuditLog.Any(entry => entry.Operation == "EnumerateEntries" && entry.RequestedPath.EndsWith("versions", StringComparison.OrdinalIgnoreCase)),
        "Discovery enumeration must appear in the capability audit.");
    Assert(
        capability.AuditLog.Any(entry => entry.Operation == "ReadFile" && entry.RequestedPath.EndsWith("1.21.1.json", StringComparison.OrdinalIgnoreCase)),
        "Inheritance JSON reads must appear in the capability audit.");
    Assert(
        capability.AuditLog.Count(entry => entry.Operation == "ReadFile" && entry.RequestedPath.EndsWith("options.txt", StringComparison.OrdinalIgnoreCase)) >= 4,
        "PrepareSelection and PreviewSelected must each audit source and target options reads.");
    AssertAcceptedEventsCarryAllowedRoots(capability);
    auditEvents.AddRange(capability.AuditLog);
}

static void WritePclInstance(
    FixtureSandbox sandbox,
    string minecraftRelative,
    string name,
    string versionJson,
    string? options)
{
    sandbox.WriteBytes(
        Path.Combine(minecraftRelative, "versions", name, name + ".json"),
        Encoding.UTF8.GetBytes(versionJson));
    sandbox.WriteBytes(
        Path.Combine(minecraftRelative, "versions", name, "PCL", "Setup.ini"),
        Encoding.UTF8.GetBytes("VersionArgumentIndieV2:true\r\n"));
    if (options is not null)
    {
        sandbox.WriteBytes(
            Path.Combine(minecraftRelative, "versions", name, "options.txt"),
            Encoding.UTF8.GetBytes(options));
    }
}

static void AssertAcceptedEventsCarryAllowedRoots(AuditedFileSystemCapability capability)
{
    var allowedRoots = capability.AllowedRootIds;
    Assert(
        capability.AuditLog.Where(entry => !entry.WasRejected).All(entry =>
            entry.RootId is Guid rootId && allowedRoots.Contains(rootId)),
        "Every accepted observation must carry an allowed capability root ID.");
}

static FixtureRootProof IssueNestedRootProof(FixtureSandbox sandbox, string path)
{
    var issueMethod = typeof(FixtureSandbox).GetMethod(
        "IssueRootProof",
        BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException("FixtureSandbox must retain its reviewed physical-root proof issuer.");
    return (FixtureRootProof)(issueMethod.Invoke(sandbox, [path]) ??
        throw new InvalidOperationException("The fixture root proof must be issued."));
}

static BoundedFileSnapshot CreateSnapshot(byte[] bytes) =>
    new(
        true,
        bytes,
        Convert.ToHexString(SHA256.HashData(bytes)),
        new FileObjectMetadata(DateTimeOffset.UnixEpoch, FileAttributes.Normal, null));

static byte[] CreateLocalShellLink(
    string targetPath,
    FileAttributes targetAttributes = FileAttributes.Directory)
{
    var targetBytes = Encoding.ASCII.GetBytes(targetPath + "\0");
    var linkInfoSize = 0x1c + targetBytes.Length;
    var bytes = new byte[0x4c + linkInfoSize];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x4c);
    new Guid("00021401-0000-0000-C000-000000000046").TryWriteBytes(bytes.AsSpan(4, 16));
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 0x00000002);
    BinaryPrimitives.WriteUInt32LittleEndian(
        bytes.AsSpan(24, 4),
        checked((uint)targetAttributes));
    var linkInfo = bytes.AsSpan(0x4c);
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo[..4], checked((uint)linkInfoSize));
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.Slice(4, 4), 0x1c);
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.Slice(8, 4), 0x1);
    BinaryPrimitives.WriteUInt32LittleEndian(linkInfo.Slice(16, 4), 0x1c);
    targetBytes.CopyTo(linkInfo[0x1c..]);
    return bytes;
}

static string ReadCase(string[] arguments)
{
    for (var index = 0; index < arguments.Length; index++)
    {
        if (arguments[index] == "--case" && index + 1 < arguments.Length)
        {
            return arguments[index + 1];
        }
    }

    return "capability";
}

static byte[] CreateClassicEocd(ushort entries, uint centralDirectoryBytes, uint centralDirectoryOffset)
{
    var bytes = new byte[22];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x06054B50);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), entries);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), entries);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), centralDirectoryBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), centralDirectoryOffset);
    return bytes;
}

static byte[] CreateClassicArchiveWithMalformedCentralRecord()
{
    var centralRecord = new byte[46];
    BinaryPrimitives.WriteUInt32LittleEndian(centralRecord.AsSpan(0, 4), 0x02014B50);
    BinaryPrimitives.WriteUInt16LittleEndian(centralRecord.AsSpan(28, 2), 1);
    var eocd = CreateClassicEocd(1, 46, 0);
    return [.. centralRecord, .. eocd];
}

static byte[] CreateZip64Archive(
    ulong entries,
    ulong centralDirectoryBytes,
    ulong centralDirectoryOffset,
    ulong zip64RecordSize = 44)
{
    var bytes = new byte[98];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x06064B50);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4, 8), zip64RecordSize);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), 45);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), 45);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24, 8), entries);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32, 8), entries);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40, 8), centralDirectoryBytes);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48, 8), centralDirectoryOffset);

    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56, 4), 0x07064B50);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64, 8), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72, 4), 1);

    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 0x06054B50);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(84, 2), ushort.MaxValue);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(86, 2), ushort.MaxValue);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), uint.MaxValue);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92, 4), uint.MaxValue);
    return bytes;
}

static byte[] CreateDirectoryRecord(string name, uint nextEntryOffset, int totalBytes)
{
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var bytes = new byte[totalBytes];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), nextEntryOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, 4), checked((uint)nameBytes.Length));
    nameBytes.CopyTo(bytes, 68);
    return bytes;
}

static ZipReadLimits ZipLimits(
    int maximumEntries = 3,
    int maximumEntryBytes = 3,
    long maximumTotalBytes = 5,
    long maximumArchiveBytes = 4096,
    long maximumCentralDirectoryBytes = 2048) =>
    new(
        maximumEntries,
        maximumEntryBytes,
        maximumTotalBytes,
        maximumArchiveBytes,
        maximumCentralDirectoryBytes);

static NormalizedRelativePath MustPath(string value)
{
    if (!NormalizedRelativePath.TryCreate(value, out var path, out var rejection))
    {
        throw new InvalidOperationException($"Test fixture path was rejected: {value}; {rejection}");
    }

    return path!;
}

static void AssertPathRejected(string candidate, string message)
{
    Assert(
        !NormalizedRelativePath.TryCreate(candidate, out _, out var reason) &&
        !string.IsNullOrWhiteSpace(reason),
        $"{message}: {candidate}");
}

static bool HasPclDiagnostic(
    IEnumerable<Pcl2Diagnostic> diagnostics,
    Pcl2DiagnosticCode code) =>
    diagnostics.Any(diagnostic => diagnostic.Code == code);

static void AssertThrows<TException>(Action action, string message)
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

static void AssertThrowsExactly<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (Exception exception)
    {
        Assert(
            exception.GetType() == typeof(TException),
            $"{message}; expected {typeof(TException).Name}, got {exception.GetType().Name}.");
        return;
    }

    throw new InvalidOperationException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class RecordingShortcutResolver(
    string targetPath,
    ShortcutTargetKind targetKind = ShortcutTargetKind.Directory) : IShortcutTargetResolver
{
    public int ParseCount { get; private set; }
    public long LastLength { get; private set; }

    public ShortcutResolution Parse(BoundedFileSnapshot shortcutBytes)
    {
        ParseCount++;
        LastLength = shortcutBytes.Length;
        return ShortcutResolution.Resolved(targetPath, targetKind);
    }
}

internal sealed class HostileReadOnlyList<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> repeatingValues;
    private readonly int successfulObservationsBeforeThrow;

    public HostileReadOnlyList(
        IReadOnlyList<T> repeatingValues,
        int successfulObservationsBeforeThrow)
    {
        ArgumentNullException.ThrowIfNull(repeatingValues);
        if (repeatingValues.Count == 0)
        {
            throw new ArgumentException("At least one repeating hostile value is required.", nameof(repeatingValues));
        }

        this.repeatingValues = repeatingValues;
        this.successfulObservationsBeforeThrow = successfulObservationsBeforeThrow;
    }

    public int CountAccesses { get; private set; }
    public int MoveNextCalls { get; private set; }
    public int SuccessfulObservations { get; private set; }

    public int Count
    {
        get
        {
            CountAccesses++;
            return successfulObservationsBeforeThrow + 1;
        }
    }

    public T this[int index] =>
        throw new InvalidOperationException("The hostile input must never be indexed.");

    public IEnumerator<T> GetEnumerator() => new HostileEnumerator(this);

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class HostileEnumerator(HostileReadOnlyList<T> owner) : IEnumerator<T>
    {
        public T Current { get; private set; } = default!;

        object? System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            owner.MoveNextCalls++;
            if (owner.SuccessfulObservations >= owner.successfulObservationsBeforeThrow)
            {
                throw new InvalidOperationException("hostile-enumerator\tcontinued past its raw observation boundary");
            }

            Current = owner.repeatingValues[owner.SuccessfulObservations % owner.repeatingValues.Count];
            owner.SuccessfulObservations++;
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}

internal sealed class CancelingReadOnlyList<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> values;
    private readonly CancellationTokenSource cancellation;

    public CancelingReadOnlyList(
        IReadOnlyList<T> values,
        CancellationTokenSource cancellation)
    {
        this.values = values;
        this.cancellation = cancellation;
    }

    public int MoveNextCalls { get; private set; }

    public int Count => values.Count;

    public T this[int index] => values[index];

    public IEnumerator<T> GetEnumerator() => new CancelingEnumerator(this);

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class CancelingEnumerator(CancelingReadOnlyList<T> owner) : IEnumerator<T>
    {
        private int index = -1;

        public T Current => owner.values[index];

        object? System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            owner.MoveNextCalls++;
            index++;
            if (index >= owner.values.Count)
            {
                return false;
            }

            owner.cancellation.Cancel();
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}

internal sealed class StubWindowsHandleVolumeMetadataReader(WindowsHandleVolumeMetadata metadata)
    : IWindowsHandleVolumeMetadataReader
{
    public WindowsHandleVolumeMetadata Read(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return metadata;
    }
}

internal sealed record TreeSnapshotEntry(
    string RelativePath,
    bool IsDirectory,
    string Bytes,
    FileAttributes Attributes,
    long LastWriteTimeUtcTicks,
    string SecurityDescriptor,
    SnapshotIdentity Identity);

internal readonly record struct SnapshotIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);

internal readonly record struct SynchronizationAccessRule(string Sid, uint AccessMask);

internal sealed record SynchronizationDaclSnapshot(
    bool IsProtected,
    IReadOnlyList<SynchronizationAccessRule> Rules);

#pragma warning disable CA1416
internal static class SynchronizationNative
{
    public const uint MutexAllAccess = 0x001F0001;
    private const uint EventAllAccess = 0x001F0003;
    private const uint ReadControl = 0x00020000;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint WaitObject0 = 0x00000000;

    public static string DeriveStorageMutexName(
        string currentSid,
        PhysicalDirectoryIdentity localRootIdentity)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{currentSid}|{localRootIdentity.VolumeSerialNumber:X16}|" +
            $"{localRootIdentity.FileIdHigh:X16}{localRootIdentity.FileIdLow:X16}");
        try
        {
            var digest = SHA256.HashData(material);
            try
            {
                return "Global\\BlockFerry.AppStorage." + Convert.ToHexString(digest);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public static SafeWaitHandle CreateMutex(string name, string sddl) =>
        CreateSecuredObject(
            sddl,
            (ref NativeSecurityAttributes attributes) =>
                CreateMutexEx(ref attributes, name, 0, MutexAllAccess));

    public static SafeWaitHandle CreateEvent(string name, string sddl) =>
        CreateSecuredObject(
            sddl,
            (ref NativeSecurityAttributes attributes) =>
                CreateEventEx(ref attributes, name, 0, EventAllAccess));

    public static SafeWaitHandle CreateAbandonedMutex(string name, string sddl)
    {
        var handle = CreateMutex(name, sddl);
        uint waitResult = uint.MaxValue;
        var owner = new Thread(() =>
        {
            var referenceAdded = false;
            try
            {
                handle.DangerousAddRef(ref referenceAdded);
                waitResult = WaitForSingleObject(handle.DangerousGetHandle(), 5000);
            }
            finally
            {
                if (referenceAdded)
                {
                    handle.DangerousRelease();
                }
            }
        });
        owner.Start();
        if (!owner.Join(TimeSpan.FromSeconds(10)) || waitResult != WaitObject0)
        {
            handle.Dispose();
            throw new InvalidOperationException(
                $"The fixture thread could not acquire and abandon the global mutex (wait 0x{waitResult:X8}).");
        }

        return handle;
    }

    public static SafeWaitHandle OpenMutexForSecurity(string name)
    {
        var handle = OpenMutex(ReadControl, false, name);
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException(
                $"The fixture could not open the production mutex for DACL inspection (Windows error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        }

        return handle;
    }

    public static SynchronizationDaclSnapshot ReadDacl(SafeWaitHandle handle)
    {
        IntPtr securityDescriptor = IntPtr.Zero;
        var referenceAdded = false;
        try
        {
            handle.DangerousAddRef(ref referenceAdded);
            var status = GetSecurityInfo(
                handle.DangerousGetHandle(),
                SecurityObjectType.KernelObject,
                DaclSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out securityDescriptor);
            if (status != 0 || securityDescriptor == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"The fixture could not read the production mutex DACL (Windows error {status}).");
            }

            var length = checked((int)GetSecurityDescriptorLength(securityDescriptor));
            var bytes = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(securityDescriptor, bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            var rules = new List<SynchronizationAccessRule>();
            if (descriptor.DiscretionaryAcl is not null)
            {
                foreach (GenericAce genericAce in descriptor.DiscretionaryAcl)
                {
                    if (genericAce is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed)
                    {
                        throw new InvalidOperationException("The production mutex DACL contained a non-allow ACE.");
                    }

                    rules.Add(new SynchronizationAccessRule(
                        ace.SecurityIdentifier.Value,
                        unchecked((uint)ace.AccessMask)));
                }
            }

            return new SynchronizationDaclSnapshot(
                (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
                rules.AsReadOnly());
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(securityDescriptor);
            }

            if (referenceAdded)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static SafeWaitHandle CreateSecuredObject(
        string sddl,
        CreateSecuredObjectCallback create)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                1,
                out var securityDescriptor,
                out _))
        {
            throw new InvalidOperationException(
                $"The fixture security descriptor could not be created (Windows error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var attributes = new NativeSecurityAttributes
            {
                Length = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSecurityAttributes>()),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = false,
            };
            var handle = create(ref attributes);
            if (handle.IsInvalid)
            {
                throw new InvalidOperationException(
                    $"The fixture synchronization object could not be created (Windows error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            }

            return handle;
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    private delegate SafeWaitHandle CreateSecuredObjectCallback(ref NativeSecurityAttributes attributes);

    private enum SecurityObjectType
    {
        KernelObject = 6,
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeSecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public bool InheritHandle;
    }

#pragma warning disable SYSLIB1054
    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "CreateMutexExW",
        SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    private static extern SafeWaitHandle CreateMutexEx(
        ref NativeSecurityAttributes mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "CreateEventExW",
        SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    private static extern SafeWaitHandle CreateEventEx(
        ref NativeSecurityAttributes eventAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "OpenMutexW",
        SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    private static extern SafeWaitHandle OpenMutex(
        uint desiredAccess,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "WaitForSingleObject",
        SetLastError = true,
        ExactSpelling = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(
        IntPtr handle,
        SecurityObjectType objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl,
        out IntPtr securityDescriptor);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength", ExactSpelling = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [System.Runtime.InteropServices.DllImport(
        "advapi32.dll",
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
}
#pragma warning restore CA1416

internal static class SnapshotNative
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    public static SnapshotIdentity GetIdentity(string path, bool isDirectory)
    {
        using var handle = CreateFile(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            (isDirectory ? FileFlagBackupSemantics : 0) | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out var information,
                checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<FileIdInfo>())))
        {
            throw new InvalidOperationException("The fixture snapshot could not read a full physical identity.");
        }

        return new SnapshotIdentity(
            information.VolumeSerialNumber,
            information.FileId.LowPart,
            information.FileId.HighPart);
    }

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 0x12,
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public NativeFileId128 FileId;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 16)]
    private struct NativeFileId128
    {
        public ulong LowPart;
        public ulong HighPart;
    }

#pragma warning disable SYSLIB1054
    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true,
        ExactSpelling = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);
#pragma warning restore SYSLIB1054
}

internal sealed class TestProtectedData(byte[] key) : IProtectedData
{
    private readonly byte[] key = key.Length == 32
        ? (byte[])key.Clone()
        : throw new ArgumentException("The fixture protection key must be 32 bytes.", nameof(key));

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy, int maximumOutputBytes)
    {
        if (plaintext.Length > maximumOutputBytes - 29)
        {
            throw new ProtectedDataLimitException("The fixture protected output exceeded its bound.");
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, entropy);
        return [0x01, .. nonce, .. tag, .. ciphertext];
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy, int maximumOutputBytes)
    {
        if (ciphertext.Length < 29 || ciphertext[0] != 0x01)
        {
            throw new CryptographicException("The fixture protected payload is invalid.");
        }

        var plaintext = new byte[ciphertext.Length - 29];
        if (plaintext.Length > maximumOutputBytes)
        {
            throw new ProtectedDataLimitException("The fixture plaintext exceeded its bound.");
        }
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(
            ciphertext.Slice(1, 12),
            ciphertext[29..],
            ciphertext.Slice(13, 16),
            plaintext,
            entropy);
        return plaintext;
    }
}

internal sealed class ExpandingProtectedData(int ciphertextLength) : IProtectedData
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy, int maximumOutputBytes) =>
        ciphertextLength > maximumOutputBytes
            ? throw new ProtectedDataLimitException("The fixture protected output exceeded its bound.")
            : new byte[ciphertextLength];

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy, int maximumOutputBytes) => [];
}

internal sealed class OversizedPlaintextProtectedData(int plaintextLength) : IProtectedData
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy, int maximumOutputBytes) => [0x01];

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy, int maximumOutputBytes) =>
        plaintextLength > maximumOutputBytes
            ? throw new ProtectedDataLimitException("The fixture plaintext exceeded its bound.")
            : new byte[plaintextLength];
}

internal sealed class RejectingProtectedData : IProtectedData
{
    public int CallCount { get; private set; }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy, int maximumOutputBytes)
    {
        CallCount++;
        throw new InvalidOperationException("Protection must not be reached by this fixture.");
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy, int maximumOutputBytes)
    {
        CallCount++;
        throw new InvalidOperationException("Unprotection must not be reached by this fixture.");
    }
}

internal sealed class OversizedWindowsProtectedDataNative : IWindowsProtectedDataNative, IDisposable
{
    private int sequence;
    private IntPtr output = System.Runtime.InteropServices.Marshal.AllocHGlobal(1);

    public OversizedWindowsProtectedDataNative(int outputLength)
    {
        OutputLength = outputLength;
    }

    public int OutputLength { get; }
    public int ManagedCopyCount { get; private set; }
    public int ZeroCount { get; private set; }
    public int LastZeroLength { get; private set; }
    public int FreeCount { get; private set; }
    public int ZeroSequence { get; private set; }
    public int FreeSequence { get; private set; }

    public WindowsProtectedDataNativeResult Transform(byte[] input, byte[] entropy, bool protect) =>
        new(output, checked((uint)OutputLength));

    public void CopyToManaged(IntPtr source, byte[] destination)
    {
        ManagedCopyCount++;
        throw new InvalidOperationException("An oversized native blob must never be copied to managed memory.");
    }

    public void SecureZero(IntPtr data, uint length)
    {
        ZeroCount++;
        LastZeroLength = checked((int)length);
        ZeroSequence = ++sequence;
    }

    public void Free(IntPtr data)
    {
        FreeCount++;
        FreeSequence = ++sequence;
        if (output != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(output);
            output = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (output != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(output);
            output = IntPtr.Zero;
        }
    }
}

internal sealed class CancelingProtectedData(
    IProtectedData inner,
    CancellationTokenSource cancellation) : IProtectedData
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy, int maximumOutputBytes)
    {
        var result = inner.Protect(plaintext, entropy, maximumOutputBytes);
        cancellation.Cancel();
        return result;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy, int maximumOutputBytes) =>
        inner.Unprotect(ciphertext, entropy, maximumOutputBytes);
}

internal sealed class AppStorageInterleavingProbe(
    Action<AppStorageInterleavingPoint, AppStorageInterleavingContext> callback) :
    IAppStorageInterleaving
{
    public void Reach(
        AppStorageInterleavingPoint point,
        AppStorageInterleavingContext context) => callback(point, context);
}
