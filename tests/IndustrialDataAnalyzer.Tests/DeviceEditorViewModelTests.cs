using IndustrialDataAnalyzer.App.ViewModels;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Tests;

public sealed class DeviceEditorViewModelTests
{
    [Fact]
    public void NewSimulatorGetsMeaningfulFieldAndCanBeSaved()
    {
        var model = new DeviceEditorViewModel(null, new TestPorts());
        Assert.Equal("温度", model.FieldName);
        Assert.Equal("℃", model.Unit);
        model.Measurement = MeasurementKind.Voltage;
        Assert.Equal("电压", model.FieldName);
        Assert.Equal("V", model.Unit);
        model.Name = "电压模拟";
        model.Code = "V01";
        bool? accepted = null;
        model.RequestClose += value => accepted = value;

        model.SaveCommand.Execute(null);

        Assert.True(accepted);
        Assert.Equal(DeviceKind.Simulator, model.Result?.Kind);
        Assert.Equal(MeasurementKind.Voltage, model.Result?.Simulator.Measurement);
        Assert.Contains("COM7", model.AvailablePorts);
    }

    [Fact]
    public void DuplicateCodeKeepsEditorOpen()
    {
        var existing = new DeviceDefinition { Id = Guid.NewGuid(), Name = "Old", Code = "A01", Kind = DeviceKind.Simulator };
        var model = new DeviceEditorViewModel(null, new TestPorts(), [existing]) { Name = "New", Code = "a01" };
        bool closed = false;
        model.RequestClose += _ => closed = true;

        model.SaveCommand.Execute(null);

        Assert.False(closed);
        Assert.Null(model.Result);
        Assert.Contains("已存在", model.ErrorText);
    }

    [Fact]
    public void InvalidNumericInputKeepsEditorOpenWithoutSavingOldValue()
    {
        var model = new DeviceEditorViewModel(null, new TestPorts())
        {
            Name = "模拟电压", Code = "V02", MinimumText = "oops"
        };
        bool closed = false;
        model.RequestClose += _ => closed = true;

        model.SaveCommand.Execute(null);

        Assert.False(closed);
        Assert.Null(model.Result);
        Assert.Contains("无效", model.ErrorText);
    }

    [Fact]
    public void SerialEditorKeepsAllCommunicationSettings()
    {
        var model = new DeviceEditorViewModel(null, new TestPorts())
        {
            Name = "电子秤", Code = "S02", Kind = DeviceKind.Serial, PortName = "COM7",
            BaudRateText = "115200", DataBits = 7, StopBits = SerialStopBits.Two,
            Parity = SerialParity.Even, FlowControl = SerialFlowControl.RequestToSend,
            Terminator = "\\r\\n", EncodingName = "ASCII"
        };

        model.SaveCommand.Execute(null);

        Assert.NotNull(model.Result);
        Assert.Equal(DeviceKind.Serial, model.Result.Kind);
        Assert.Equal(115200, model.Result.Serial.BaudRate);
        Assert.Equal(7, model.Result.Serial.DataBits);
        Assert.Equal(SerialStopBits.Two, model.Result.Serial.StopBits);
        Assert.Equal(SerialParity.Even, model.Result.Serial.Parity);
        Assert.Equal(SerialFlowControl.RequestToSend, model.Result.Serial.FlowControl);
        Assert.Equal("\\r\\n", model.Result.Serial.Terminator);
        Assert.Equal("ASCII", model.Result.Serial.EncodingName);
    }

    private sealed class TestPorts : IPortCatalog
    {
        public IReadOnlyList<string> GetAvailablePorts() => ["COM7", "COM8"];
    }
}
