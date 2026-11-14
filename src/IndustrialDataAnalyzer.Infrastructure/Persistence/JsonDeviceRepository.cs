using System.Text.Json;
using System.Text.Json.Serialization;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;

namespace IndustrialDataAnalyzer.Infrastructure.Persistence;

/// <summary>Stores the device catalog in a safely replaced JSON file.</summary>
public sealed class JsonDeviceRepository(IApplicationPaths paths, ILogger<JsonDeviceRepository> logger) : IDeviceRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceDefinition>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string file = paths.DeviceConfigurationFile;
            string backup = file + ".bak";
            if (!File.Exists(file))
            {
                return File.Exists(backup)
                    ? await ReadFileAsync(backup, cancellationToken).ConfigureAwait(false)
                    : [];
            }

            try
            {
                return await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception) when (File.Exists(backup))
            {
                logger.LogWarning(exception, "Device configuration is invalid; loading the backup");
                return await ReadFileAsync(backup, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyList<DeviceDefinition> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string file = paths.DeviceConfigurationFile;
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, devices, options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(file))
            {
                File.Replace(temporary, file, file + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, file);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<DeviceDefinition>> ReadFileAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<List<DeviceDefinition>>(stream, options, cancellationToken)
            .ConfigureAwait(false) ?? throw new JsonException("Device configuration cannot be null.");
    }
}
