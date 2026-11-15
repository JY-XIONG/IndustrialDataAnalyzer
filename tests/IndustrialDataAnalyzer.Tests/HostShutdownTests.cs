using IndustrialDataAnalyzer.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IndustrialDataAnalyzer.Tests;

public sealed class HostShutdownTests
{
    [Fact]
    public async Task HostDisposesAsyncOnlySingletonWithoutSynchronousDisposal()
    {
        IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) => services.AddSingleton<AsyncOnlyService>())
            .Build();
        AsyncOnlyService service = host.Services.GetRequiredService<AsyncOnlyService>();

        await global::IndustrialDataAnalyzer.App.App.DisposeHostAsync(host);

        Assert.Equal(1, service.DisposeCount);
    }

    public sealed class AsyncOnlyService : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
