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

## Key technical decisions
- Transitive dependency resolution via `dotnet restore` + `project.assets.json` parsing
- Public API extraction via `System.Reflection.MetadataLoadContext` (metadata-only, no JIT)
- `Microsoft.NETFramework.ReferenceAssemblies` for net462/net472 ref assemblies
- Input folder is always added as a local NuGet source (so cross-package resolution works)
- Output format: Markdown with Mermaid diagrams (primary), JSON sidecar (secondary)

## Conventions
- Target: net8.0
- Nullable reference types enabled
- TreatWarningsAsErrors enabled
- Use records for data models
- Methods that call external processes (dotnet restore) are async
