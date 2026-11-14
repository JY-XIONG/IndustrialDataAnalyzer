# 开发说明

## 环境

- Windows x64，.NET 10 SDK；若使用 Visual Studio，安装 .NET 桌面开发组件。
- 项目使用 Nullable Reference Types、Implicit Usings 和中央 NuGet 包版本管理。
- 运行时日志位于 `%LocalAppData%\IndustrialDataAnalyzer\Logs`。请确保当前用户可写。
- `.github/workflows/windows-build.yml` 在 push 与 pull_request 时使用 Windows 和 .NET 10 SDK，按“还原、Debug 编译、xUnit 测试”执行；正常构建使用官方 NuGet 源与默认依赖安全审计。

## 常用命令

```powershell
dotnet restore IndustrialDataAnalyzer.slnx
dotnet build IndustrialDataAnalyzer.slnx -c Debug
dotnet test IndustrialDataAnalyzer.slnx -c Debug
dotnet run --project src/IndustrialDataAnalyzer.App/IndustrialDataAnalyzer.App.csproj
```

Debug 版本可逐页导出 WPF 渲染图：

```powershell
dotnet run --project src/IndustrialDataAnalyzer.App/IndustrialDataAnalyzer.App.csproj -- --capture-all artifacts/pages
```

可在末尾添加 `--min-size`，以 1120 × 720 最小窗口尺寸导出。截图模式依次执行八个页面的导航命令并正常退出。

第二阶段还可用 Debug 专用命令检查弹窗及带模拟数据的页面，不修改正式设备配置：

```powershell
dotnet run --project src/IndustrialDataAnalyzer.App/IndustrialDataAnalyzer.App.csproj -- --capture-editor .test-temp/phase2-ui/editor.png
dotnet run --project src/IndustrialDataAnalyzer.App/IndustrialDataAnalyzer.App.csproj -- --capture-editor .test-temp/phase2-ui/serial-editor.png --serial
dotnet run --project src/IndustrialDataAnalyzer.App/IndustrialDataAnalyzer.App.csproj -- --capture-demo .test-temp/phase2-ui/demo --min-size
dotnet run --project src/IndustrialDataAnalyzer.App/IndustrialDataAnalyzer.App.csproj -- --capture-storage-demo .test-temp/phase3-ui/storage --min-size
```

## 当前环境的 NuGet TLS 问题

2026-11-14，在本工作环境中，.NET 的默认 NuGet HTTPS 请求遇到 Windows TLS 凭据错误。仓库提供了仅监听本机回环地址的临时桥接脚本。它通过 Python 标准库从官方 NuGet API 读取包，供当前机器恢复；正常网络环境无需使用。

先在一个终端运行 `python scripts/nuget_proxy.py`，再在另一个终端运行：

```powershell
$env:NUGET_HTTP_CACHE_PATH = "$PWD\.nuget-http-cache"
dotnet restore IndustrialDataAnalyzer.slnx --configfile scripts/NuGet.Local.config --no-http-cache --disable-parallel -p:RestorePackagesPath=$PWD\.packages
dotnet build IndustrialDataAnalyzer.slnx -c Debug --no-restore -p:RestorePackagesPath=$PWD\.packages
dotnet test IndustrialDataAnalyzer.slnx -c Debug --no-build --no-restore -p:RestorePackagesPath=$PWD\.packages
```

桥接脚本也转发 NuGet 漏洞信息，恢复命令不关闭安全审计。完成后关闭桥接脚本。`.packages` 和 `.nuget-http-cache` 是本机缓存，已加入 `.gitignore`。

## 第一阶段历史验证（2026-11-14）

完整结论与待确认事项见 [第一阶段质量验收报告](FIRST_STAGE_ACCEPTANCE.md)。

- 本机 Windows x64、.NET SDK 10.0.400。直接运行 `dotnet restore IndustrialDataAnalyzer.slnx` 因本机 TLS 凭据错误报 NU1301；使用上述可选桥接后，保持安全审计启用，五个项目全部还原成功。
- `dotnet build IndustrialDataAnalyzer.slnx -c Debug --no-restore` 成功，0 警告、0 错误；`dotnet test IndustrialDataAnalyzer.slnx -c Debug --no-build --no-restore` 共 13 项通过。测试使用系统临时目录和随机子目录，没有依赖固定用户路径；切换临时目录后复测仍为 13 项通过。
- 在 Windows 实际运行 Debug 应用，逐页生成 1440 × 900 和 1120 × 720 的八页 WPF 截图，位于 `artifacts/pages/` 与 `artifacts/min-pages/`。人工检查未见文字横向溢出或卡片重叠；最小尺寸下较长页面使用纵向滚动。历史数据分析页的滚动范围已通过 Windows UI 自动化从 0% 移至 100%。
- 临时注入“日志建立前启动失败”后，实际错误对话框显示了失败原因和 `%LocalAppData%` 写入权限提示；验证后已移除注入代码。
- 当前工作流文件尚未在 GitHub 托管运行器上执行，需推送后查看 Actions 结果。CI 执行还原、编译和单元测试，不代替交互式 UI 检查。
- 当时为第一阶段空状态界面；设备通信、模拟设备、SQLite、分析与报表功能均未接入。该结论仅对应第一阶段版本。

## 第二阶段开发与验证

当前设备配置文件在 `%LocalAppData%\IndustrialDataAnalyzer\devices.json`，日志在 `Logs` 子目录。设备参数、协议规则及模拟设备用法见 [设备通信说明](DEVICE_COMMUNICATION.md)。串口联机前需确认设备驱动、COM 名称、波特率、结束符和解析规则与仪器一致。

本阶段增加了设备 CRUD/JSON 持久化、串口与模拟统一通道、报文组装、数值解析及页面联动。测试覆盖配置重启恢复与备份、设备编号唯一性、模拟设备持续输出和停止、拆包粘包、UTF-8 跨块接收及三种解析方式。实际构建和测试结果见 [第二阶段验收报告](SECOND_STAGE_ACCEPTANCE.md)。

不要把用户数据写到应用目录，也不要在 UI 线程同步等待异步任务。真实设备协议、驱动和拔插行为须在目标 Windows 机器上联机验证。

## 第三阶段开发与验证

测量数据库位于 `%LocalAppData%\IndustrialDataAnalyzer\measurements.db`，设备配置仍为 `devices.json`。`--capture-storage-demo` 只在 Debug 构建中使用，在截图目录的 `.local-data` 内创建隔离数据库、采集模拟数据并截图采集、总览和历史页面；不会写入正式用户数据。数据库表、写入队列、恢复文件和查询说明见 [架构说明](ARCHITECTURE.md)，实际结果见 [第三阶段验收报告](THIRD_STAGE_ACCEPTANCE.md)。

[Microsoft.Data.Sqlite 官方说明](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)指出其 ADO.NET 异步方法内部仍执行同步 SQLite I/O，因此仓储方法将数据库工作放到后台任务，采集回调不等待数据库；界面只使用异步调用。正常停止等待已接受数据提交，数据库持续故障时显示待写与丢失数量，退出时转存待写报文，重启后回放。真实 COM 仪器、驱动及长期压力仍须现场验证。实时曲线、统计分析、报警和报表没有开发。
