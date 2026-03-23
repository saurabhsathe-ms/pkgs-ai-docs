using System.CommandLine;

namespace DotnetPkgsAiDocs.Commands;

/// <summary>
/// Shared CLI options and arguments used by all commands.
/// </summary>
internal static class CommonOptions
{
    public static Argument<DirectoryInfo> InputFolder { get; } = new(
        name: "input-folder",
        description: "Folder containing .nupkg files to analyze.")
    {
        Arity = ArgumentArity.ExactlyOne
    };

    public static Option<DirectoryInfo> OutputFolder { get; } = new(
        aliases: ["-o", "--output"],
        description: "Output directory for generated documentation.",
        getDefaultValue: () => new DirectoryInfo("./pkgs-ai-docs-output"));

    public static Option<string[]> NuGetSources { get; } = new(
        aliases: ["--source"],
        description: "Additional NuGet source(s) for transitive dependency resolution. The input folder is always added as a local source.")
    {
        AllowMultipleArgumentsPerToken = true
    };

    public static Option<string[]> FirstPartyPrefixes { get; } = new(
        aliases: ["--1p-prefix"],
        description: "Package ID prefix(es) to classify as first-party in dependency output.")
    {
        AllowMultipleArgumentsPerToken = true
    };

    public static Option<string> Format { get; } = new(
        aliases: ["--format"],
        description: "Output format: md, json, or both.",
        getDefaultValue: () => "md");
}
