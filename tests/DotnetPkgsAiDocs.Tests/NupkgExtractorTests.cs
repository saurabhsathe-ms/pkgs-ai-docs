using DotnetPkgsAiDocs.Analysis;
using Xunit;

namespace DotnetPkgsAiDocs.Tests;

public class NupkgExtractorTests
{
    [Fact]
    public void Extract_WithNonExistentFile_ReturnsNull()
    {
        var result = NupkgExtractor.Extract("nonexistent.nupkg");
        Assert.Null(result);
    }

    [Fact]
    public void Extract_WithInvalidZip_ReturnsNull()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "not a zip file");
            var renamed = Path.ChangeExtension(tempFile, ".nupkg");
            File.Move(tempFile, renamed, overwrite: true);

            var result = NupkgExtractor.Extract(renamed);
            Assert.Null(result);

            File.Delete(renamed);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
