# 通用工业设备数据采集与分析软件

IndustrialDataAnalyzer 是面向工业仪器、自动化检测设备和实验设备的 Windows 桌面软件。当前仓库完成 **V1.0 第一阶段**：.NET 10 / WPF 工程、MVVM 导航、基础服务、日志和八个页面的界面框架。

## 当前可用内容

- 单窗口导航：系统总览、设备管理、实时数据采集、实时监测、历史数据分析、报警中心、报表管理、系统设置。
- 统一的页头、指标卡、状态标签、空状态、区域卡片和只读表格样式。
- 启动时创建 `%LocalAppData%\IndustrialDataAnalyzer\Logs`，按天滚动日志，最多保留 30 天。
- 系统设置显示软件版本、运行环境、数据目录和日志目录。
- 尚未接入设备，所有统计为零或明确的空状态；不生成演示采集数据。

第一阶段没有串口、Modbus、模拟设备、SQLite、实时曲线、报警规则或报表导出。后续范围见 [开发计划](docs/DEVELOPMENT_PLAN.md)。

## 工程结构

```text
IndustrialDataAnalyzer.slnx
Directory.Build.props / Directory.Packages.props
src/
  IndustrialDataAnalyzer.Core/             接口及领域基础
  IndustrialDataAnalyzer.Services/         应用信息等服务
  IndustrialDataAnalyzer.Infrastructure/   用户数据目录等基础设施
  IndustrialDataAnalyzer.App/              WPF 页面、控件、资源与导航
tests/IndustrialDataAnalyzer.Tests/        xUnit 基础服务测试
docs/                                   架构、开发和阶段计划
publish/                                预留免安装发布目录
```

依赖方向：`Services → Core`、`Infrastructure → Core`、`App → Core + Services + Infrastructure`。详细说明见 [架构文档](docs/ARCHITECTURE.md)。

## 开发和运行

在 Windows x64 上安装 .NET 10 SDK。用 PowerShell 在仓库根目录执行：

```powershell
dotnet --info
dotnet restore IndustrialDataAnalyzer.slnx
dotnet build IndustrialDataAnalyzer.slnx -c Debug
dotnet test IndustrialDataAnalyzer.slnx -c Debug
dotnet run --project src/IndustrialDataAnalyzer.App/IndustrialDataAnalyzer.App.csproj
```

也可以用支持 .NET 10 WPF 开发的 Visual Studio 打开 `.slnx`。本机若遇到 NuGet TLS 错误，见 [开发说明](docs/DEVELOPMENT.md) 中的受限网络恢复方法。

实际运行的 [系统总览截图](artifacts/pages/01-Overview.png) 和其余七页截图位于 `artifacts/pages/`。这些截图只证明第一阶段的界面渲染，设备与数据业务尚未实现。
