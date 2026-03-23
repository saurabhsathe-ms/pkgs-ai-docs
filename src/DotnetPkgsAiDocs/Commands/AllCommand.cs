using System.CommandLine;

namespace DotnetPkgsAiDocs.Commands;

/// <summary>
/// Command that runs both deps and api extraction.
/// </summary>
internal static class AllCommand
{
    public static Command Create()
    {
        var command = new Command("all", "Generate both dependency graphs and public API docs from .nupkg files.");

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

    private static async Task ExecuteAsync(
        DirectoryInfo inputFolder,
        DirectoryInfo outputFolder,
        string[]? nugetSources,
        string[]? firstPartyPrefixes,
        string format)
    {
        Console.WriteLine("=== Transitive Dependency Graphs ===\n");
        await DepsCommand.ExecuteAsync(inputFolder, outputFolder, nugetSources, firstPartyPrefixes, format);

        Console.WriteLine("\n=== Public API Surface ===\n");
        await ApiCommand.ExecuteAsync(inputFolder, outputFolder, format);
    }
}
