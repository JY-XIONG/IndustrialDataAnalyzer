using System.IO;
using IndustrialDataAnalyzer.Core.Models;
using IndustrialDataAnalyzer.Infrastructure.Paths;
using IndustrialDataAnalyzer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialDataAnalyzer.Tests;

public sealed class JsonDeviceRepositoryTests
{
    [Fact]
    public async Task SavesEditsAndLoadsDevicesWithoutFixedUserPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPaths(root);
            var repository = new JsonDeviceRepository(paths, NullLogger<JsonDeviceRepository>.Instance);
            DeviceDefinition device = new() { Id = Guid.NewGuid(), Name = "秤", Code = "S-01", Kind = DeviceKind.Serial,
                Serial = new SerialOptions { PortName = "COM7", BaudRate = 115200, Terminator = "\\r\\n" } };
            await repository.SaveAsync([device]);
            await repository.SaveAsync([device with { Name = "电子秤" }]);

            var loaded = new JsonDeviceRepository(paths, NullLogger<JsonDeviceRepository>.Instance);
            DeviceDefinition restored = Assert.Single(await loaded.LoadAsync());
            Assert.Equal("电子秤", restored.Name);
            Assert.Equal("COM7", restored.Serial.PortName);
            Assert.True(File.Exists(paths.DeviceConfigurationFile + ".bak"));
            Assert.Empty(Directory.GetFiles(paths.DataDirectory, "*.tmp"));

            File.WriteAllText(paths.DeviceConfigurationFile, "not json");
            DeviceDefinition backup = Assert.Single(await loaded.LoadAsync());
            Assert.Equal("秤", backup.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
