namespace DotnetPkgsAiDocs.Analysis;

/// <summary>
/// Metadata extracted from a .nupkg file.
/// </summary>
public record PackageInfo(
    string Id,
    string Version,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyDictionary<string, IReadOnlyList<PackageDependency>> DependenciesByTfm,
    string NupkgPath);

/// <summary>
/// A direct dependency declared in a package's nuspec.
/// </summary>
public record PackageDependency(string Id, string VersionRange);

/// <summary>
/// Result of transitive dependency resolution for a single package × TFM.
/// </summary>
public record DependencyResult(
    string PackageId,
    string PackageVersion,
    string TargetFramework,
    IReadOnlyList<ResolvedDependency> Dependencies);

/// <summary>
/// A resolved transitive dependency with its version.
/// </summary>
public record ResolvedDependency(string Id, string ResolvedVersion, string RequestedRange);

/// <summary>
/// Result of public API extraction for a single package × TFM.
/// </summary>
public record PublicApiResult(
    string PackageId,
    string PackageVersion,
    string TargetFramework,
    IReadOnlyList<string> ApiLines);
