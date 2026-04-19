using System.IO.Compression;
using System.Reflection;

namespace DotnetPkgsAiDocs.Analysis;

/// <summary>
/// Extracts the public API surface from assemblies inside a .nupkg file
/// using System.Reflection.MetadataLoadContext (metadata-only, no code execution).
/// </summary>
public static class PublicApiAnalyzer
{
    /// <summary>
    /// Extracts all DLLs from all nupkg files for a given TFM into a shared directory.
    /// Returns the path to the shared directory. Caller is responsible for cleanup.
    /// </summary>
    public static string ExtractAllAssembliesForTfm(IReadOnlyList<string> nupkgPaths, string tfm)
    {
        var sharedDir = Path.Combine(Path.GetTempPath(), "pkgs-ai-docs-api", $"shared-{tfm}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sharedDir);

        // Build list of compatible TFMs to try (exact match first, then fallbacks)
        var compatibleTfms = GetCompatibleTfms(tfm);

        foreach (var nupkgPath in nupkgPaths)
        {
            try
            {
                using var archive = ZipFile.OpenRead(nupkgPath);

                // Try each compatible TFM in priority order, stop at first match
                foreach (var candidateTfm in compatibleTfms)
                {
                    var prefix = $"lib/{candidateTfm}/";
                    var entries = archive.Entries
                        .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                 && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (entries.Count > 0)
                    {
                        foreach (var entry in entries)
                        {
                            var destPath = Path.Combine(sharedDir, entry.Name);
                            if (!File.Exists(destPath))
                            {
                                entry.ExtractToFile(destPath);
                            }
                        }
                        break; // Found assemblies for this nupkg, don't try lower TFMs
                    }
                }
            }
            catch
            {
                // Skip nupkgs that can't be read
            }
        }

        return sharedDir;
    }

    /// <summary>
    /// Returns a list of TFMs compatible with the requested one, in priority order.
    /// e.g., net10.0 → [net10.0, net9.0, net8.0, netstandard2.1, netstandard2.0]
    /// </summary>
    private static List<string> GetCompatibleTfms(string tfm)
    {
        var result = new List<string> { tfm };

        // For modern .NET (net5.0+), add lower versions as fallbacks
        if (tfm.StartsWith("net") && !tfm.StartsWith("net4") && !tfm.StartsWith("netstandard")
            && Version.TryParse(tfm[3..], out var version))
        {
            // Add lower major versions down to net6.0
            for (var v = version.Major - 1; v >= 6; v--)
            {
                result.Add($"net{v}.0");
            }
            // Add netstandard fallbacks
            result.Add("netstandard2.1");
            result.Add("netstandard2.0");
        }
        else if (tfm.StartsWith("net4"))
        {
            // .NET Framework — add netstandard fallbacks
            result.Add("netstandard2.0");
            result.Add("netstandard1.6");
        }

        return result;
    }

    /// <summary>
    /// Analyzes a specific TFM inside a .nupkg and returns the public API surface.
    /// Uses the sharedRefDir for cross-package assembly resolution.
    /// </summary>
    public static PublicApiResult? Analyze(string nupkgPath, PackageInfo packageInfo, string tfm, string sharedRefDir)
    {
        try
        {
            // Find which assemblies belong to THIS package (already extracted in sharedRefDir)
            var packageAssemblyNames = GetPackageAssemblyNames(nupkgPath, tfm);
            if (packageAssemblyNames.Count == 0)
            {
                return null;
            }

            var packageAssemblyPaths = packageAssemblyNames
                .Select(name => Path.Combine(sharedRefDir, name))
                .Where(File.Exists)
                .ToList();

            if (packageAssemblyPaths.Count == 0)
            {
                return null;
            }

            // Build resolver: runtime assemblies < ref assemblies < shared extracted assemblies
            var pathsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Runtime assemblies (lowest priority)
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (runtimeDir != null)
            {
                foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
                {
                    pathsByName[Path.GetFileName(dll)] = dll;
                }
            }

            // NuGet global packages cache (for external deps not in the input folder)
            AddNuGetCacheAssemblies(pathsByName, tfm);

            // Ref assemblies (override runtime)
            foreach (var refPath in GetReferenceAssemblyPaths(tfm))
            {
                pathsByName[Path.GetFileName(refPath)] = refPath;
            }

            // All sibling assemblies from all nupkgs (highest priority)
            foreach (var dll in Directory.GetFiles(sharedRefDir, "*.dll"))
            {
                pathsByName[Path.GetFileName(dll)] = dll;
            }

            var resolver = new NameMatchingAssemblyResolver(pathsByName);
            using var mlc = new MetadataLoadContext(resolver);

            var apiLines = new List<string>();

            foreach (var assemblyPath in packageAssemblyPaths)
            {
                try
                {
                    var assembly = mlc.LoadFromAssemblyPath(assemblyPath);
                    var lines = ExtractPublicApi(assembly);
                    apiLines.AddRange(lines);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"    Warning: Could not load {Path.GetFileName(assemblyPath)}: {ex.Message}");
                }
            }

            apiLines.Sort(StringComparer.Ordinal);
            return new PublicApiResult(packageInfo.Id, packageInfo.Version, tfm, apiLines);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    Error analyzing API for {packageInfo.Id} -> {tfm}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets the DLL filenames inside a nupkg for a given TFM without extracting them.
    /// Falls back through compatible TFMs (matching ExtractAllAssembliesForTfm behavior).
    /// </summary>
    private static List<string> GetPackageAssemblyNames(string nupkgPath, string tfm)
    {
        try
        {
            using var archive = ZipFile.OpenRead(nupkgPath);

            foreach (var candidateTfm in GetCompatibleTfms(tfm))
            {
                var prefix = $"lib/{candidateTfm}/";
                var names = archive.Entries
                    .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                             && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Name)
                    .ToList();

                if (names.Count > 0)
                    return names;
            }

            return [];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> GetReferenceAssemblyPaths(string tfm)
    {
        // Try to find .NET reference assemblies from the SDK's packs folder
        var dotnetRoot = GetDotnetRoot();
        if (dotnetRoot == null) yield break;

        // For net8.0, net9.0, etc. — look in packs/Microsoft.NETCore.App.Ref
        if (tfm.StartsWith("net") && !tfm.StartsWith("net4") && !tfm.StartsWith("netstandard"))
        {
            // Microsoft.NETCore.App.Ref — core runtime assemblies
            var packsDir = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
            if (Directory.Exists(packsDir))
            {
                var versionDirs = SortDirectoriesByVersionDescending(Directory.GetDirectories(packsDir));

                foreach (var versionDir in versionDirs)
                {
                    var refDir = Path.Combine(versionDir, "ref", tfm);
                    if (Directory.Exists(refDir))
                    {
                        foreach (var dll in Directory.GetFiles(refDir, "*.dll"))
                        {
                            yield return dll;
                        }
                        break;
                    }
                }
            }

            // Microsoft.AspNetCore.App.Ref — ASP.NET Core assemblies (Microsoft.AspNetCore.Authentication, etc.)
            var aspNetPacksDir = Path.Combine(dotnetRoot, "packs", "Microsoft.AspNetCore.App.Ref");
            if (Directory.Exists(aspNetPacksDir))
            {
                var versionDirs = SortDirectoriesByVersionDescending(Directory.GetDirectories(aspNetPacksDir));

                foreach (var versionDir in versionDirs)
                {
                    var refDir = Path.Combine(versionDir, "ref", tfm);
                    if (Directory.Exists(refDir))
                    {
                        foreach (var dll in Directory.GetFiles(refDir, "*.dll"))
                        {
                            yield return dll;
                        }
                        break;
                    }
                }
            }
        }

        // For net462, net472, etc. — try Microsoft.NETFramework.ReferenceAssemblies NuGet package
        // These get installed to the NuGet global packages cache
        if (tfm.StartsWith("net4"))
        {
            var nugetCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");

            var frameworkVersion = TfmToFrameworkVersion(tfm);
            if (frameworkVersion != null)
            {
                var refAsmPkg = Path.Combine(nugetCache,
                    $"microsoft.netframework.referenceassemblies.{tfm}");

                if (Directory.Exists(refAsmPkg))
                {
                    var latestVersion = SortDirectoriesByVersionDescending(
                        Directory.GetDirectories(refAsmPkg)).FirstOrDefault();

                    if (latestVersion != null)
                    {
                        var buildDir = Path.Combine(latestVersion, "build", frameworkVersion);
                        if (Directory.Exists(buildDir))
                        {
                            foreach (var dll in Directory.GetFiles(buildDir, "*.dll", SearchOption.AllDirectories))
                            {
                                yield return dll;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sorts directories by parsed semantic version (descending), falling back to lexicographic order.
    /// </summary>
    private static IReadOnlyList<string> SortDirectoriesByVersionDescending(string[] dirs)
    {
        return dirs
            .Select(d => (dir: d, version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .OrderByDescending(x => x.version)
            .ThenByDescending(x => x.dir)
            .Select(x => x.dir)
            .ToList();
    }

    private static string? GetDotnetRoot()
    {
        // DOTNET_ROOT env var
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) return root;

        // Default locations
        if (OperatingSystem.IsWindows())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            if (Directory.Exists(root)) return root;
        }
        else
        {
            foreach (var candidate in new[] { "/usr/share/dotnet", "/usr/local/share/dotnet" })
            {
                if (Directory.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static string? TfmToFrameworkVersion(string tfm)
    {
        return tfm switch
        {
            "net462" => ".NETFramework/v4.6.2",
            "net47" => ".NETFramework/v4.7",
            "net471" => ".NETFramework/v4.7.1",
            "net472" => ".NETFramework/v4.7.2",
            "net48" => ".NETFramework/v4.8",
            "net481" => ".NETFramework/v4.8.1",
            _ => null
        };
    }

    private static List<string> ExtractPublicApi(Assembly assembly)
    {
        var lines = new List<string>();

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        foreach (var type in types.Where(t => t.IsPublic || (t.IsNestedPublic && IsEffectivelyPublic(t))))
        {
            if (IsObsolete(type)) continue;

            var typeName = FormatTypeName(type);
            var typeKind = GetTypeKind(type);
            lines.Add($"{typeName} ({typeKind})");

            // Constructors
            try
            {
                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (IsObsolete(ctor)) continue;
                    var parameters = FormatParameters(ctor.GetParameters());
                    var ctorName = StripGenericArity(type.Name);
                    lines.Add($"{typeName}.{ctorName}({parameters}) -> void");
                }
            }
            catch { /* skip on error */ }

            // Properties
            try
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (IsObsolete(prop)) continue;
                    var propType = FormatTypeName(prop.PropertyType);
                    if (prop.GetGetMethod() != null)
                        lines.Add($"{typeName}.{prop.Name}.get -> {propType}");
                    if (prop.GetSetMethod() != null)
                        lines.Add($"{typeName}.{prop.Name}.set -> void");
                }
            }
            catch { /* skip on error */ }

            // Methods (excluding property accessors and event accessors)
            try
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.IsSpecialName) continue; // Skip property/event accessors
                    if (IsObsolete(method)) continue;
                    var parameters = FormatParameters(method.GetParameters());
                    var returnType = FormatTypeName(method.ReturnType);
                    var staticMod = method.IsStatic ? "static " : "";
                    lines.Add($"{staticMod}{typeName}.{method.Name}({parameters}) -> {returnType}");
                }
            }
            catch { /* skip on error */ }

            // Events
            try
            {
                foreach (var evt in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (IsObsolete(evt)) continue;
                    var handlerType = evt.EventHandlerType != null ? FormatTypeName(evt.EventHandlerType) : "EventHandler";
                    lines.Add($"{typeName}.{evt.Name} -> {handlerType}");
                }
            }
            catch { /* skip on error */ }

            // Fields (public, non-backing)
            try
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (field.Name.Contains("k__BackingField", StringComparison.Ordinal)) continue;
                    if (field.Name == "value__" && type.IsEnum) continue;
                    if (IsObsolete(field)) continue;
                    var fieldType = FormatTypeName(field.FieldType);
                    var constMod = field.IsLiteral ? "const " : (field.IsStatic ? "static " : "");
                    lines.Add($"{constMod}{typeName}.{field.Name} -> {fieldType}");
                }
            }
            catch { /* skip on error */ }
        }

        return lines;
    }

    /// <summary>
    /// Checks whether a nested type is effectively public by verifying all enclosing types are also public.
    /// A public nested type inside an internal class is not accessible outside the assembly.
    /// </summary>
    private static bool IsEffectivelyPublic(Type type)
    {
        var current = type.DeclaringType;
        while (current != null)
        {
            if (!(current.IsPublic || current.IsNestedPublic))
                return false;
            current = current.DeclaringType;
        }
        return true;
    }

    private static string FormatTypeName(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.FullName ?? type.Name;
            var backtick = name.IndexOf('`');
            if (backtick > 0)
            {
                name = name[..backtick];
            }
            // Normalize nested type separator from '+' to '.'
            name = name.Replace('+', '.');
            var args = type.GetGenericArguments().Select(FormatTypeName);
            return $"{name}<{string.Join(", ", args)}>";
        }

        // Normalize nested type separator from '+' to '.'
        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    /// <summary>
    /// Strips the generic arity suffix (e.g., "CacheItem`1" → "CacheItem").
    /// </summary>
    private static string StripGenericArity(string name)
    {
        var backtick = name.IndexOf('`');
        return backtick > 0 ? name[..backtick] : name;
    }

    private static string FormatParameters(ParameterInfo[] parameters)
    {
        return string.Join(", ", parameters.Select(p =>
        {
            var typeName = FormatTypeName(p.ParameterType);
            return $"{typeName} {p.Name}";
        }));
    }

    private static string GetTypeKind(Type type)
    {
        if (type.IsInterface) return "interface";
        if (type.IsEnum) return "enum";
        if (type.IsValueType) return "struct";
        if (type.BaseType?.FullName == "System.MulticastDelegate") return "delegate";
        if (type.IsAbstract && type.IsSealed) return "static class";
        if (type.IsAbstract) return "abstract class";
        if (type.IsSealed) return "sealed class";
        return "class";
    }

    /// <summary>
    /// Checks if a member has the [Obsolete] attribute via CustomAttributeData
    /// (works with MetadataLoadContext where normal attribute instantiation isn't available).
    /// </summary>
    private static bool IsObsolete(MemberInfo member)
    {
        try
        {
            return member.CustomAttributes.Any(a =>
                a.AttributeType.FullName == "System.ObsoleteAttribute");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Scans the NuGet global packages cache for assemblies matching a TFM
    /// and adds them to the paths dictionary (low priority — existing entries not overwritten).
    /// </summary>
    private static void AddNuGetCacheAssemblies(Dictionary<string, string> pathsByName, string tfm)
    {
        var nugetCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");

        if (!Directory.Exists(nugetCache)) return;

        // Build list of compatible TFMs to try
        var compatibleTfms = GetCompatibleTfms(tfm);

        // Search for DLLs matching the TFM in the cache
        // Structure: ~/.nuget/packages/{packageId}/{version}/lib/{tfm}/*.dll
        try
        {
            foreach (var packageDir in Directory.GetDirectories(nugetCache))
            {
                // Get the latest version directory
                var versionDirs = Directory.GetDirectories(packageDir);
                if (versionDirs.Length == 0) continue;

                var latestVersion = SortDirectoriesByVersionDescending(versionDirs)
                    .First();

                // Try each compatible TFM in priority order
                foreach (var candidateTfm in compatibleTfms)
                {
                    var libTfmDir = Path.Combine(latestVersion, "lib", candidateTfm);
                    if (Directory.Exists(libTfmDir))
                    {
                        foreach (var dll in Directory.GetFiles(libTfmDir, "*.dll"))
                        {
                            var fileName = Path.GetFileName(dll);
                            // Don't overwrite higher-priority entries
                            if (!pathsByName.ContainsKey(fileName))
                            {
                                pathsByName[fileName] = dll;
                            }
                        }
                        break; // Found assemblies for this package, don't try lower TFMs
                    }
                }
            }
        }
        catch
        {
            // NuGet cache scan is best-effort
        }
    }
}

/// <summary>
/// Assembly resolver that matches by simple name (ignoring version, culture, public key token).
/// This handles cases where assemblies reference each other with placeholder versions like 42.42.42.42.
/// </summary>
internal sealed class NameMatchingAssemblyResolver : MetadataAssemblyResolver
{
    private readonly Dictionary<string, string> _pathsByFileName;
    private readonly Dictionary<string, string> _pathsBySimpleName;

    public NameMatchingAssemblyResolver(Dictionary<string, string> pathsByFileName)
    {
        _pathsByFileName = pathsByFileName;

        // Build a lookup by assembly simple name (without extension)
        _pathsBySimpleName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in pathsByFileName)
        {
            var simpleName = Path.GetFileNameWithoutExtension(kvp.Key);
            // Keep the highest priority (last-added) entry
            _pathsBySimpleName[simpleName] = kvp.Value;
        }
    }

    public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName)
    {
        // Try exact filename match first
        var fileName = assemblyName.Name + ".dll";
        if (_pathsByFileName.TryGetValue(fileName, out var path))
        {
            return context.LoadFromAssemblyPath(path);
        }

        // Try simple name match (handles version mismatches)
        if (assemblyName.Name != null && _pathsBySimpleName.TryGetValue(assemblyName.Name, out var pathByName))
        {
            return context.LoadFromAssemblyPath(pathByName);
        }

        return null;
    }
}
