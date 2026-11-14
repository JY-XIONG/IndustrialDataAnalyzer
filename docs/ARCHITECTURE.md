# 架构说明

## 分层与依赖

```text
App (WPF, MVVM, Hosting, Serilog)
 ├─ Services ──────┐
 ├─ Infrastructure ├─ Core (接口)
 └─────────────────┘
Tests → Core / Services / Infrastructure
```

`Core` 只定义当前需要的路径和应用信息接口。`Services` 提供应用信息，不引用 WPF。`Infrastructure` 管理用户数据与日志目录，不依赖安装目录。`App` 负责组合服务、窗口、导航及 XAML 资源，不在页面 CodeBehind 中放业务逻辑。

## 启动和异常

`App.xaml.cs` 先初始化 `%LocalAppData%\IndustrialDataAnalyzer` 和 `Logs`，再配置 Serilog 日滚动文件并构建 Host。Host 注册全部页面 ViewModel、主窗口 ViewModel、路径和应用信息服务；窗口仅由容器创建一次。UI、AppDomain 和未观察到的 Task 异常都会写入日志。UI 致命错误会通知用户后退出。

日志使用 30 天时间限制和 30 个文件数量限制，不记录业务凭据。第一阶段只创建目录和日志，不创建数据库或配置数据文件。

## 导航和页面

`MainWindowViewModel` 保存当前页面、八个导航项及选中状态。导航命令切换单个主窗口内的 ContentControl；`App.xaml` 中的 DataTemplate 将各页面 ViewModel 对应到 UserControl。页面通过同一组资源字典和公共控件显示内容。当前数字均为无设备时的真实零值；未实现区域使用明确空状态。

## 后续扩展

设备协议、采集和报警接口应先在 Core 明确契约，再由 Services 实现业务编排、Infrastructure 实现通信或持久化。当前没有为尚未确定的协议堆放空类。SQLite、ScottPlot 和 ClosedXML 将在相应阶段引入并统一记录到 `Directory.Packages.props`。
