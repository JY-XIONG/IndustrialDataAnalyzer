using System.IO;
using IndustrialDataAnalyzer.Infrastructure.Paths;
using IndustrialDataAnalyzer.Services.Application;

namespace IndustrialDataAnalyzer.Tests;

public sealed class ApplicationFoundationTests
{
    [Fact]
    public void InitializeCreatesOnlyDataAndLogDirectories()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPaths(sandbox);
            paths.Initialize();

            Assert.True(Directory.Exists(paths.DataDirectory));
            Assert.True(Directory.Exists(paths.LogsDirectory));
            Assert.Equal(new[] { paths.LogsDirectory }, Directory.GetDirectories(paths.DataDirectory));
            Assert.Empty(Directory.GetFiles(paths.DataDirectory));
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplicationInfoUsesProvidedVersionAndPaths()
    {
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), "test-root"));
        var info = new ApplicationInfoService(paths, "Production", new Version(1, 0, 0, 0));

        Assert.Equal("V1.0.0", info.Version);
        Assert.Equal("Production", info.EnvironmentName);
        Assert.Equal(paths.DataDirectory, info.DataDirectory);
        Assert.Equal(paths.LogsDirectory, info.LogsDirectory);
    }
}
