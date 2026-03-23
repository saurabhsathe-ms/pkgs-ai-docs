using System.Diagnostics;
using System.Text.Json;

namespace DotnetPkgsAiDocs.Analysis;

/// <summary>
/// Resolves transitive dependencies for a package × TFM combination by creating
/// a temporary .csproj, running 'dotnet restore', and parsing project.assets.json.
/// </summary>
public static class TransitiveDepsResolver
{
    /// <summary>
    /// Resolves the full transitive dependency tree for a package targeting a specific framework.
    /// </summary>
    public static async Task<DependencyResult?> ResolveAsync(
        PackageInfo package,
        string targetFramework,
        IReadOnlyList<string> nugetSources)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pkgs-ai-docs", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            // Create a temporary nuget.config with all sources
            var nugetConfigPath = Path.Combine(tempDir, "nuget.config");
            WriteTempNuGetConfig(nugetConfigPath, nugetSources);

            // Create a temporary .csproj that references this single package
            var csprojPath = Path.Combine(tempDir, "resolve.csproj");
            WriteTempCsproj(csprojPath, package.Id, package.Version, targetFramework);

            // Run dotnet restore
            var restoreResult = await RunDotnetRestoreAsync(tempDir);
            if (!restoreResult)
            {
                Console.Error.WriteLine($"    Warning: dotnet restore failed for {package.Id} {package.Version} -> {targetFramework}");
                return null;
            }

            // Parse project.assets.json
            var assetsPath = Path.Combine(tempDir, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
            {
                Console.Error.WriteLine($"    Warning: project.assets.json not found after restore");
                return null;
            }

            var dependencies = ParseAssetsJson(assetsPath, targetFramework, package.Id);
            return new DependencyResult(package.Id, package.Version, targetFramework, dependencies);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    Error resolving {package.Id} -> {targetFramework}: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private static void WriteTempNuGetConfig(string path, IReadOnlyList<string> sources)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        writer.WriteLine("<configuration>");
        writer.WriteLine("  <packageSources>");
        writer.WriteLine("    <clear />");
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var name = Directory.Exists(source) ? $"local-{i}" : $"remote-{i}";
            writer.WriteLine($"    <add key=\"{name}\" value=\"{EscapeXml(source)}\" />");
        }
        writer.WriteLine("  </packageSources>");
        writer.WriteLine("</configuration>");
    }

    private static void WriteTempCsproj(string path, string packageId, string version, string tfm)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        writer.WriteLine("  <PropertyGroup>");
        writer.WriteLine($"    <TargetFramework>{tfm}</TargetFramework>");
        writer.WriteLine("    <OutputType>Library</OutputType>");
        // Disable warnings that slow down restore for our temp project
        writer.WriteLine("    <NoWarn>NU1701;NU1603;NU1605</NoWarn>");
        writer.WriteLine("  </PropertyGroup>");
        writer.WriteLine("  <ItemGroup>");
        writer.WriteLine($"    <PackageReference Include=\"{EscapeXml(packageId)}\" Version=\"{EscapeXml(version)}\" />");
        writer.WriteLine("  </ItemGroup>");
        writer.WriteLine("</Project>");
    }

    private static async Task<bool> RunDotnetRestoreAsync(string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "restore --no-build --verbosity quiet",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return false;

        // Read output to prevent deadlocks
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            // Only show details on failure
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                // Trim to first few lines to avoid noise
                var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines.Take(5))
                {
                    Console.Error.WriteLine($"    {line.Trim()}");
                }
            }
            return false;
        }

        return true;
    }

    private static IReadOnlyList<ResolvedDependency> ParseAssetsJson(
        string assetsPath, string targetFramework, string rootPackageId)
    {
        var json = File.ReadAllText(assetsPath);
        using var doc = JsonDocument.Parse(json);

        var targets = doc.RootElement.GetProperty("targets");

        // Find the matching TFM entry — the key might be slightly different from what we passed
        // (e.g., "net8.0" vs ".NETCoreApp,Version=v8.0")
        JsonElement? targetElement = null;
        foreach (var prop in targets.EnumerateObject())
        {
            if (prop.Name.Contains(targetFramework, StringComparison.OrdinalIgnoreCase) ||
                NormalizeTfmKey(prop.Name) == targetFramework.ToLowerInvariant())
            {
                targetElement = prop.Value;
                break;
            }
        }

        // Fallback: take the first target if we have exactly one
        if (targetElement == null)
        {
            var allTargets = targets.EnumerateObject().ToList();
            if (allTargets.Count == 1)
            {
                targetElement = allTargets[0].Value;
            }
        }

        if (targetElement == null)
        {
            Console.Error.WriteLine($"    Warning: TFM '{targetFramework}' not found in project.assets.json");
            return [];
        }

        var deps = new List<ResolvedDependency>();
        foreach (var pkg in targetElement.Value.EnumerateObject())
        {
            // Key format: "PackageId/Version"
            var parts = pkg.Name.Split('/');
            if (parts.Length != 2) continue;

            var pkgId = parts[0];
            var pkgVersion = parts[1];

            // Skip the root package itself
            if (pkgId.Equals(rootPackageId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Get the requested version range if available
            var requestedRange = "";
            if (pkg.Value.TryGetProperty("dependencies", out _))
            {
                // This is available but complex — for now use the resolved version
                requestedRange = $">= {pkgVersion}";
            }

            deps.Add(new ResolvedDependency(pkgId, pkgVersion, requestedRange));
        }

        return deps.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NormalizeTfmKey(string key)
    {
        // Handle ".NETCoreApp,Version=v8.0" → "net8.0"
        if (key.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            var v = key.Split("Version=v");
            if (v.Length == 2) return "net" + v[1].ToLowerInvariant();
        }
        if (key.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
        {
            var v = key.Split("Version=v");
            if (v.Length == 2) return "net" + v[1].Replace(".", "").ToLowerInvariant();
        }
        if (key.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase))
        {
            var v = key.Split("Version=v");
            if (v.Length == 2) return "netstandard" + v[1].ToLowerInvariant();
        }
        return key.ToLowerInvariant();
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
