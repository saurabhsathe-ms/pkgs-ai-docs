using System.CommandLine;
using DotnetPkgsAiDocs.Analysis;
using DotnetPkgsAiDocs.Output;

namespace DotnetPkgsAiDocs.Commands;

/// <summary>
/// Command to generate transitive dependency graphs from .nupkg files.
/// </summary>
internal static class DepsCommand
{
    public static Command Create()
    {
        var command = new Command("deps", "Generate transitive dependency graphs from .nupkg files.");

        command.AddArgument(CommonOptions.InputFolder);
        command.AddOption(CommonOptions.OutputFolder);
        command.AddOption(CommonOptions.NuGetSources);
        command.AddOption(CommonOptions.FirstPartyPrefixes);
        command.AddOption(CommonOptions.Format);

        command.SetHandler(ExecuteAsync,
            CommonOptions.InputFolder,
            CommonOptions.OutputFolder,
            CommonOptions.NuGetSources,
            CommonOptions.FirstPartyPrefixes,
            CommonOptions.Format);

        return command;
    }

    internal static async Task ExecuteAsync(
        DirectoryInfo inputFolder,
        DirectoryInfo outputFolder,
        string[]? nugetSources,
        string[]? firstPartyPrefixes,
        string format)
    {
        if (!inputFolder.Exists)
        {
            Console.Error.WriteLine($"Input folder does not exist: {inputFolder.FullName}");
            return;
        }

        outputFolder.Create();

        var nupkgFiles = inputFolder.GetFiles("*.nupkg");
        if (nupkgFiles.Length == 0)
        {
            Console.Error.WriteLine($"No .nupkg files found in {inputFolder.FullName}");
            return;
        }

        Console.WriteLine($"Found {nupkgFiles.Length} .nupkg files in {inputFolder.FullName}");

        // Extract package metadata from each nupkg
        var packages = new List<PackageInfo>();
        foreach (var nupkg in nupkgFiles)
        {
            // Skip symbol packages
            if (nupkg.Name.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
                continue;

            var info = NupkgExtractor.Extract(nupkg.FullName);
            if (info != null)
            {
                packages.Add(info);
                Console.WriteLine($"  {info.Id} {info.Version} [{string.Join(", ", info.TargetFrameworks)}]");
            }
        }

        Console.WriteLine($"\nResolving transitive dependencies for {packages.Count} packages...");

        // Build the list of NuGet sources: input folder + user sources + nuget.org
        var sources = new List<string> { inputFolder.FullName };
        if (nugetSources != null)
            sources.AddRange(nugetSources);
        sources.Add("https://api.nuget.org/v3/index.json");

        // Resolve transitive dependencies
        var allResults = new List<DependencyResult>();
        foreach (var pkg in packages)
        {
            foreach (var tfm in pkg.TargetFrameworks)
            {
                Console.WriteLine($"  Resolving {pkg.Id} {pkg.Version} -> {tfm}...");
                var result = await TransitiveDepsResolver.ResolveAsync(pkg, tfm, sources);
                if (result != null)
                {
                    allResults.Add(result);
                }
            }
        }

        // Write output
        var prefixes = firstPartyPrefixes ?? [];
        if (format is "md" or "both")
        {
            var files = MarkdownWriter.WriteDependencyGraphPerTfm(outputFolder.FullName, allResults, prefixes);
            Console.WriteLine($"\nMarkdown output ({files.Count} files):");
            foreach (var f in files)
                Console.WriteLine($"  {f}");
        }

        if (format is "json" or "both")
        {
            var jsonPath = Path.Combine(outputFolder.FullName, "dependency-graph.json");
            JsonWriter.WriteDependencyGraph(jsonPath, allResults, prefixes);
            Console.WriteLine($"JSON output: {jsonPath}");
        }

        Console.WriteLine("Done.");
    }
}
