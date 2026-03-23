using System.CommandLine;
using DotnetPkgsAiDocs.Analysis;
using DotnetPkgsAiDocs.Output;

namespace DotnetPkgsAiDocs.Commands;

/// <summary>
/// Command to extract public API surface from .nupkg files.
/// </summary>
internal static class ApiCommand
{
    public static Command Create()
    {
        var command = new Command("api", "Extract public API surface from .nupkg files.");

        command.AddArgument(CommonOptions.InputFolder);
        command.AddOption(CommonOptions.OutputFolder);
        command.AddOption(CommonOptions.Format);

        command.SetHandler(ExecuteAsync,
            CommonOptions.InputFolder,
            CommonOptions.OutputFolder,
            CommonOptions.Format);

        return command;
    }

    internal static async Task ExecuteAsync(
        DirectoryInfo inputFolder,
        DirectoryInfo outputFolder,
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

        // Collect all nupkg paths (excluding snupkg) and their metadata
        var nupkgPaths = new List<string>();
        var packages = new List<(FileInfo file, PackageInfo info)>();
        foreach (var nupkg in nupkgFiles)
        {
            if (nupkg.Name.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
                continue;

            nupkgPaths.Add(nupkg.FullName);
            var info = NupkgExtractor.Extract(nupkg.FullName);
            if (info != null)
                packages.Add((nupkg, info));
        }

        // Discover all TFMs across all packages
        var allTfms = packages
            .SelectMany(p => p.info.TargetFrameworks)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        Console.WriteLine($"Target frameworks found: {string.Join(", ", allTfms)}");

        var allResults = new List<PublicApiResult>();

        // Process per TFM: extract all assemblies once, then analyze each package
        foreach (var tfm in allTfms)
        {
            Console.WriteLine($"\n  Extracting all assemblies for {tfm}...");
            var sharedRefDir = PublicApiAnalyzer.ExtractAllAssembliesForTfm(nupkgPaths, tfm);

            try
            {
                var dllCount = Directory.GetFiles(sharedRefDir, "*.dll").Length;
                Console.WriteLine($"  {dllCount} assemblies available for resolution");

                foreach (var (nupkg, info) in packages)
                {
                    if (!info.TargetFrameworks.Contains(tfm, StringComparer.OrdinalIgnoreCase))
                        continue;

                    Console.WriteLine($"    Extracting API: {info.Id} {info.Version} -> {tfm}...");
                    var result = PublicApiAnalyzer.Analyze(nupkg.FullName, info, tfm, sharedRefDir);
                    if (result != null)
                    {
                        allResults.Add(result);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(sharedRefDir, recursive: true); }
                catch { /* best effort cleanup */ }
            }
        }

        if (format is "md" or "both")
        {
            var files = MarkdownWriter.WritePublicApiPerTfm(outputFolder.FullName, allResults);
            Console.WriteLine($"\nMarkdown output ({files.Count} files):");
            foreach (var f in files)
                Console.WriteLine($"  {f}");
        }

        if (format is "json" or "both")
        {
            var jsonPath = Path.Combine(outputFolder.FullName, "public-api.json");
            JsonWriter.WritePublicApi(jsonPath, allResults);
            Console.WriteLine($"JSON output: {jsonPath}");
        }

        Console.WriteLine("Done.");
        await Task.CompletedTask;
    }
}
