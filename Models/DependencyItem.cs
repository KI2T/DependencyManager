namespace DependencyManager.Models;
using System.Text.RegularExpressions;
internal sealed class DependencyItem
{
    public required string Configuration { get; init; }
    public required string Group { get; init; }
    public required string Artifact { get; init; }
    public required string OriginalLine { get; init; }
    public string RequestedVersion { get; init; } = "";
    public string? CurrentManagedVersion { get; set; }
    public string? BomVersion { get; set; }
    public string? MetadataError { get; set; }
    public string? ParseWarning { get; init; }
    public IReadOnlyList<string> AvailableVersions { get; set; } = Array.Empty<string>();

    public bool IsUnverified => MetadataError is not null || ParseWarning is not null || BomVersion is null;
    public bool IsIncompatibleWithBom => CompareReleaseLine(RequestedVersion, BomVersion) == -1;
    public bool IsOlderPatchThanBom => CompareReleaseLine(RequestedVersion, BomVersion) == 1;

    public string CurrentVersion => !string.IsNullOrWhiteSpace(RequestedVersion) &&
                                    !RequestedVersion.Contains('$') && !RequestedVersion.Contains('+') &&
                                    !RequestedVersion.Contains('[') && !RequestedVersion.Contains(']') &&
                                    !RequestedVersion.Equals("latest.release", StringComparison.OrdinalIgnoreCase)
        ? RequestedVersion
        : !string.IsNullOrWhiteSpace(CurrentManagedVersion) ? CurrentManagedVersion! :
            !string.IsNullOrWhiteSpace(RequestedVersion) ? "(variable / range)" : "(not specified)";

    public string DisplayArtifact => IsUnverified ? "? " + Artifact : Artifact;

    // -1: different major/minor release line; 1: same line but lower patch; 0: no conflict detected.
    private static int CompareReleaseLine(string requested, string? bom)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(bom))
            return 0;
        Match requestedMatch = System.Text.RegularExpressions.Regex.Match(requested, @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$");
        Match bomMatch = System.Text.RegularExpressions.Regex.Match(bom, @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$");
        if (!requestedMatch.Success || !bomMatch.Success)
            return 0;
        int requestedMajor = int.Parse(requestedMatch.Groups["major"].Value);
        int requestedMinor = int.Parse(requestedMatch.Groups["minor"].Value);
        int bomMajor = int.Parse(bomMatch.Groups["major"].Value);
        int bomMinor = int.Parse(bomMatch.Groups["minor"].Value);
        if (requestedMajor != bomMajor || requestedMinor != bomMinor)
            return -1;
        return int.Parse(requestedMatch.Groups["patch"].Value) < int.Parse(bomMatch.Groups["patch"].Value) ? 1 : 0;
    }
}

internal sealed class VersionOption
{
    public required string Version { get; init; }
    public bool IsBomPreferred { get; init; }
    public bool IsCompatibilityUnknown { get; init; }
    public string Display => IsBomPreferred ? Version + "   ★ BOM" : IsCompatibilityUnknown ? Version + "   (?)" : Version;
}

internal sealed class GradleFileAnalysis
{
    public string FilePath { get; init; } = "";
    public string? SpringBootVersion { get; init; }
    public bool HasDependencyManagementPlugin { get; init; }
    public Dictionary<string, string> PropertyOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DependencyItem> Dependencies { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
