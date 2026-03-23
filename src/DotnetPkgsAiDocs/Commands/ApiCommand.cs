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

        var allResults = new List<PublicApiResult>();
        foreach (var nupkg in nupkgFiles)
        {
            if (nupkg.Name.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
                continue;

            var info = NupkgExtractor.Extract(nupkg.FullName);
            if (info == null) continue;

            foreach (var tfm in info.TargetFrameworks)
            {
                Console.WriteLine($"  Extracting API: {info.Id} {info.Version} -> {tfm}...");
                var result = PublicApiAnalyzer.Analyze(nupkg.FullName, info, tfm);
                if (result != null)
                {
                    allResults.Add(result);
                }
            }
        }

        if (format is "md" or "both")
        {
            var mdPath = Path.Combine(outputFolder.FullName, "public-api.md");
            MarkdownWriter.WritePublicApi(mdPath, allResults);
            Console.WriteLine($"\nMarkdown output: {mdPath}");
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
