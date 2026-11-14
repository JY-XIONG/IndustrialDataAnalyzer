# 架构说明

## 分层

```text
App (WPF / MVVM / Host / Serilog)
  ├── Services ── Core
  ├── Infrastructure ── Core
  └── Core
Tests ── App / Services / Infrastructure / Core
```

现有 Core、Services、Infrastructure、App 四层保持不变。`Core` 定义设备、采集会话、记录及 `IDeviceManager`、`ICommunicationChannel`、`IDeviceRepository`、`IMeasurementRepository`、`ICapturePersistence` 等契约；`Services` 完成设备目录编排、报文解析和批量持久化调度；`Infrastructure` 实现 JSON、SQLite、真实串口和模拟通道；`App` 负责依赖注入、页面、弹窗和 UI 线程调度。页面 CodeBehind 仅初始化 XAML，不包含业务逻辑。

## 设备配置与通信

设备定义包含稳定 ID、名称、唯一编号、类型、备注、串口或模拟参数及解析规则。配置文件为 `%LocalAppData%\IndustrialDataAnalyzer\devices.json`，采用临时文件写入、刷盘后原子替换；旧版保留为 `.bak`，主文件 JSON 无效时会尝试读取备份。第三阶段没有迁移设备配置。

`DeviceManager` 串行化设备增删改、连接和断开操作，阻止重复连接。串口和模拟设备都由 `ICommunicationChannel` 表示，由工厂按类型创建。串口接收循环在后台读取 `SerialPort.BaseStream`，`LineFrameBuffer` 用配置的结束符拼接拆分字节流；模拟通道按采样间隔生成范围内的数值。通道统一发出完整原始报文与连接状态，`FrameParser` 提取一个数值并保留原文或错误。设备切换只改变当前查看对象，不会暗中关闭其他设备。

停止采集、编辑或删除正在连接的设备、应用退出时，管理器先关闭对应通道并释放端口。串口读取异常和端口消失会标记为连接异常；用户可手动重连。通信与解析错误进入日志，页面显示可读状态。解析规则的正则匹配设有 100 ms 超时。

## SQLite 采集存储

`SqliteMeasurementRepository` 把会话和测量记录存入 `%LocalAppData%\IndustrialDataAnalyzer\measurements.db`。时间以 UTC Unix 毫秒整数存储；界面转换到本地时间。`PRAGMA user_version` 管理模式版本，当前为 1；从旧版本升级前用 SQLite 在线备份 API 生成 `.before-v1.bak`。会话表保存设备 ID、设备名快照、起止时间、总数、有效数、失败数、丢失数和状态。记录表保存会话/设备 ID、时间、来源、原文、字段、数值、单位和错误。设备配置与历史记录之间没有级联删除。设备时间、会话时间及全局时间列均有索引。

每次连接先建立数据库会话，再打开通道。通道收到的原始报文分别进入两个独立管道：展示管道最多 4096 条、满时丢弃最旧报文；存储管道最多 4096 条、满时拒收新报文并增加该会话的丢失计数。存储管道以 100 条为一批、等待最多约 100 ms 后写入单个事务。记录 ID 固定，失败后重试使用 `INSERT OR IGNORE`，避免提交结果不确定时重复计数。只有事务成功后才增加“已保存”；数据库故障时保留批次并逐步重试，页面显示错误、待写和丢失数量。SQLite 操作在后台任务执行，不占用串口回调或 UI 线程。

停止设备时先关闭通道，再等待已接受报文落盘并结束会话；正常退出对所有设备执行相同步骤。等待超过 15 秒时向界面报告未落盘数量，可重试停止；退出时写入上限等待后把尚未落盘的报文和丢失计数安全转存为 `pending-capture.jsonl`、`pending-loss.json`，下次启动自动回放。恢复文件回放完成前不会标记为“已保存”。SQLite 故障及恢复文件写入失败会进入日志。异常退出后，启动时把未结束会话标记为 `Interrupted`；进程突然终止时尚在内存、未写入 SQLite 或恢复文件的尾部报文无法保证保留，因此这类会话的最终丢失数可能低估。

数据库使用 WAL；SQLite 自动检查点仍启用。正常退出时执行 `wal_checkpoint(TRUNCATE)` 并通过在线备份 API 生成 `measurements.db.bak`。迁移前另存版本备份。备份和恢复文件位于同一用户数据目录，部署时应一起纳入用户数据备份。若数据库损坏，先保留原库、WAL/SHM 与恢复文件，再从备份恢复，避免覆盖唯一副本。

## 页面与启动

`MainWindowViewModel` 保留八个导航项与单窗口 ContentControl。设备管理页提供弹窗编辑、查询和连接操作；采集页按所选设备显示近期报文、解析结果和存储状态；总览页显示设备状态及今日已保存量；历史页按设备、时间和会话分页查询 SQLite，并显示所选记录的原文与错误。其他页面沿用原有布局。

`App.xaml.cs` 先创建用户数据与日志目录，再配置 Serilog 和 Host；Host 启动后初始化数据库、修复中断会话并加载设备配置，再显示窗口。数据库初始化失败时主界面仍可打开，但启动采集会报告存储不可用；重试时重新初始化。启动早期若日志初始化失败，错误对话框直接显示原因。关闭窗口时异步断开所有设备、处理待写数据，再停止 Host。

## 后续阶段边界

本阶段完成 SQLite 存储和基础历史记录查询。实时曲线、统计分析、报警规则和报表导出仍未接入。设备协议目前是可配置结束符的文本行协议；二进制、Modbus 或设备专用协议需以后按实机协议扩展。
