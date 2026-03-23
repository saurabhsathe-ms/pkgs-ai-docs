# Copilot Instructions for pkgs-ai-docs

## Project overview
This is `dotnet-pkgs-ai-docs`, a .NET CLI tool that generates AI-ready documentation 
(transitive dependency graphs + public API surfaces) from `.nupkg` files without source code.

## Architecture
- `src/DotnetPkgsAiDocs/` — main console app, packaged as a dotnet tool
  - `Commands/` — CLI commands using `System.CommandLine` (deps, api, all)
  - `Analysis/` — core logic: NupkgExtractor, TransitiveDepsResolver, PublicApiAnalyzer
  - `Output/` — output generators: MarkdownWriter, MermaidGenerator, JsonWriter
- `tests/DotnetPkgsAiDocs.Tests/` — xUnit tests
- `samples/` — example output generated from .NET Aspire packages

## Key technical decisions
- Transitive dependency resolution via `dotnet restore` + `project.assets.json` parsing
- Public API extraction via `System.Reflection.MetadataLoadContext` (metadata-only, no JIT)
- `NameMatchingAssemblyResolver` — custom `MetadataAssemblyResolver` that resolves by simple name, ignoring version mismatches (handles placeholder versions like 42.42.42.42)
- TFM fallback — when extracting sibling assemblies, falls back through compatible TFMs (e.g., net10.0 → net9.0 → net8.0 → netstandard2.1)
- NuGet global cache fallback — scans `~/.nuget/packages/` for external dependencies not in the input folder
- Per-TFM batch extraction — all DLLs from all nupkgs extracted once per TFM into a shared directory for cross-package resolution
- `[Obsolete]` filtering — types and members marked with `[System.ObsoleteAttribute]` are excluded from public API output
- `Microsoft.NETFramework.ReferenceAssemblies` for net462/net472 ref assemblies
- Input folder is always added as a local NuGet source (so cross-package resolution works)
- Output: one file per TFM (`dependencies-{tfm}.md`, `public-api-{tfm}.md`), plus JSON sidecar
- 1P/external classification via `--1p-prefix` flag

## Conventions
- Target: net8.0
- Nullable reference types enabled
- TreatWarningsAsErrors enabled
- Use records for data models
- Methods that call external processes (dotnet restore) are async
