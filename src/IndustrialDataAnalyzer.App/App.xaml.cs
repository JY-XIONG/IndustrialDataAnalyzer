using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IndustrialDataAnalyzer.App.ViewModels;
using IndustrialDataAnalyzer.App.ViewModels.Pages;
using IndustrialDataAnalyzer.App.Views;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Infrastructure.Paths;
using IndustrialDataAnalyzer.Services.Application;
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
            var paths = new ApplicationPaths(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            paths.Initialize();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine(paths.LogsDirectory, "app-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
                    retainedFileTimeLimit: TimeSpan.FromDays(30), shared: true)
                .CreateLogger();

            DispatcherUnhandledException += ReportUiException;
            AppDomain.CurrentDomain.UnhandledException += ReportDomainException;
            TaskScheduler.UnobservedTaskException += ReportTaskException;

            host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices((_, services) =>
                {
                    services.AddSingleton<IApplicationPaths>(paths);
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
                    await Dispatcher.InvokeAsync(() => SaveWindowSnapshot(path), DispatcherPriority.ApplicationIdle);
                }
                await ShutdownApplicationAsync();
            }
#endif
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application startup failed");
            MessageBox.Show("程序启动失败。请查看日志目录中的错误记录。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private async Task ShutdownApplicationAsync()
    {
        if (isShuttingDown)
        {
            return;
        }

        isShuttingDown = true;
        Log.Information("Application closing");
        try
        {
            if (host is not null)
            {
                await host.StopAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Application shutdown failed");
        }
        finally
        {
            Shutdown();
        }
    }

    private void ReportUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show("程序发生错误，即将退出。请查看日志。", "程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private static void ReportDomainException(object sender, UnhandledExceptionEventArgs e) =>
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled process exception");

    private static void ReportTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

#if DEBUG
    private void SaveWindowSnapshot(string path)
    {
        MainWindow.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)MainWindow.ActualWidth, (int)MainWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(MainWindow);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
#endif
}
