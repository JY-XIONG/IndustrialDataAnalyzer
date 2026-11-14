using System.Windows;

namespace IndustrialDataAnalyzer.App.ViewModels;

internal static class UiThread
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }
}
