using System.Windows;
using IndustrialDataAnalyzer.App.ViewModels;

namespace IndustrialDataAnalyzer.App.Views;

/// <summary>Hosts the eight navigable pages.</summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window with its injected view model.</summary>
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
