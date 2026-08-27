using System.Text;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Discovery;

/// <summary>
/// Supplies untrusted remembered-root observations. Production consumes at most
/// 64 raw values plus one overflow observation before filtering or deduplication.
/// </summary>
public sealed record AutomaticCandidateRequest(
    IEnumerable<string> RememberedRoots,
    int MaximumShortcutFiles = 256,
    int MaximumCandidates = 64);

public sealed record DiscoveryCandidate(
    string CandidatePath,
    Pcl2CandidateOrigin Origin,
    string Evidence)
{
    internal PhysicalDirectoryIdentity? Identity { get; init; }
}

public sealed record AutomaticCandidateResult(
    IReadOnlyList<DiscoveryCandidate> Candidates,
    IReadOnlyList<DiscoveryDiagnostic> Diagnostics);

public sealed record InstanceCandidateResolution(
    IReadOnlyList<Pcl2RootCandidate> Candidates,
    IReadOnlyList<DiscoveryDiagnostic> Diagnostics);

public enum DiscoveryDiagnosticCode
{
    CandidatePathInvalid,
    CandidateOutsideCapability,
    CandidateNotRecognized,
    NetworkOrDeviceTargetRejected,
    ShortcutMalformed,
    ShortcutTooLarge,
    ShortcutEnumerationLimitReached,
    ShortcutTargetKindUnknown,
    CandidateLimitReached,
    CandidateEnumerationFailed,
    DiscoveryLimitReached,
}

public sealed record DiscoveryDiagnostic(
    DiscoveryDiagnosticCode Code,
    string Message,
    string? Path = null);

public enum ShortcutTargetKind
{
    Unknown,
    File,
    Directory,
}

public sealed record ShortcutResolution(
    string? TargetPath,
    ShortcutTargetKind TargetKind,
    DiscoveryDiagnostic? Diagnostic)
{
    public bool IsResolved =>
        TargetPath is not null &&
        TargetKind != ShortcutTargetKind.Unknown &&
        Diagnostic is null;

    public static ShortcutResolution Resolved(
        string targetPath,
        ShortcutTargetKind targetKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (targetKind == ShortcutTargetKind.Unknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetKind),
                "A resolved shortcut requires positive file or directory evidence.");
        }

        return new ShortcutResolution(targetPath, targetKind, null);
    }

    public static ShortcutResolution Rejected(
        DiscoveryDiagnosticCode code,
        string message) =>
        new(null, ShortcutTargetKind.Unknown, new DiscoveryDiagnostic(code, message));
}

internal static class DiscoveryPathPolicy
{
    public static bool TryNormalizeLocalAbsolute(
        string? candidate,
        out string normalized,
        out DiscoveryDiagnostic diagnostic)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            diagnostic = new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidatePathInvalid,
                "The candidate path is empty.",
                candidate);
            return false;
        }

        var trimmed = candidate.Trim().Trim('"');
        if (trimmed.StartsWith("\\\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            diagnostic = new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.NetworkOrDeviceTargetRejected,
                "UNC, device, and extended-device paths are not eligible for automatic discovery.",
                candidate);
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(trimmed))
            {
                diagnostic = new DiscoveryDiagnostic(
                    DiscoveryDiagnosticCode.CandidatePathInvalid,
                    "The candidate path is not fully qualified.",
                    candidate);
                return false;
            }

            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            diagnostic = new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidatePathInvalid,
                string.Empty);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostic = new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidatePathInvalid,
                $"The candidate path could not be normalized: {DiagnosticText.EscapeTechnicalValue(exception.Message)}",
                candidate);
            return false;
        }
    }
}

internal static class DiagnosticText
{
    private const int MaximumTechnicalValueCharacters = 160;

    public static string EscapeTechnicalValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = new StringBuilder(Math.Min(value.Length, MaximumTechnicalValueCharacters));
        foreach (var character in value)
        {
            var representation = character switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\u{(int)character:X4}",
                _ => character.ToString(),
            };
            if (escaped.Length + representation.Length > MaximumTechnicalValueCharacters)
            {
                escaped.Append("...");
                break;
            }

            escaped.Append(representation);
        }

        return escaped.ToString();
    }
}
