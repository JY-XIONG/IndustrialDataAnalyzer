# 第三阶段验收报告：采集会话与 SQLite 持久化

日期：2026-11-14。环境：Windows x64、.NET SDK 10.0.400、WPF / MVVM。本报告只对应第三阶段；第一、二阶段历史结果见各自验收报告。

## 完成情况

| 验收项 | 结果与证据 |
| --- | --- |
| SQLite 与迁移 | `Microsoft.Data.Sqlite` 10.0.15；数据库位于 `%LocalAppData%\IndustrialDataAnalyzer\measurements.db`。会话、测量记录表及设备/会话/时间索引已建立；UTC Unix 毫秒存储，`user_version=1`，升级前备份。测试覆盖重启读取、版本 0 迁移保留旧表、WAL 和备份。 |
| 自动持久化 | 原始报文先独立进入有界存储队列，再由后台解析、100 条批量事务写入；UI 展示保留原有 4096 条 DropOldest 管道。数据库事务成功才增加已保存数。测试覆盖两台模拟设备、多会话归属、批量幂等和停止后落盘。 |
| 故障与积压 | 存储队列满时拒收新报文，增加丢失计数；写入失败保留批次、显示错误并重试。正常停止等待待写记录；数据库持续故障时，退出把待写报文和丢失计数转存恢复文件，下次启动回放。测试覆盖故障、溢出、不虚报已保存、恢复文件回放。 |
| 会话管理 | 每次启动独立会话，保存设备、起止时间、总数、有效数、解析失败数、丢失数和状态；异常退出后未结束会话标记为 `Interrupted`。删除 `devices.json` 中的设备不会删除历史记录。 |
| 历史与界面 | 历史页按设备、本地日期和会话筛选，50 条分页；显示时间、设备、值、单位、来源与状态，选中可查看原文/错误。采集页显示会话、已保存、待写、丢失及存储异常；总览显示今日已保存量。八个页面保留原有导航和视觉风格。 |

## 本地实际验证

- 依赖还原：新增包首次下载使用仓库中可选的本机回环桥接脚本完成；随后执行默认官方源 `dotnet restore IndustrialDataAnalyzer.slnx -p:RestorePackagesPath=D:\Projects\IndustrialDataAnalyzer\.packages`，五个项目均成功，退出码 0。没有禁用 NuGet 安全审计。GitHub Actions 仍使用官方源。
- `dotnet build IndustrialDataAnalyzer.slnx -c Debug --no-restore -p:RestorePackagesPath=D:\Projects\IndustrialDataAnalyzer\.packages`：退出码 0，**0 警告、0 错误**。
- `dotnet test IndustrialDataAnalyzer.slnx -c Debug --no-build --no-restore -p:RestorePackagesPath=D:\Projects\IndustrialDataAnalyzer\.packages`：退出码 0，**53/53 通过，0 跳过**；其中包含原有 46 项及新增 7 项。
- Debug WPF 实际运行：`--capture-all` 在 1440×900 和 1120×720 各捕获 8 页，均正常退出；`--capture-storage-demo` 使用隔离的 SQLite 数据库采集模拟温度，捕获采集、总览及有真实历史行的页面。历史页最小尺寸下筛选控件、表格和纵向滚动可用；截图在本机 `.test-temp/phase3-ui/`，未作为产品数据提交。
- [GitHub Actions Windows build 第 5 次运行](https://github.com/JY-XIONG/IndustrialDataAnalyzer/actions/runs/43580688898) 对第三阶段代码提交 `ee0ad28` 的官方源还原、Debug 编译和全部 xUnit 测试均为 **success**。

## 本轮修改文件

`Directory.Packages.props`、`README.md`；`src/IndustrialDataAnalyzer.Core/Abstractions/ICapturePersistence.cs`、`IMeasurementRepository.cs`、`Models/CaptureRecords.cs`；`src/IndustrialDataAnalyzer.Services/AssemblyInfo.cs`、`Devices/CapturePersistenceService.cs`、`DeviceManager.cs`；`src/IndustrialDataAnalyzer.Infrastructure/IndustrialDataAnalyzer.Infrastructure.csproj`、`Persistence/SqliteMeasurementRepository.cs`；`src/IndustrialDataAnalyzer.App/App.xaml.cs`、`ViewModels/MainWindowViewModel.cs`、`ViewModels/Pages/AcquisitionViewModel.cs`、`OverviewViewModel.cs`、`HistoryViewModel.cs`、`Views/Pages/AcquisitionPage.xaml`、`OverviewPage.xaml`、`HistoryPage.xaml`；`tests/IndustrialDataAnalyzer.Tests/CapturePersistenceServiceTests.cs`、`SqliteMeasurementRepositoryTests.cs`、`HistoryViewModelTests.cs`；`docs/ARCHITECTURE.md`、`DEVELOPMENT.md`、`DEVELOPMENT_PLAN.md`、本报告。

## 已知限制与现场验证

1. 本机没有目标 COM 仪器和驱动，真实串口采集落库、拔插和长时间高频运行仍需现场联调；模拟和可控故障测试不能替代实机验证。
2. 存储队列达到上限时会明确拒收并计入丢失数。若进程突然终止，尚在内存且未落盘或未转存的尾部报文无法保证保留，`Interrupted` 会话的丢失数可能低估。正常停止和正常退出会等待或转存。
3. 数据库持续损坏时不会自动覆盖原库；应用报告故障，并保留 `.bak`、迁移备份和恢复文件供人工检查后恢复。历史会话下拉默认列出最近 200 个，可输入完整会话 ID 查询更早的会话。
4. 第四阶段实时曲线与统计分析、第五阶段报警和报表没有开发。

**验收结论：** 第三阶段软件功能、本地编译、自动化测试与远端 Windows CI 通过；真实硬件和多小时运行待现场验证。

## 2026-11-15 第三阶段代码审查修复复验

- 基线：GitHub `c3d9c5d`。本次只修复关闭、存储恢复和会话状态；`measurements.db`、数据库版本 1、`devices.json` 格式及现有页面架构均保持兼容，未清空或重建用户数据。
- WPF 关闭流程先等待设备与采集服务异步停止，再通过 Host 的 `IAsyncDisposable.DisposeAsync` 释放依赖注入服务；启动失败时也使用同一异步释放路径。仅实现 `IAsyncDisposable` 的服务有专门测试。
- 退出超时后先请求后台写入任务停止，确认其真正结束才读取待写队列并写入恢复文件。任务仍未结束或恢复文件写入失败时，明确报告错误，待写数据不计入已保存数；恢复快照保留供同一进程重试。恢复文件成功写入后重启可回放，记录 ID 保持不变，避免重复计数。
- 会话状态分别为 `Active`（正在采集）、`Stopping`（正在停止）、`WaitingForStorage`（等待存储恢复）、`Completed`（正常结束）、`Failed`（异常结束）；数据库重启时仍将未正常结束的会话标为 `Interrupted`。结束会话失败后可重试，重复提交相同终态不会覆盖已有记录或丢失数。
- 本地复验：Windows x64、.NET 10；Debug 构建与全部 **61/61** xUnit 测试通过。覆盖正常关闭、持续数据库故障、恢复文件写入失败、写入任务超时未停、重启回放、状态转换与重复结束。Debug WPF `--capture-all --min-size` 在隔离数据目录生成八页截图后正常退出（退出码 0，关闭日志无错误）。
- [GitHub Actions Windows build 第 7 次运行](https://github.com/JY-XIONG/IndustrialDataAnalyzer/actions/runs/43684238806) 对修复提交 `a9e9d99` 的官方 NuGet 还原、Debug 编译、xUnit 测试均为 **success**。
- 剩余限制：真实 COM 设备和长时间现场采集未验证；进程被强制终止时，尚在内存且未落盘或未形成恢复文件的尾部报文仍无法保证保留。后台写入任务无法在退出期限内停止时，应用会明确提示数据未确认保存，此时不能声称恢复文件已生成。
