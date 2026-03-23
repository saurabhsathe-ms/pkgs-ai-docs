using System.CommandLine;
using DotnetPkgsAiDocs.Commands;

var rootCommand = new RootCommand(
    "Generate AI-ready documentation (transitive dependency graphs and public API surfaces) from NuGet packages.");

rootCommand.AddCommand(DepsCommand.Create());
rootCommand.AddCommand(ApiCommand.Create());
rootCommand.AddCommand(AllCommand.Create());

return await rootCommand.InvokeAsync(args);
