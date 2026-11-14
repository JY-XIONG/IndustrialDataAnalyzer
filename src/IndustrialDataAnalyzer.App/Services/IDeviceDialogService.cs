using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.App.Services;

/// <summary>Shows device editing and confirmation dialogs for view models.</summary>
public interface IDeviceDialogService
{
    DeviceDefinition? Edit(DeviceDefinition? existing);
    bool ConfirmDelete(DeviceDefinition device);
}
