using DotnetPkgsAiDocs.Analysis;

namespace DotnetPkgsAiDocs.Output;

/// <summary>
/// Generates Mermaid diagram markup for dependency graphs.
/// </summary>
public static class MermaidGenerator
{
    /// <summary>
    /// Generates a Mermaid graph TD (top-down) diagram for a package's transitive dependencies.
    /// 1P dependencies get solid arrows; external dependency families get dashed arrows and are grouped.
    /// </summary>
    public static string GenerateDependencyGraph(
        DependencyResult result,
        IReadOnlyList<string> firstPartyPrefixes)
    {
        if (result.Dependencies.Count == 0)
            return "";

        var lines = new List<string>();
        lines.Add("graph TD");

        var rootId = SanitizeId(result.PackageId);
        var rootLabel = ShortName(result.PackageId, firstPartyPrefixes);
        lines.Add($"    {rootId}[\"{rootLabel} {result.PackageVersion}\"]");

        // Classify dependencies
        var firstParty = result.Dependencies
            .Where(d => IsFirstParty(d.Id, firstPartyPrefixes))
            .ToList();

        var external = result.Dependencies
            .Where(d => !IsFirstParty(d.Id, firstPartyPrefixes))
            .ToList();

        // Add 1P dependencies with solid arrows
        foreach (var dep in firstParty)
        {
            var depId = SanitizeId(dep.Id);
            var depLabel = ShortName(dep.Id, firstPartyPrefixes);
            lines.Add($"    {rootId} --> {depId}[\"{depLabel} {dep.ResolvedVersion}\"]");
        }

        // Group external dependencies by family to avoid diagram explosion
        var families = GroupByFamily(external);
        foreach (var family in families.OrderBy(f => f.Key))
        {
            var familyId = SanitizeId(family.Key);
            var versionRange = GetVersionSummary(family.Value);
            lines.Add($"    {rootId} -.-> |\"{versionRange}\"| {familyId}[\"{family.Key}\"]");
        }

        lines.Add("");
        return string.Join(Environment.NewLine, lines);
    }

    private static Dictionary<string, List<ResolvedDependency>> GroupByFamily(
        IReadOnlyList<ResolvedDependency> deps)
    {
        var families = new Dictionary<string, List<ResolvedDependency>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dep in deps)
        {
            var family = GetFamily(dep.Id);
            if (!families.TryGetValue(family, out var list))
            {
                list = [];
                families[family] = list;
            }
            list.Add(dep);
        }

        return families;
    }

    private static string GetFamily(string packageId)
    {
        // Group by common prefixes
        var knownFamilies = new[]
        {
            "Microsoft.Extensions.",
            "Microsoft.IdentityModel.",
            "Microsoft.Identity.Web",
            "Microsoft.Identity.",
            "System.",
            "OpenTelemetry.",
            "Microsoft.Bcl.",
            "Azure.",
            "Polly."
        };

        foreach (var prefix in knownFamilies)
        {
            if (packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return prefix.TrimEnd('.') + ".*";
            }
        }

        return packageId;
    }

    private static string GetVersionSummary(List<ResolvedDependency> deps)
    {
        if (deps.Count == 1)
            return $">= {deps[0].ResolvedVersion}";

        var versions = deps.Select(d => d.ResolvedVersion).Distinct().OrderBy(v => v).ToList();
        return $">= {versions[0]}";
    }

    private static string SanitizeId(string name)
    {
        // Mermaid node IDs can't contain dots or special chars
        return name.Replace(".", "_").Replace("-", "_").Replace("*", "star").Replace(" ", "_");
    }

    private static string ShortName(string packageId, IReadOnlyList<string> firstPartyPrefixes)
    {
        // Remove common prefix for readability
        foreach (var prefix in firstPartyPrefixes)
        {
            if (packageId.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                return packageId[(prefix.Length + 1)..];
            }
            if (packageId.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return packageId.Split('.').Last();
            }
        }
        return packageId;
    }

    private static bool IsFirstParty(string packageId, IReadOnlyList<string> prefixes)
    {
        return prefixes.Any(p => packageId.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
