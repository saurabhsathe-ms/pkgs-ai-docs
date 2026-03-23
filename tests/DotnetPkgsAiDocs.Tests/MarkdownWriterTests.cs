using DotnetPkgsAiDocs.Output;
using DotnetPkgsAiDocs.Analysis;
using Xunit;

namespace DotnetPkgsAiDocs.Tests;

public class MarkdownWriterTests
{
    [Fact]
    public void WriteDependencyGraph_ProducesValidMarkdown()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var deps = new List<ResolvedDependency>
            {
                new("Microsoft.Extensions.Logging", "8.0.0", ">= 8.0.0")
            };
            var results = new List<DependencyResult>
            {
                new("TestPackage", "1.0.0", "net8.0", deps)
            };

            MarkdownWriter.WriteDependencyGraph(tempFile, results, []);
            var content = File.ReadAllText(tempFile);

            Assert.Contains("# Transitive Dependency Graph", content);
            Assert.Contains("## TestPackage", content);
            Assert.Contains("### net8.0", content);
            Assert.Contains("Microsoft.Extensions.Logging >= 8.0.0", content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void WritePublicApi_ProducesValidMarkdown()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var results = new List<PublicApiResult>
            {
                new("TestPackage", "1.0.0", "net8.0", new[] { "TestNamespace.TestClass (class)", "TestNamespace.TestClass.DoSomething() -> void" })
            };

            MarkdownWriter.WritePublicApi(tempFile, results);
            var content = File.ReadAllText(tempFile);

            Assert.Contains("# Public API Surface", content);
            Assert.Contains("## TestPackage 1.0.0", content);
            Assert.Contains("TestNamespace.TestClass (class)", content);
            Assert.Contains("TestNamespace.TestClass.DoSomething() -> void", content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
