# 架构说明

## 分层

```text
App (WPF / MVVM / Host / Serilog)
  ├── Services ── Core
  ├── Infrastructure ── Core
  └── Core
Tests ── App / Services / Infrastructure / Core
```

现有 Core、Services、Infrastructure、App 四层保持不变。`Core` 定义设备、通信事件及 `IDeviceManager`、`ICommunicationChannel`、`IDeviceRepository`、`IFrameParser`、`IPortCatalog` 等契约；`Services` 完成设备目录编排、参数验证和文本解析；`Infrastructure` 实现 JSON 存储、真实串口和模拟通道；`App` 负责依赖注入、页面、弹窗和 UI 线程调度。页面 CodeBehind 仅初始化 XAML，不包含设备业务逻辑。

## 设备配置与通信

设备定义包含稳定 ID、名称、唯一编号、类型、备注、串口或模拟参数及解析规则。配置文件为 `%LocalAppData%\IndustrialDataAnalyzer\devices.json`，采用临时文件写入、刷盘后原子替换；旧版保留为 `.bak`，主文件 JSON 无效时会尝试读取备份。测量报文和解析记录没有落盘。

`DeviceManager` 串行化设备增删改、连接和断开操作，阻止重复连接。串口和模拟设备都由 `ICommunicationChannel` 表示，由工厂按类型创建。串口接收循环在后台读取 `SerialPort.BaseStream`，`LineFrameBuffer` 用配置的结束符拼接拆分字节流；模拟通道按采样间隔生成范围内的数值。通道统一发出完整原始报文与连接状态，`FrameParser` 提取一个数值并保留原文或错误。设备切换只改变当前查看对象，不会暗中关闭其他设备。

停止采集、编辑或删除正在连接的设备、应用退出时，管理器先关闭对应通道并释放端口。串口读取异常和端口消失会标记为连接异常；用户可手动重连。通信与解析错误进入日志，页面显示可读状态。解析规则的正则匹配设有 100 ms 超时。

## 页面与启动

`MainWindowViewModel` 保留八个导航项与单窗口 ContentControl。设备管理页提供弹窗编辑、查询和连接操作；采集页按所选设备显示最近原始报文及解析结果；总览页订阅设备目录与连接状态，更新设备总数和在线数。其他页面沿用第一阶段布局。

`App.xaml.cs` 先创建用户数据与日志目录，再配置 Serilog 和 Host；Host 启动后加载设备配置，再显示窗口。启动早期若日志初始化失败，错误对话框直接显示原因。关闭窗口时异步断开所有设备，再停止 Host。日志按日期和大小滚动，避免高频通信无限增长单文件。

## 后续阶段边界

本阶段只保存设备配置。SQLite 测量存储、历史分析、实时曲线、报警规则和报表导出均未接入。设备协议目前是可配置结束符的文本行协议；二进制、Modbus 或设备专用协议需以后按实机协议扩展。
