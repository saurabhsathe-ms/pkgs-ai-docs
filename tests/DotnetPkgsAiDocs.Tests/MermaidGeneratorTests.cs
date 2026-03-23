using DotnetPkgsAiDocs.Output;
using DotnetPkgsAiDocs.Analysis;
using Xunit;

namespace DotnetPkgsAiDocs.Tests;

public class MermaidGeneratorTests
{
    [Fact]
    public void GenerateDependencyGraph_WithNoDeps_ReturnsEmpty()
    {
        var result = new DependencyResult("TestPackage", "1.0.0", "net8.0", []);
        var mermaid = MermaidGenerator.GenerateDependencyGraph(result, []);
        Assert.Empty(mermaid);
    }

    [Fact]
    public void GenerateDependencyGraph_WithDeps_ContainsGraphTD()
    {
        var deps = new List<ResolvedDependency>
        {
            new("Microsoft.Extensions.Logging", "8.0.0", ">= 8.0.0"),
            new("Newtonsoft.Json", "13.0.3", ">= 13.0.3")
        };
        var result = new DependencyResult("TestPackage", "1.0.0", "net8.0", deps);
        var mermaid = MermaidGenerator.GenerateDependencyGraph(result, []);

        Assert.Contains("graph TD", mermaid);
        Assert.Contains("TestPackage", mermaid);
    }

    [Fact]
    public void GenerateDependencyGraph_ClassifiesFirstParty()
    {
        var deps = new List<ResolvedDependency>
        {
            new("Microsoft.Identity.ServiceEssentials.Core", "2.0.1", ">= 2.0.1"),
            new("Microsoft.Extensions.Logging", "8.0.0", ">= 8.0.0")
        };
        var result = new DependencyResult("Microsoft.Identity.ServiceEssentials.AspNetCore", "2.0.1", "net8.0", deps);
        var prefixes = new[] { "Microsoft.Identity.ServiceEssentials" };

        var mermaid = MermaidGenerator.GenerateDependencyGraph(result, prefixes);

        // 1P dep should have solid arrow
        Assert.Contains("-->", mermaid);
        // External dep should have dashed arrow
        Assert.Contains("-.->", mermaid);
    }
}
