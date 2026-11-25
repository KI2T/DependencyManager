using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DependencyManager.Services;

internal sealed class BomResolver
{
    private readonly MavenCentralClient _maven;
    private readonly Dictionary<string, Dictionary<string, string>> _resolvedPomCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _pomPropertiesCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _activePropertyOverrides = new Dictionary<string, string>();

    public BomResolver(MavenCentralClient maven) => _maven = maven;

    public async Task<IReadOnlyDictionary<string, string>> ResolveSpringBootBomAsync(
        string bootVersion,
        IReadOnlyDictionary<string, string> propertyOverrides,
        CancellationToken cancellationToken)
    {
        // Resolved POMs depend on property overrides, so keep this in-memory cache scoped to one analysis.
        _resolvedPomCache.Clear();
        _pomPropertiesCache.Clear();
        _activePropertyOverrides = propertyOverrides;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return await ResolvePomAsync("org.springframework.boot", "spring-boot-dependencies", bootVersion, visited, cancellationToken);
    }

    private async Task<Dictionary<string, string>> ResolvePomAsync(
        string group,
        string artifact,
        string version,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        string coordinate = $"{group}:{artifact}:{version}";
        if (_resolvedPomCache.TryGetValue(coordinate, out Dictionary<string, string>? cached))
            return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(coordinate))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string pomText = await _maven.GetPomAsync(group, artifact, version, cancellationToken);
        XDocument document = XDocument.Parse(pomText);
        XElement? project = document.Root;
        if (project is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var managed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        XElement? parent = Child(project, "parent");
        if (parent is not null)
        {
            string parentGroup = Value(parent, "groupId");
            string parentArtifact = Value(parent, "artifactId");
            string parentVersion = Value(parent, "version");
            if (parentGroup.Length > 0 && parentArtifact.Length > 0 && parentVersion.Length > 0)
            {
                try
                {
                    Dictionary<string, string> parentManaged = await ResolvePomAsync(parentGroup, parentArtifact, parentVersion, visited, cancellationToken);
                    foreach (var pair in parentManaged)
                        managed[pair.Key] = pair.Value;
                    string parentCoordinate = $"{parentGroup}:{parentArtifact}:{parentVersion}";
                    if (_pomPropertiesCache.TryGetValue(parentCoordinate, out Dictionary<string, string>? parentProperties))
                    {
                        foreach (var pair in parentProperties)
                            properties[pair.Key] = pair.Value;
                    }
                }
                catch
                {
                    // Parent POMs may be unavailable in Maven Central. Continue with the child POM.
                }
            }
        }

        XElement? propertiesElement = Child(project, "properties");
        if (propertiesElement is not null)
        {
            foreach (XElement property in propertiesElement.Elements())
                properties[property.Name.LocalName] = property.Value.Trim();
        }

        string projectVersion = Value(project, "version");
        string projectGroup = Value(project, "groupId");
        if (projectGroup.Length == 0 && parent is not null)
            projectGroup = Value(parent, "groupId");
        if (projectVersion.Length == 0 && parent is not null)
            projectVersion = Value(parent, "version");
        properties["project.version"] = projectVersion;
        properties["pom.version"] = projectVersion;
        properties["project.groupId"] = projectGroup;
        properties["pom.groupId"] = projectGroup;
        if (parent is not null)
        {
            properties["parent.version"] = Value(parent, "version");
            properties["project.parent.version"] = Value(parent, "version");
        }

        // Resolve property values after the current POM has been read.
        foreach ((string key, string value) in _activePropertyOverrides)
        {
            if (properties.ContainsKey(key))
                properties[key] = value;
        }

        foreach (string key in properties.Keys.ToList())
            properties[key] = ResolveProperties(properties[key], properties, projectVersion, 0);

        XElement? dependencyManagement = Child(project, "dependencyManagement");
        XElement? dependencies = dependencyManagement is null ? null : Child(dependencyManagement, "dependencies");
        if (dependencies is not null)
        {
            var normalDependencies = new List<(string Group, string Artifact, string Version)>();
            foreach (XElement dependency in dependencies.Elements().Where(element => element.Name.LocalName == "dependency"))
            {
                string depGroup = ResolveProperties(Value(dependency, "groupId"), properties, projectVersion, 0);
                string depArtifact = ResolveProperties(Value(dependency, "artifactId"), properties, projectVersion, 0);
                string depVersion = ResolveProperties(Value(dependency, "version"), properties, projectVersion, 0);
                string type = Value(dependency, "type");
                string scope = Value(dependency, "scope");
                if (depGroup.Length == 0 || depArtifact.Length == 0 || depVersion.Length == 0 || depVersion.Contains("${", StringComparison.Ordinal))
                    continue;

                if (type.Equals("pom", StringComparison.OrdinalIgnoreCase) && scope.Equals("import", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Dictionary<string, string> imported = await ResolvePomAsync(depGroup, depArtifact, depVersion, visited, cancellationToken);
                        foreach (var pair in imported)
                            managed[pair.Key] = pair.Value;
                    }
                    catch
                    {
                        // A missing imported BOM must not prevent other managed versions from being shown.
                    }
                }
                else
                {
                    normalDependencies.Add((depGroup, depArtifact, depVersion));
                }
            }

            // Entries declared directly by this BOM override entries imported from nested BOMs.
            foreach (var entry in normalDependencies)
                managed[$"{entry.Group}:{entry.Artifact}"] = entry.Version;
        }

        _pomPropertiesCache[coordinate] = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase);
        _resolvedPomCache[coordinate] = new Dictionary<string, string>(managed, StringComparer.OrdinalIgnoreCase);
        return managed;
    }

    private static string ResolveProperties(string value, IReadOnlyDictionary<string, string> properties, string projectVersion, int depth)
    {
        if (depth > 8 || string.IsNullOrEmpty(value))
            return value;
        return Regex.Replace(value, @"\$\{(?<key>[^}]+)\}", match =>
        {
            string key = match.Groups["key"].Value;
            if (properties.TryGetValue(key, out string? replacement) && replacement != value)
                return ResolveProperties(replacement, properties, projectVersion, depth + 1);
            if (key is "project.version" or "pom.version")
                return projectVersion;
            return match.Value;
        });
    }

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName);

    private static string Value(XElement element, string localName) => Child(element, localName)?.Value.Trim() ?? "";
}
