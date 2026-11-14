# 开发说明

## 环境

- Windows x64，.NET 10 SDK；若使用 Visual Studio，安装 .NET 桌面开发组件。
- 项目使用 Nullable Reference Types、Implicit Usings 和中央 NuGet 包版本管理。
- 运行时日志位于 `%LocalAppData%\IndustrialDataAnalyzer\Logs`。请确保当前用户可写。

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

## 当前环境的 NuGet TLS 问题

2026-11-14，在本工作环境中，.NET 的默认 NuGet HTTPS 请求遇到 Windows TLS 凭据错误。仓库提供了仅监听本机回环地址的临时桥接脚本。它通过 Python 标准库从官方 NuGet API 读取包，供当前机器恢复；正常网络环境无需使用。

先在一个终端运行 `python scripts/nuget_proxy.py`，再在另一个终端运行：

```powershell
dotnet restore IndustrialDataAnalyzer.slnx --configfile scripts/NuGet.Local.config --no-http-cache -p:RestorePackagesPath=$PWD\.packages -p:NuGetAudit=false
dotnet build IndustrialDataAnalyzer.slnx -c Debug --no-restore -p:RestorePackagesPath=$PWD\.packages
dotnet test IndustrialDataAnalyzer.slnx -c Debug --no-restore -p:RestorePackagesPath=$PWD\.packages
```

完成后关闭桥接脚本。`.packages` 为本机缓存，已加入 `.gitignore`。

## 开发边界

第一阶段只提供界面和基础能力。不要把占位说明替换成无响应按钮；接入真实操作时应同时提供状态、异常反馈和测试。不要把用户数据写到应用目录，也不要在 UI 线程同步等待异步任务。
