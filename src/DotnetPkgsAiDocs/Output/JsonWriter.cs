using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetPkgsAiDocs.Analysis;

namespace DotnetPkgsAiDocs.Output;

/// <summary>
/// Writes dependency graphs and public API data as structured JSON for programmatic consumption.
/// </summary>
public static class JsonWriter
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void WriteDependencyGraph(
        string outputPath,
        IReadOnlyList<DependencyResult> results,
        IReadOnlyList<string> firstPartyPrefixes)
    {
        var output = results.Select(r => new
        {
            r.PackageId,
            r.PackageVersion,
            r.TargetFramework,
            FirstPartyDependencies = r.Dependencies
                .Where(d => IsFirstParty(d.Id, firstPartyPrefixes))
                .Select(d => new { d.Id, d.ResolvedVersion })
                .ToList(),
            ExternalDependencies = r.Dependencies
                .Where(d => !IsFirstParty(d.Id, firstPartyPrefixes))
                .Select(d => new { d.Id, d.ResolvedVersion })
                .ToList()
        }).ToList();

        var json = JsonSerializer.Serialize(output, s_options);
        File.WriteAllText(outputPath, json);
    }

    public static void WritePublicApi(
        string outputPath,
        IReadOnlyList<PublicApiResult> results)
    {
        var output = results.Select(r => new
        {
            r.PackageId,
            r.PackageVersion,
            r.TargetFramework,
            r.ApiLines
        }).ToList();

        var json = JsonSerializer.Serialize(output, s_options);
        File.WriteAllText(outputPath, json);
    }

    private static bool IsFirstParty(string packageId, IReadOnlyList<string> prefixes)
    {
        return prefixes.Any(p => packageId.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
