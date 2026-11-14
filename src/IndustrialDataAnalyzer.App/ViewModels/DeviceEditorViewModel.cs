using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using ParseMode = IndustrialDataAnalyzer.Core.Models.ParserMode;

namespace IndustrialDataAnalyzer.App.ViewModels;

/// <summary>Editable device configuration shown in the modal editor.</summary>
public partial class DeviceEditorViewModel : ObservableObject
{
    private readonly DeviceDefinition? existing;
    private readonly IPortCatalog portCatalog;
    private readonly IReadOnlyList<DeviceDefinition> catalog;
    public DeviceEditorViewModel(DeviceDefinition? existing, IPortCatalog portCatalog, IReadOnlyList<DeviceDefinition>? catalog = null)
    {
        this.existing = existing;
        this.portCatalog = portCatalog;
        this.catalog = catalog ?? [];
        Name = existing?.Name ?? string.Empty;
        Code = existing?.Code ?? string.Empty;
        Kind = existing?.Kind ?? DeviceKind.Simulator;
        Notes = existing?.Notes ?? string.Empty;
        PortName = existing?.Serial.PortName ?? string.Empty;
        BaudRateText = (existing?.Serial.BaudRate ?? 9600).ToString(CultureInfo.InvariantCulture);
        DataBits = existing?.Serial.DataBits ?? 8;
        StopBits = existing?.Serial.StopBits ?? SerialStopBits.One;
        Parity = existing?.Serial.Parity ?? SerialParity.None;
        FlowControl = existing?.Serial.FlowControl ?? SerialFlowControl.None;
        Terminator = existing?.Serial.Terminator ?? "\\n";
        EncodingName = existing?.Serial.EncodingName ?? "UTF-8";
        Measurement = existing?.Simulator.Measurement ?? MeasurementKind.Temperature;
        MinimumText = (existing?.Simulator.Minimum ?? 18).ToString(CultureInfo.InvariantCulture);
        MaximumText = (existing?.Simulator.Maximum ?? 30).ToString(CultureInfo.InvariantCulture);
        IntervalText = (existing?.Simulator.IntervalMilliseconds ?? 1000).ToString(CultureInfo.InvariantCulture);
        ParserMode = existing?.Parser.Mode ?? ParseMode.Numeric;
        FieldName = existing?.Parser.FieldName ?? "测量值";
        Unit = existing?.Parser.Unit ?? string.Empty;
        Separator = existing?.Parser.Separator ?? ",";
        FieldIndexText = (existing?.Parser.FieldIndex ?? 0).ToString(CultureInfo.InvariantCulture);
        Pattern = existing?.Parser.Pattern ?? @"([-+]?\d+(?:\.\d+)?)";
        CaptureGroup = existing?.Parser.CaptureGroup ?? "1";
        if (existing is null) ApplyMeasurementDefaults();
        RefreshPorts();
    }

    public event Action<bool>? RequestClose;
    public DeviceDefinition? Result { get; private set; }
    public string DialogTitle => existing is null ? "新增设备" : "编辑设备";
    public IReadOnlyList<EnumChoice<DeviceKind>> DeviceKinds { get; } =
        [new(DeviceKind.Serial, "真实串口"), new(DeviceKind.Simulator, "模拟设备")];
    public IReadOnlyList<EnumChoice<MeasurementKind>> Measurements { get; } =
        [new(MeasurementKind.Temperature, "温度"), new(MeasurementKind.Weight, "重量"), new(MeasurementKind.Voltage, "电压")];
    public IReadOnlyList<EnumChoice<SerialStopBits>> StopBitChoices { get; } =
        [new(SerialStopBits.One, "1 位"), new(SerialStopBits.OnePointFive, "1.5 位"), new(SerialStopBits.Two, "2 位")];
    public IReadOnlyList<EnumChoice<SerialParity>> ParityChoices { get; } =
        [new(SerialParity.None, "无"), new(SerialParity.Odd, "奇校验"), new(SerialParity.Even, "偶校验"),
         new(SerialParity.Mark, "Mark"), new(SerialParity.Space, "Space")];
    public IReadOnlyList<EnumChoice<SerialFlowControl>> FlowChoices { get; } =
        [new(SerialFlowControl.None, "无"), new(SerialFlowControl.XOnXOff, "XON/XOFF"),
         new(SerialFlowControl.RequestToSend, "RTS/CTS"), new(SerialFlowControl.RequestToSendXOnXOff, "RTS/CTS + XON/XOFF")];
    public IReadOnlyList<EnumChoice<ParseMode>> ParserModes { get; } =
        [new(ParseMode.Numeric, "文本数值"), new(ParseMode.Delimited, "分隔符"), new(ParseMode.Regex, "正则表达式")];
    public IReadOnlyList<string> Encodings { get; } = ["UTF-8", "ASCII"];
    public ObservableCollection<string> AvailablePorts { get; } = [];
    public IReadOnlyList<string> BaudRates { get; } = ["1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200"];
    public IReadOnlyList<int> DataBitChoices { get; } = [5, 6, 7, 8];

    [ObservableProperty] private string name;
    [ObservableProperty] private string code;
    [ObservableProperty] private DeviceKind kind;
    [ObservableProperty] private string notes;
    [ObservableProperty] private string portName;
    [ObservableProperty] private string baudRateText;
    [ObservableProperty] private int dataBits;
    [ObservableProperty] private SerialStopBits stopBits;
    [ObservableProperty] private SerialParity parity;
    [ObservableProperty] private SerialFlowControl flowControl;
    [ObservableProperty] private string terminator;
    [ObservableProperty] private string encodingName;
    [ObservableProperty] private MeasurementKind measurement;
    [ObservableProperty] private string minimumText;
    [ObservableProperty] private string maximumText;
    [ObservableProperty] private string intervalText;
    [ObservableProperty] private ParseMode parserMode;
    [ObservableProperty] private string fieldName;
    [ObservableProperty] private string unit;
    [ObservableProperty] private string separator;
    [ObservableProperty] private string fieldIndexText;
    [ObservableProperty] private string pattern;
    [ObservableProperty] private string captureGroup;
    [ObservableProperty] private string? errorText;

    public bool IsSerial => Kind == DeviceKind.Serial;
    public bool IsSimulator => Kind == DeviceKind.Simulator;
    partial void OnKindChanged(DeviceKind value)
    {
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsSimulator));
    }

    partial void OnMeasurementChanged(MeasurementKind value) => ApplyMeasurementDefaults();

    private void ApplyMeasurementDefaults()
    {
        if (string.IsNullOrWhiteSpace(FieldName) || FieldName is "测量值" or "温度" or "重量" or "电压")
            FieldName = Measurement switch
        {
            MeasurementKind.Temperature => "温度",
            MeasurementKind.Weight => "重量",
            _ => "电压"
        };
        if (string.IsNullOrWhiteSpace(Unit) || Unit is "℃" or "g" or "V")
            Unit = Measurement switch
        {
            MeasurementKind.Temperature => "℃",
            MeasurementKind.Weight => "g",
            _ => "V"
        };
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        try
        {
            string previous = PortName;
            AvailablePorts.Clear();
            foreach (string port in portCatalog.GetAvailablePorts()) AvailablePorts.Add(port);
            if (!string.IsNullOrWhiteSpace(previous) && !AvailablePorts.Contains(previous)) AvailablePorts.Add(previous);
            PortName = previous;
            ErrorText = null;
        }
        catch (Exception exception)
        {
            ErrorText = "扫描 COM 端口失败：" + exception.Message;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Code))
        {
            ErrorText = "请填写设备名称和编号。";
            return;
        }
        if (catalog.Any(device => device.Id != existing?.Id &&
            string.Equals(device.Code, Code.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ErrorText = "设备编号已存在。";
            return;
        }
        if (Kind == DeviceKind.Serial && string.IsNullOrWhiteSpace(PortName))
        {
            ErrorText = "请选择或输入 COM 端口。";
            return;
        }
        int baudRate = 9600;
        if (Kind == DeviceKind.Serial &&
            (!int.TryParse(BaudRateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out baudRate) ||
             baudRate <= 0 || DataBits is < 5 or > 8))
        {
            ErrorText = "波特率或数据位无效。";
            return;
        }
        double minimum = 18, maximum = 30;
        int interval = 1000;
        if (Kind == DeviceKind.Simulator &&
            (!double.TryParse(MinimumText, NumberStyles.Float, CultureInfo.InvariantCulture, out minimum) ||
             !double.TryParse(MaximumText, NumberStyles.Float, CultureInfo.InvariantCulture, out maximum) ||
             !double.IsFinite(minimum) || !double.IsFinite(maximum) ||
             !int.TryParse(IntervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval) ||
             interval is < 50 or > 3_600_000 || minimum > maximum))
        {
            ErrorText = "模拟设备范围或采样间隔无效；间隔须为 50～3,600,000 毫秒。";
            return;
        }
        if (!int.TryParse(FieldIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fieldIndex) ||
            fieldIndex < 0 || string.IsNullOrWhiteSpace(FieldName) ||
            (ParserMode == ParseMode.Delimited && string.IsNullOrEmpty(Separator)))
        {
            ErrorText = "解析字段、分隔符或字段序号无效。";
            return;
        }
        if (Kind == DeviceKind.Serial)
        {
            try
            {
                _ = Encoding.GetEncoding(EncodingName);
                if (string.IsNullOrEmpty(Terminator)) throw new ArgumentException("报文结束符不能为空。");
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                ErrorText = "串口编码或报文结束符无效：" + exception.Message;
                return;
            }
        }
        if (ParserMode == ParseMode.Regex)
        {
            try { _ = new Regex(Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException exception) { ErrorText = "正则表达式无效：" + exception.Message; return; }
        }
        Result = new DeviceDefinition
        {
            Id = existing?.Id ?? Guid.NewGuid(), Name = Name.Trim(), Code = Code.Trim(), Kind = Kind,
            Notes = Notes.Trim(),
            Serial = Kind == DeviceKind.Serial ? new SerialOptions { PortName = PortName.Trim(), BaudRate = baudRate, DataBits = DataBits,
                StopBits = StopBits, Parity = Parity, FlowControl = FlowControl,
                Terminator = Terminator, EncodingName = EncodingName } : existing?.Serial ?? new(),
            Simulator = Kind == DeviceKind.Simulator ? new SimulatorOptions { Measurement = Measurement, Minimum = minimum,
                Maximum = maximum, IntervalMilliseconds = interval } : existing?.Simulator ?? new(),
            Parser = new ParserOptions { Mode = ParserMode, FieldName = FieldName.Trim(), Unit = Unit.Trim(),
                Separator = Separator, FieldIndex = fieldIndex, Pattern = Pattern, CaptureGroup = CaptureGroup }
        };
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}

public sealed record EnumChoice<T>(T Value, string Label);
