using System.IO.Compression;
using System.Xml.Linq;

namespace DotnetPkgsAiDocs.Analysis;

/// <summary>
/// Extracts package metadata from a .nupkg file (which is a zip archive containing a .nuspec).
/// </summary>
public static class NupkgExtractor
{
    /// <summary>
    /// Opens a .nupkg file and extracts package ID, version, target frameworks, and direct dependencies.
    /// </summary>
    public static PackageInfo? Extract(string nupkgPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(nupkgPath);

            // Find the .nuspec file
            var nuspecEntry = archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
                !e.FullName.Contains('/'));

            if (nuspecEntry == null)
            {
                Console.Error.WriteLine($"  Warning: No .nuspec found in {Path.GetFileName(nupkgPath)}");
                return null;
            }

            using var stream = nuspecEntry.Open();
            var doc = XDocument.Load(stream);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var metadata = doc.Root?.Element(ns + "metadata");
            if (metadata == null) return null;

            var id = metadata.Element(ns + "id")?.Value ?? "";
            var version = metadata.Element(ns + "version")?.Value ?? "";

            // Parse dependencies per TFM
            var depsByTfm = new Dictionary<string, IReadOnlyList<PackageDependency>>();
            var depsElement = metadata.Element(ns + "dependencies");

            if (depsElement != null)
            {
                var groups = depsElement.Elements(ns + "group");
                foreach (var group in groups)
                {
                    var tfm = group.Attribute("targetFramework")?.Value ?? "any";
                    tfm = NormalizeTfm(tfm);

                    var deps = group.Elements(ns + "dependency")
                        .Select(d => new PackageDependency(
                            d.Attribute("id")?.Value ?? "",
                            d.Attribute("version")?.Value ?? ""))
                        .Where(d => !string.IsNullOrEmpty(d.Id))
                        .ToList();

                    depsByTfm[tfm] = deps;
                }

                // Handle flat dependencies (no groups)
                if (!groups.Any())
                {
                    var deps = depsElement.Elements(ns + "dependency")
                        .Select(d => new PackageDependency(
                            d.Attribute("id")?.Value ?? "",
                            d.Attribute("version")?.Value ?? ""))
                        .Where(d => !string.IsNullOrEmpty(d.Id))
                        .ToList();

                    if (deps.Count > 0)
                    {
                        depsByTfm["any"] = deps;
                    }
                }
            }

            // Also discover TFMs from lib/ folder structure in the nupkg
            var libTfms = archive.Entries
                .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
                .Select(e =>
                {
                    var parts = e.FullName.Split('/');
                    return parts.Length >= 2 ? NormalizeTfm(parts[1]) : null;
                })
                .Where(t => t != null && t != "any")
                .Select(t => t!)
                .Distinct()
                .ToList();

            // Merge TFMs from both sources
            var allTfms = depsByTfm.Keys
                .Union(libTfms!)
                .Where(t => t != "any")
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            // If no TFMs found but we have "any" deps, use that
            if (allTfms.Count == 0 && depsByTfm.ContainsKey("any"))
            {
                allTfms.Add("any");
            }

            if (allTfms.Count == 0)
            {
                Console.Error.WriteLine($"  Warning: No target frameworks found in {id} {version}");
                return null;
            }

            return new PackageInfo(id, version, allTfms, depsByTfm, nupkgPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Error extracting {Path.GetFileName(nupkgPath)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Discovers which TFMs have assemblies in the lib/ folder of a nupkg.
    /// </summary>
    public static IReadOnlyList<string> GetLibTfms(string nupkgPath)
    {
        using var archive = ZipFile.OpenRead(nupkgPath);
        return archive.Entries
            .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(e =>
            {
                var parts = e.FullName.Split('/');
                return parts.Length >= 2 ? NormalizeTfm(parts[1]) : null;
            })
            .Where(t => t != null)
            .Distinct()!
            .ToList()!;
    }

    /// <summary>
    /// Normalizes TFM strings to short form (e.g., ".NETFramework,Version=v4.6.2" -> "net462").
    /// </summary>
    private static string NormalizeTfm(string tfm)
    {
        // Already short form
        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase) && !tfm.Contains(','))
            return tfm.ToLowerInvariant();

        // Long form: .NETFramework,Version=v4.6.2
        if (tfm.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
        {
            var versionPart = tfm.Split("Version=v", StringSplitOptions.None);
            if (versionPart.Length == 2)
            {
                return "net" + versionPart[1].Replace(".", "");
            }
        }

        // Long form: .NETCoreApp,Version=v8.0
        if (tfm.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            var versionPart = tfm.Split("Version=v", StringSplitOptions.None);
            if (versionPart.Length == 2)
            {
                return "net" + versionPart[1];
            }
        }

        // Long form: .NETStandard,Version=v2.0
        if (tfm.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase))
        {
            var versionPart = tfm.Split("Version=v", StringSplitOptions.None);
            if (versionPart.Length == 2)
            {
                return "netstandard" + versionPart[1];
            }
        }

        return tfm.ToLowerInvariant();
    }
}
