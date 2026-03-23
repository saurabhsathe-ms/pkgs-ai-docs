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
    /// Analyzes a specific TFM inside a .nupkg and returns the public API surface.
    /// </summary>
    public static PublicApiResult? Analyze(string nupkgPath, PackageInfo packageInfo, string tfm)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pkgs-ai-docs-api", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            // Extract assemblies for this TFM from the nupkg
            var assemblies = ExtractAssemblies(nupkgPath, tfm, tempDir);
            if (assemblies.Count == 0)
            {
                return null;
            }

            // Build the resolver paths: extracted assemblies + .NET ref assemblies
            var resolverPaths = new List<string>(assemblies);
            resolverPaths.AddRange(GetReferenceAssemblyPaths(tfm));

            // Also add the running runtime's assemblies as fallback
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (runtimeDir != null)
            {
                resolverPaths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
            }

            var resolver = new PathAssemblyResolver(resolverPaths.Distinct());
            using var mlc = new MetadataLoadContext(resolver);

            var apiLines = new List<string>();

            foreach (var assemblyPath in assemblies)
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
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private static List<string> ExtractAssemblies(string nupkgPath, string tfm, string tempDir)
    {
        var extracted = new List<string>();
        using var archive = ZipFile.OpenRead(nupkgPath);

        var prefix = $"lib/{tfm}/";
        var entries = archive.Entries
            .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var entry in entries)
        {
            var destPath = Path.Combine(tempDir, entry.Name);
            entry.ExtractToFile(destPath, overwrite: true);
            extracted.Add(destPath);
        }

        return extracted;
    }

    private static IEnumerable<string> GetReferenceAssemblyPaths(string tfm)
    {
        // Try to find .NET reference assemblies from the SDK's packs folder
        var dotnetRoot = GetDotnetRoot();
        if (dotnetRoot == null) yield break;

        // For net8.0, net9.0, etc. — look in packs/Microsoft.NETCore.App.Ref
        if (tfm.StartsWith("net") && !tfm.StartsWith("net4") && !tfm.StartsWith("netstandard"))
        {
            var packsDir = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
            if (Directory.Exists(packsDir))
            {
                var versionDirs = Directory.GetDirectories(packsDir)
                    .OrderByDescending(d => d)
                    .ToList();

                foreach (var versionDir in versionDirs)
                {
                    var refDir = Path.Combine(versionDir, "ref", tfm);
                    if (Directory.Exists(refDir))
                    {
                        foreach (var dll in Directory.GetFiles(refDir, "*.dll"))
                        {
                            yield return dll;
                        }
                        yield break;
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
                    var latestVersion = Directory.GetDirectories(refAsmPkg)
                        .OrderByDescending(d => d)
                        .FirstOrDefault();

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

        foreach (var type in types.Where(t => t.IsPublic || t.IsNestedPublic))
        {
            var typeName = FormatTypeName(type);
            var typeKind = GetTypeKind(type);
            lines.Add($"{typeName} ({typeKind})");

            // Constructors
            try
            {
                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var parameters = FormatParameters(ctor.GetParameters());
                    lines.Add($"{typeName}.{type.Name}({parameters}) -> void");
                }
            }
            catch { /* skip on error */ }

            // Properties
            try
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
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
                    var fieldType = FormatTypeName(field.FieldType);
                    var constMod = field.IsLiteral ? "const " : (field.IsStatic ? "static " : "");
                    lines.Add($"{constMod}{typeName}.{field.Name} -> {fieldType}");
                }
            }
            catch { /* skip on error */ }
        }

        return lines;
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
            var args = type.GetGenericArguments().Select(FormatTypeName);
            return $"{name}<{string.Join(", ", args)}>";
        }

        return type.FullName ?? type.Name;
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
}
