using System.Windows;
using IndustrialDataAnalyzer.App.ViewModels;
using IndustrialDataAnalyzer.App.Views;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.App.Services;

/// <summary>Adapts WPF modal windows to a small view-model-facing interface.</summary>
public sealed class DeviceDialogService(IPortCatalog ports, IDeviceManager manager) : IDeviceDialogService
{
    public DeviceDefinition? Edit(DeviceDefinition? existing)
    {
        var model = new DeviceEditorViewModel(existing, ports, manager.Devices);
        var dialog = new DeviceEditorWindow { DataContext = model, Owner = Application.Current.MainWindow };
        model.RequestClose += accepted => dialog.DialogResult = accepted;
        return dialog.ShowDialog() == true ? model.Result : null;
    }

    public bool ConfirmDelete(DeviceDefinition device) => MessageBox.Show(
        Application.Current.MainWindow, $"确定删除设备“{device.Name}”吗？连接将自动断开。",
        "删除设备", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
