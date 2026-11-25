using System.Text.RegularExpressions;
using DependencyManager.Models;

namespace DependencyManager.Services;

internal static class BuildGradleParser
{
    private static readonly Regex BootPluginRegex = new(
        "id\\s*\\(?\\s*['\\\"]org\\.springframework\\.boot['\\\"]\\s*\\)?\\s+version\\s+['\\\"](?<version>[^'\\\"]+)['\\\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DependencyConfigurationRegex = new(
        @"^\s*(?<configuration>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\(\s*)?(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex QuotedValueRegex = new(
        "['\\\"](?<value>[^'\\\"]+)['\\\"]",
        RegexOptions.Compiled);

    private static readonly Regex PropertyOverrideRegex = new(
        "(?:ext\\s*\\[\\s*['\\\"](?<name>[^'\\\"]+)['\\\"]\\s*\\]|ext\\.(?<name2>[A-Za-z0-9_.-]+))\\s*=\\s*['\\\"](?<value>[^'\\\"]+)['\\\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> DependencyConfigurations = new(StringComparer.OrdinalIgnoreCase)
    {
        "implementation", "api", "compileOnly", "runtimeOnly", "runtime", "compile",
        "testImplementation", "testCompileOnly", "testRuntimeOnly", "testRuntime",
        "testCompile", "annotationProcessor", "testAnnotationProcessor", "developmentOnly",
        "providedCompile", "providedRuntime", "classpath"
    };

    public static GradleFileAnalysis Parse(string filePath, string text)
    {
        string? bootVersion = null;
        bool hasDependencyManagement = false;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new List<DependencyItem>();
        var warnings = new List<string>();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        // Read simple ext property overrides first, regardless of where they appear in the file.
        foreach (string propertyLine in lines)
        {
            string trimmedPropertyLine = propertyLine.Trim();
            if (trimmedPropertyLine.Length == 0 || trimmedPropertyLine.StartsWith("//", StringComparison.Ordinal))
                continue;
            Match propertyMatch = PropertyOverrideRegex.Match(Regex.Replace(propertyLine, @"\s+//.*$", ""));
            if (propertyMatch.Success)
            {
                string name = propertyMatch.Groups["name"].Success
                    ? propertyMatch.Groups["name"].Value
                    : propertyMatch.Groups["name2"].Value;
                properties[name] = propertyMatch.Groups["value"].Value;
            }
        }

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            // Strip trailing line comments only for parsing; OriginalLine keeps the exact source line.
            string parseLine = Regex.Replace(line, @"\s+//.*$", "");

            Match bootMatch = BootPluginRegex.Match(parseLine);
            if (bootMatch.Success)
                bootVersion = bootMatch.Groups["version"].Value.Trim();

            if (parseLine.Contains("io.spring.dependency-management", StringComparison.OrdinalIgnoreCase))
                hasDependencyManagement = true;

            Match configMatch = DependencyConfigurationRegex.Match(parseLine);
            if (!configMatch.Success)
                continue;

            string configuration = configMatch.Groups["configuration"].Value;
            if (!DependencyConfigurations.Contains(configuration))
                continue;

            string rest = configMatch.Groups["rest"].Value;
            if (rest.StartsWith("files(", StringComparison.OrdinalIgnoreCase) ||
                rest.StartsWith("fileTree(", StringComparison.OrdinalIgnoreCase) ||
                rest.StartsWith("project(", StringComparison.OrdinalIgnoreCase))
                continue;

            Match quoteMatch = QuotedValueRegex.Match(rest);
            if (!quoteMatch.Success)
                continue;

            string notation = quoteMatch.Groups["value"].Value.Trim();
            if (notation.StartsWith("$", StringComparison.Ordinal) || notation.Contains("libs.", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Line {index + 1}: dependency uses a variable/catalog alias and could not be resolved from build.gradle alone: {trimmed}");
                continue;
            }

            // Strip Maven artifact extension/classifier decorations; the first three fields are group/artifact/version.
            string coordinate = notation.Split('@')[0];
            string[] parts = coordinate.Split(':');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                if (notation.Contains(':'))
                    warnings.Add($"Line {index + 1}: unrecognized dependency notation: {trimmed}");
                continue;
            }

            string group = parts[0].Trim();
            string artifact = parts[1].Trim();
            if (!Regex.IsMatch(group, @"^[A-Za-z0-9_.-]+$") || !Regex.IsMatch(artifact, @"^[A-Za-z0-9_.-]+$"))
            {
                warnings.Add($"Line {index + 1}: invalid Maven group/artifact coordinates: {trimmed}");
                continue;
            }
            string requestedVersion = parts.Length >= 3 ? parts[2].Trim() : "";

            string? parseWarning = null;
            if (requestedVersion.Contains("${", StringComparison.Ordinal) || requestedVersion.Contains('$'))
            {
                string propertyName = Regex.Match(requestedVersion, @"\$\{(?<name>[^}]+)\}").Groups["name"].Value;
                if (propertyName.Length > 0 && properties.TryGetValue(propertyName, out string? resolved))
                    requestedVersion = resolved;
                else
                {
                    parseWarning = $"Version expression '{requestedVersion}' was not resolved from build.gradle.";
                    warnings.Add($"Line {index + 1}: {parseWarning}");
                }
            }
            else if (requestedVersion.Contains('+') || requestedVersion.Contains('[') || requestedVersion.Contains(']') ||
                     requestedVersion.Equals("latest.release", StringComparison.OrdinalIgnoreCase))
            {
                parseWarning = $"Dynamic version selector '{requestedVersion}' cannot be resolved without running Gradle.";
                warnings.Add($"Line {index + 1}: {parseWarning}");
            }

            dependencies.Add(new DependencyItem
            {
                Configuration = configuration,
                Group = group,
                Artifact = artifact,
                RequestedVersion = requestedVersion,
                OriginalLine = line,
                ParseWarning = parseWarning
            });
        }

        return new GradleFileAnalysis
        {
            FilePath = Path.GetFullPath(filePath),
            SpringBootVersion = bootVersion,
            HasDependencyManagementPlugin = hasDependencyManagement,
            PropertyOverrides = properties,
            Dependencies = dependencies,
            Warnings = warnings
        };
    }
}
