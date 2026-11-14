using CommunityToolkit.Mvvm.ComponentModel;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.App.ViewModels;

/// <summary>One device entry with live state.</summary>
public partial class DeviceRowViewModel(DeviceDefinition device, ConnectionState initialState) : ObservableObject
{
    public DeviceDefinition Device { get; } = device;
    public Guid Id => Device.Id;
    public string Name => Device.Name;
    public string Code => Device.Code;
    public string TypeText => Device.Kind == DeviceKind.Serial ? "真实串口" : "模拟设备";
    public string Summary => Device.Kind == DeviceKind.Serial
        ? $"{Device.Serial.PortName} · {Device.Serial.BaudRate} bps · {Device.Serial.EncodingName}"
        : $"{Device.Simulator.Measurement switch
        {
            MeasurementKind.Temperature => "温度", MeasurementKind.Weight => "重量", _ => "电压"
        }} · {Device.Simulator.IntervalMilliseconds} ms · {Device.Simulator.Minimum}～{Device.Simulator.Maximum}";
    public string Notes => Device.Notes;
    [ObservableProperty] private ConnectionState state = initialState;
    public string StatusText => State switch
    {
        ConnectionState.Connected => "已连接", ConnectionState.Connecting => "连接中",
        ConnectionState.Faulted => "连接异常", _ => "未连接"
    };
    partial void OnStateChanged(ConnectionState value) => OnPropertyChanged(nameof(StatusText));
}
