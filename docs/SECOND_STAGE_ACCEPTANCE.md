# 第二阶段验收报告：设备管理与通信

日期：2026-11-14。环境：Windows x64，.NET SDK 10.0.400，WPF / MVVM。此报告只对应第二阶段，不包含第三阶段工作。

## 完成情况

| 验收项 | 结果与证据 |
| --- | --- |
| 设备新增、编辑、删除、查询与重启恢复 | 已实现。设备编号唯一，弹窗编辑；JSON 安全替换、备份回退。xUnit 覆盖增删改、重新加载、备份读取及编辑器校验。 |
| 串口参数、COM 扫描与通信 | 已实现 COM、波特率、数据位、停止位、校验位、流控、编码和结束符配置；`System.IO.Ports` 异步收发、帧缓存、连接/断开/重连、状态与日志。由于本机无目标 COM 仪器，真实收发和拔插尚待联机验收。 |
| 模拟设备 | 温度、重量、电压均可按范围和间隔持续输出；来源标记为“模拟”。单元测试覆盖三种类型、输出范围、停止后无继续报文和重连。 |
| 数据解析 | 文本数值、分隔符字段、正则捕获组；解析失败保留原文与错误并记录日志。测试覆盖正常、缺字段、非法正则、无效数值、拆包粘包和跨块 UTF-8。 |
| 页面联动 | 设备页可查询、编辑和连接；采集页可选设备、启动/停止、发送串口文本、查看状态、原文、解析值和近期记录；总览实时显示设备总数、在线数、本次报文数。八个原页面均能打开。 |
| 退出与资源释放 | 应用退出前异步关闭所有通道；编辑、删除和手动停止时释放相应通道。测试覆盖停止、编辑和删除连接中的模拟设备。 |

## 实际执行结果

本机 .NET 的官方 NuGet HTTPS 请求沿用第一阶段已记录的 TLS 问题；本次保持仓库与 CI 的默认官方源不变，使用仓库内可选的本机回环桥接脚本完成本地还原，**未关闭 NuGet 安全审计**。首次并行还原因本机 NuGet 缓存权限和桥接并发请求失败；改用仓库内缓存路径及 `--disable-parallel` 后成功。

```powershell
$env:NUGET_HTTP_CACHE_PATH='D:\Projects\IndustrialDataAnalyzer\.nuget-http-cache'
dotnet restore IndustrialDataAnalyzer.slnx --configfile scripts/NuGet.Local.config --no-http-cache --disable-parallel -p:RestorePackagesPath=D:\Projects\IndustrialDataAnalyzer\.packages
dotnet build IndustrialDataAnalyzer.slnx -c Debug --no-restore -p:RestorePackagesPath=D:\Projects\IndustrialDataAnalyzer\.packages
dotnet test IndustrialDataAnalyzer.slnx -c Debug --no-build --no-restore -p:RestorePackagesPath=D:\Projects\IndustrialDataAnalyzer\.packages
```

- `restore`：Core、Services、Infrastructure、App、Tests 五个项目全部成功，退出码 0。
- `build`：Debug 成功，**0 警告、0 错误**，退出码 0。
- `test`：**37/37 通过**，退出码 0；测试使用系统临时目录和随机子目录，不依赖固定用户文件路径。新增测试还验证非法数值不会以旧值误保存、连接失败后的手动重连，以及模拟设备启动后立即停止。
- `git diff --check`：无空白错误。Git 提示工作区部分文件未来可能转换为 CRLF，仅为行尾提示。

### WPF 运行与显示

实际启动 Debug 应用，用 `--capture-all` 在 **1440×900** 和 **1120×720** 分别打开并捕获八个页面，退出码均为 0。图像位于 [1440×900 八页截图](../artifacts/phase2/pages-1440/) 和 [1120×720 八页截图](../artifacts/phase2/pages-1120/)。最小尺寸下长页面使用主内容区纵向滚动，未观察到卡片重叠或明显横向文字溢出。

另以仅在内存中的测试设备运行模拟采集，检查了设备列表、在线数、原始报文、解析值和总览计数；截图位于 [1440×900 带数据页面](../artifacts/phase2/demo-1440/) 和 [1120×720 带数据页面](../artifacts/phase2/demo-1120/)。[模拟设备弹窗](../artifacts/phase2/editor/simulator.png) 与 [串口设备弹窗](../artifacts/phase2/editor/serial.png) 也已实际渲染检查。截图辅助命令只在 Debug 构建存在，不修改正式设备配置。

## 修改文件

### 配置与文档

`Directory.Packages.props`、`README.md`、`docs/ARCHITECTURE.md`、`docs/DEVELOPMENT.md`、`docs/DEVELOPMENT_PLAN.md`、`docs/DEVICE_COMMUNICATION.md`、`docs/SECOND_STAGE_ACCEPTANCE.md`；`artifacts/phase2/` 下的页面与弹窗截图。

### Core

`src/IndustrialDataAnalyzer.Core/Abstractions/IApplicationPaths.cs`、`ICommunicationChannel.cs`、`IDeviceManager.cs`、`IDeviceRepository.cs`、`IFrameParser.cs`；`Models/CommunicationEvents.cs`、`DeviceDefinition.cs`、`DeviceOptions.cs`。

### Services 与 Infrastructure

`src/IndustrialDataAnalyzer.Services/IndustrialDataAnalyzer.Services.csproj`、`Devices/DeviceManager.cs`、`Devices/FrameParser.cs`；`src/IndustrialDataAnalyzer.Infrastructure/IndustrialDataAnalyzer.Infrastructure.csproj`、`Paths/ApplicationPaths.cs`、`Persistence/JsonDeviceRepository.cs`、`Communication/CommunicationChannelFactory.cs`、`LineFrameBuffer.cs`、`SerialDeviceChannel.cs`、`SerialPortCatalog.cs`、`SimulatedDeviceChannel.cs`。

### App

`src/IndustrialDataAnalyzer.App/App.xaml.cs`、`Services/IDeviceDialogService.cs`、`DeviceDialogService.cs`、`ViewModels/DeviceEditorViewModel.cs`、`DeviceRowViewModel.cs`、`UiThread.cs`、`MainWindowViewModel.cs`、`ViewModels/Pages/DevicesViewModel.cs`、`AcquisitionViewModel.cs`、`OverviewViewModel.cs`、`Views/DeviceEditorWindow.xaml`、`DeviceEditorWindow.xaml.cs`、`MainWindow.xaml`、`Views/Pages/DevicesPage.xaml`、`AcquisitionPage.xaml`、`OverviewPage.xaml`、`SettingsPage.xaml`。

### 测试

`tests/IndustrialDataAnalyzer.Tests/AcquisitionViewModelTests.cs`、`DeviceEditorViewModelTests.cs`、`DeviceManagerTests.cs`、`FrameParserTests.cs`、`JsonDeviceRepositoryTests.cs`、`LineFrameBufferTests.cs`。

本轮开始时工作区已存在第一阶段未提交的质量保障改动：`.gitignore`、`.github/workflows/windows-build.yml`、`scripts/nuget_proxy.py`、`docs/FIRST_STAGE_ACCEPTANCE.md`、`tests/IndustrialDataAnalyzer.Tests/ApplicationFoundationTests.cs`、`MainWindowViewModelTests.cs`、`IndustrialDataAnalyzer.Tests.csproj` 等。本轮保留了这些文件，没有重置第一阶段工作。

## 未验证事项与边界

1. **真实 COM 联机**：本机没有目标仪器/驱动/串口回环对。端口扫描、真实设备的连接、发送、接收、拔插断线及重连未经过硬件验证；必须在目标设备上按协议联调。
2. **断线判定**：当前可识别串口 I/O 错误、端口关闭以及系统端口列表中的消失。若仪器停止发数但 COM 仍保持可用，没有协议心跳就无法判断业务层掉线。
3. **长时间运行与压力**：已验证模拟设备的短时间连续输出和停止；尚未完成多小时串口压力与驱动兼容性测试。
4. **GitHub Actions 远程结果**：Windows 工作流在每次推送后重新运行；远端结果以对应提交的 GitHub Actions 页面为准。
5. **第三阶段功能**：SQLite 测量存储、历史分析、实时曲线、报警规则及报表导出没有开发。运行期原始/解析记录在内存中，每台设备保留最近 200 条，退出后清除。

**验收结论：** 第二阶段软件实现、模拟设备流程、页面运行、编译和自动化测试通过；真实串口设备联机项待现场验证。未进入第三阶段。
