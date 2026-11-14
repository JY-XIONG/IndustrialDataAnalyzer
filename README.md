# 通用工业设备数据采集与分析软件

IndustrialDataAnalyzer 是 .NET 10 / WPF 桌面软件。当前完成 **V1.0 第三阶段：采集会话、SQLite 持久化与历史查询**，保留前两阶段的八页导航、设备管理、通信和 Windows 构建工作流。

## 当前功能

- 设备管理：新增、编辑、删除、按名称/编号/端口/备注查询；支持真实串口和模拟设备；配置保存在 `%LocalAppData%\IndustrialDataAnalyzer\devices.json`，更新时安全替换并保留 `.bak`。
- 串口通信：扫描 COM 端口，配置波特率、数据位、停止位、校验位、流控、UTF-8/ASCII 编码和报文结束符；异步连接、收发、断开及手动重连。按结束符组装拆分或粘连的报文；I/O 失败或端口从系统消失时更新异常状态。
- 模拟设备：温度、重量、电压，范围和采样间隔可配置；无硬件也可连续采集，数据明确标为“模拟”。
- 数据解析：文本数值、分隔符字段和正则捕获组；原始报文、结果及失败原因在实时采集页显示，运行期间每台设备保留最近 200 条记录。
- 自动存储：每次启动采集建立独立会话，真实串口和模拟设备的原始报文、解析结果、来源与 UTC 时间写入 `%LocalAppData%\IndustrialDataAnalyzer\measurements.db`。采集页显示已保存、待写和丢失数量；停止采集等待已接受记录落盘。
- 历史查询：按设备、本地日期范围和采集会话查询 SQLite 记录，每页 50 条，可查看原文和解析错误。删除设备配置不会删除历史数据。
- 系统总览：设备总数、在线数、连接状态和今日已保存数据量随采集变化更新。实时曲线、统计、报警和报表页面保留后续阶段占位。
- 日志：`%LocalAppData%\IndustrialDataAnalyzer\Logs` 中的滚动文件记录连接、断开、报文与解析错误。单文件上限 10 MiB，最多保留 30 个文件 / 30 天。

本阶段只提供基础历史记录查询。**实时曲线、统计分析、报警规则和报表导出尚未开发**。设备配置继续使用原有 `devices.json`，没有迁移。

## 构建和运行

在 Windows x64 安装 .NET 10 SDK 后，从仓库根目录执行：

```powershell
dotnet restore IndustrialDataAnalyzer.slnx
dotnet build IndustrialDataAnalyzer.slnx -c Debug
dotnet test IndustrialDataAnalyzer.slnx -c Debug
dotnet run --project src/IndustrialDataAnalyzer.App/IndustrialDataAnalyzer.App.csproj
```

正常构建使用官方 NuGet 源及默认的依赖安全审计。本机若遇到 NuGet TLS 故障，可按 [开发说明](docs/DEVELOPMENT.md) 使用仅供本机恢复的可选桥接脚本。GitHub Actions 在 push 和 pull_request 时执行 Windows / .NET 10 还原、Debug 编译和测试。

## 设备使用

在“设备管理”新增设备。串口设备选择当前 COM 端口并填写通信参数；模拟设备选择测量类型、数值范围和采样间隔。可为设备设置数值、分隔符或正则解析规则。保存后在设备页连接，或在“实时数据采集”选择设备并启动；该页面显示当前状态、最新测量值、原始报文、解析记录和存储状态。串口发送会自动追加配置的报文结束符。停止或退出时连接会释放并等待已接受记录写入；历史页可按设备、日期和会话分页查询。

默认串口结束符为 `\n`，也支持 `\r\n` 等转义写法；默认编码为 UTF-8，数值解析使用小数点。真实设备需要输出与配置一致的文本行协议。详见 [设备通信说明](docs/DEVICE_COMMUNICATION.md)。

## 工程结构

```text
src/IndustrialDataAnalyzer.Core/             设备与采集模型、服务接口
src/IndustrialDataAnalyzer.Services/         设备编排、解析与批量写入
src/IndustrialDataAnalyzer.Infrastructure/   JSON 配置、SQLite、串口和模拟通信
src/IndustrialDataAnalyzer.App/              WPF 页面、视图模型与服务组装
tests/IndustrialDataAnalyzer.Tests/          xUnit 测试
docs/                                   架构、开发和阶段验收文档
```

分层依赖关系及资源释放方式见 [架构说明](docs/ARCHITECTURE.md)。本阶段的测试证据、未验证事项和验收结论见 [第三阶段验收报告](docs/THIRD_STAGE_ACCEPTANCE.md)；[第二阶段报告](docs/SECOND_STAGE_ACCEPTANCE.md)和[第一阶段报告](docs/FIRST_STAGE_ACCEPTANCE.md)保留为历史记录。
