using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IndustrialDataAnalyzer.App.ViewModels;
using IndustrialDataAnalyzer.App.ViewModels.Pages;
using IndustrialDataAnalyzer.App.Views;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using IndustrialDataAnalyzer.Infrastructure.Paths;
using IndustrialDataAnalyzer.Infrastructure.Communication;
using IndustrialDataAnalyzer.Infrastructure.Persistence;
using IndustrialDataAnalyzer.Services.Application;
using IndustrialDataAnalyzer.Services.Devices;
using IndustrialDataAnalyzer.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IndustrialDataAnalyzer.App;

/// <summary>Configures application services, logging, and exception reporting.</summary>
public partial class App : System.Windows.Application
{
    private IHost? host;
    private bool isShuttingDown;

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
#if DEBUG
            if (e.Args.Length >= 2 && e.Args[0].StartsWith("--capture-", StringComparison.Ordinal))
            {
                string captureFolder = e.Args[0] == "--capture-editor"
                    ? Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!
                    : Path.GetFullPath(e.Args[1]);
                localDataRoot = Path.Combine(captureFolder, ".local-data");
            }
#endif
            var paths = new ApplicationPaths(localDataRoot);
            paths.Initialize();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine(paths.LogsDirectory, "app-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
                    retainedFileTimeLimit: TimeSpan.FromDays(30),
                    fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true)
                .CreateLogger();

            DispatcherUnhandledException += ReportUiException;
            AppDomain.CurrentDomain.UnhandledException += ReportDomainException;
            TaskScheduler.UnobservedTaskException += ReportTaskException;

            host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices((_, services) =>
                {
                    services.AddSingleton<IApplicationPaths>(paths);
                    services.AddSingleton<IDeviceRepository, JsonDeviceRepository>();
                    services.AddSingleton<IMeasurementRepository, SqliteMeasurementRepository>();
                    services.AddSingleton<ICapturePersistence, CapturePersistenceService>();
                    services.AddSingleton<IFrameParser, FrameParser>();
                    services.AddSingleton<ICommunicationChannelFactory, CommunicationChannelFactory>();
                    services.AddSingleton<IPortCatalog, SerialPortCatalog>();
                    services.AddSingleton<IDeviceManager, DeviceManager>();
                    services.AddSingleton<IDeviceDialogService, DeviceDialogService>();
                    services.AddSingleton<IApplicationInfo>(_ => new ApplicationInfoService(
                        paths,
                        $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version.Major}",
                        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0)));
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddSingleton<MainWindow>();
                    services.AddSingleton<PageViewModel, OverviewViewModel>();
                    services.AddSingleton<PageViewModel, DevicesViewModel>();
                    services.AddSingleton<PageViewModel, AcquisitionViewModel>();
                    services.AddSingleton<PageViewModel, MonitoringViewModel>();
                    services.AddSingleton<PageViewModel, HistoryViewModel>();
                    services.AddSingleton<PageViewModel, AlarmsViewModel>();
                    services.AddSingleton<PageViewModel, ReportsViewModel>();
                    services.AddSingleton<PageViewModel, SettingsViewModel>();
                }).Build();

            await host.StartAsync();
            await host.Services.GetRequiredService<ICapturePersistence>().InitializeAsync();
            await host.Services.GetRequiredService<IDeviceManager>().InitializeAsync();
            MainWindow = host.Services.GetRequiredService<MainWindow>();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            MainWindow.Closed += async (_, _) => await ShutdownApplicationAsync();
            Log.Information("Application started");
            MainWindow.Show();
#if DEBUG
            if ((e.Args.Length == 2 || e.Args.Length == 3) && e.Args[0] == "--capture-all")
            {
                if (e.Args.Length == 3 && e.Args[2] == "--min-size")
                {
                    MainWindow.Width = MainWindow.MinWidth;
                    MainWindow.Height = MainWindow.MinHeight;
                }
                Directory.CreateDirectory(e.Args[1]);
                var viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
                var items = viewModel.WorkspaceItems.Concat(viewModel.DeviceItems).Concat(viewModel.DataItems).Concat(viewModel.SystemItems);
                foreach (var item in items)
                {
                    viewModel.NavigateCommand.Execute(item);
                    string path = Path.Combine(e.Args[1], $"{(int)item.Page + 1:00}-{item.Page}.png");
                    await Dispatcher.InvokeAsync(() => SaveWindowSnapshot(MainWindow, path), DispatcherPriority.ApplicationIdle);
                }
                await ShutdownApplicationAsync();
            }
            else if ((e.Args.Length == 2 || e.Args.Length == 3) && e.Args[0] == "--capture-editor")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(e.Args[1])!);
                var editor = new DeviceEditorViewModel(null, host.Services.GetRequiredService<IPortCatalog>());
                if (e.Args.Length == 3 && e.Args[2] == "--serial") editor.Kind = DeviceKind.Serial;
                var dialog = new DeviceEditorWindow
                {
                    Owner = MainWindow,
                    DataContext = editor
                };
                dialog.Show();
                await Dispatcher.InvokeAsync(() => SaveWindowSnapshot(dialog, e.Args[1]), DispatcherPriority.ApplicationIdle);
                dialog.Close();
                await ShutdownApplicationAsync();
            }
            else if ((e.Args.Length == 2 || e.Args.Length == 3) &&
                e.Args[0] is "--capture-demo" or "--capture-storage-demo")
            {
                bool withStorage = e.Args[0] == "--capture-storage-demo";
                if (e.Args.Length == 3 && e.Args[2] == "--min-size")
                {
                    MainWindow.Width = MainWindow.MinWidth;
                    MainWindow.Height = MainWindow.MinHeight;
                }
                Directory.CreateDirectory(e.Args[1]);
                var demoRepository = new CaptureDeviceRepository();
                var demoStore = withStorage ? new SqliteMeasurementRepository(paths) : null;
                var demoPersistence = withStorage ? new CapturePersistenceService(demoStore!, new FrameParser(), paths,
                    NullLogger<CapturePersistenceService>.Instance) : null;
                if (demoPersistence is not null) await demoPersistence.InitializeAsync();
                await using var demo = new DeviceManager(demoRepository,
                    new CommunicationChannelFactory(NullLoggerFactory.Instance), new FrameParser(),
                    NullLogger<DeviceManager>.Instance, demoPersistence);
                await demo.InitializeAsync();
                var device = new DeviceDefinition
                {
                    Id = Guid.NewGuid(), Name = "模拟温度计", Code = "SIM-T01", Kind = DeviceKind.Simulator,
                    Simulator = new SimulatorOptions { Measurement = MeasurementKind.Temperature, Minimum = 21, Maximum = 26, IntervalMilliseconds = 100 },
                    Parser = new ParserOptions { FieldName = "温度", Unit = "℃" }
                };
                await demo.UpsertAsync(device);
                PageViewModel[] pages = [.. host.Services.GetServices<PageViewModel>().Select(page => page.Page switch
                {
                    NavigationPage.Overview => new OverviewViewModel(demo, demoStore),
                    NavigationPage.Devices => new DevicesViewModel(demo),
                    NavigationPage.Acquisition => new AcquisitionViewModel(demo, demoPersistence),
                    NavigationPage.History when withStorage => new HistoryViewModel(demoStore),
                    _ => page
                })];
                var preview = new MainWindowViewModel(pages, host.Services.GetRequiredService<IApplicationInfo>(), demo);
                MainWindow.DataContext = preview;
                preview.NavigateCommand.Execute(preview.DeviceItems[0]);
                await Dispatcher.InvokeAsync(() => SaveWindowSnapshot(MainWindow, Path.Combine(e.Args[1], "devices-with-data.png")), DispatcherPriority.ApplicationIdle);
                await demo.ConnectAsync(device.Id);
                await Task.Delay(260);
                preview.NavigateCommand.Execute(preview.DeviceItems[1]);
                await Dispatcher.InvokeAsync(() => SaveWindowSnapshot(MainWindow, Path.Combine(e.Args[1], "acquisition-with-data.png")), DispatcherPriority.ApplicationIdle);
                preview.NavigateCommand.Execute(preview.WorkspaceItems[0]);
                if (withStorage) await ((OverviewViewModel)pages.Single(page => page.Page == NavigationPage.Overview)).RefreshTodayAsync();
                await Dispatcher.InvokeAsync(() => SaveWindowSnapshot(MainWindow, Path.Combine(e.Args[1], "overview-with-data.png")), DispatcherPriority.ApplicationIdle);
                if (withStorage)
                {
                    await demo.DisconnectAsync(device.Id);
                    preview.NavigateCommand.Execute(preview.DataItems[0]);
                    var history = (HistoryViewModel)pages.Single(page => page.Page == NavigationPage.History);
                    await history.SearchCommand.ExecuteAsync(null);
                    await Dispatcher.InvokeAsync(() => SaveWindowSnapshot(MainWindow,
                        Path.Combine(e.Args[1], "history-with-data.png")), DispatcherPriority.ApplicationIdle);
                }
                await ShutdownApplicationAsync();
            }
#endif
        }
        catch (Exception exception)
        {
            Trace.TraceError("Application startup failed: {0}", exception);
            try
            {
                Log.Fatal(exception, "Application startup failed");
            }
            catch (Exception loggingException)
            {
                Trace.TraceError("Startup failure could not be written to Serilog: {0}", loggingException);
            }

            string reason = exception.GetBaseException().Message;
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = exception.GetType().Name;
            }
            if (reason.Length > 400)
            {
                reason = reason[..400] + "…";
            }

            MessageBox.Show(
                $"程序启动失败。\n原因：{reason}\n\n如果没有生成日志，请检查 %LocalAppData%\\IndustrialDataAnalyzer 的写入权限。",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            if (host is not null)
            {
                try { await DisposeHostAsync(host); }
                catch (Exception disposeError) { Trace.TraceError("Host cleanup after startup failure: {0}", disposeError); }
                host = null;
            }
            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    internal static ValueTask DisposeHostAsync(IHost ownedHost) => ownedHost is IAsyncDisposable asyncHost
        ? asyncHost.DisposeAsync()
        : throw new InvalidOperationException("Host 不支持异步释放，无法安全释放异步服务。");

    private async Task ShutdownApplicationAsync(int exitCode = 0)
    {
        if (isShuttingDown)
        {
            return;
        }

        isShuttingDown = true;
        Log.Information("Application closing");
        Exception? shutdownError = null;
        IHost? closingHost = host;
        host = null;
        try
        {
            if (closingHost is not null)
            {
                try { await closingHost.Services.GetRequiredService<IDeviceManager>().DisposeAsync(); }
                catch (Exception exception)
                {
                    shutdownError = exception;
                    Log.Error(exception, "Device and capture shutdown failed");
                }
                try { await closingHost.StopAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception exception)
                {
                    shutdownError ??= exception;
                    Log.Error(exception, "Host stop failed");
                }
                try { await DisposeHostAsync(closingHost); }
                catch (Exception exception)
                {
                    shutdownError ??= exception;
                    Log.Error(exception, "Async Host disposal failed");
                }
            }
        }
        catch (Exception exception)
        {
            shutdownError ??= exception;
            Log.Error(exception, "Application shutdown failed unexpectedly");
        }
        finally
        {
            if (shutdownError is not null)
            {
                MessageBox.Show("关闭时存储或资源释放失败：" + shutdownError.GetBaseException().Message +
                    "\n待写数据不能视为已保存，请检查日志和恢复文件。",
                    "关闭异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(exitCode);
        }
    }

    private void ReportUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show("程序发生错误，即将退出。请查看日志。", "程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        _ = ShutdownApplicationAsync(1);
    }

    private static void ReportDomainException(object sender, UnhandledExceptionEventArgs e) =>
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled process exception");

    private static void ReportTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

#if DEBUG
    private sealed class CaptureDeviceRepository : IDeviceRepository
    {
        private IReadOnlyList<DeviceDefinition> devices = [];
        public Task<IReadOnlyList<DeviceDefinition>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(devices);
        public Task SaveAsync(IReadOnlyList<DeviceDefinition> updated, CancellationToken cancellationToken = default)
        {
            devices = [.. updated];
            return Task.CompletedTask;
        }
    }

    private static void SaveWindowSnapshot(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
#endif
}
