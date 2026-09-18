# GitVisualizer v1.3.3

Windows 桌面 Git 可视化工具，使用 .NET 10、WPF、LibGit2Sharp 和 SQLite。
提供提交历史、分支管理、差异对比、工作区编辑、远程操作和恢复功能。

本仓库由当前 v1.3.3 源码整理而来。外层文件夹名称保留 v1.3.2，不代表源码版本。
App 保留当前重建源码的文件命名；项目已统一为标准 src/tests 结构。

## 目录

- src/GitVisualizer.App：WPF 应用、界面、资源与图标。
- src/GitVisualizer.Core：领域模型与接口。
- src/GitVisualizer.Infrastructure：Git、持久化、文件系统与凭据实现。
- tests/GitVisualizer.Tests：自动化测试。
- tools/GitVisualizer.Benchmarks：性能基准。
- installer：Inno Setup 安装包及卸载程序源码。
- docs：异步隔离、文本安全和恢复设计说明。

## 开发环境

Windows x64、.NET SDK 10.0.302、Git CLI。
NuGet 依赖通过 nuget.org 还原，无需原工作目录的离线依赖或 DLL。
运行脚本建议使用 PowerShell 7，并确保 dotnet 和 git 位于 PATH。

在仓库根目录执行：

~~~powershell
dotnet restore GitVisualizer.slnx
dotnet build GitVisualizer.slnx -c Release --no-restore
dotnet test tests/GitVisualizer.Tests/GitVisualizer.Tests.csproj -c Release --no-build
dotnet run --project src/GitVisualizer.App/GitVisualizer.App.csproj -c Release
~~~

也可运行 ./Build-And-Test.ps1 完成测试及所需构建。

## 发布

~~~powershell
./Build-CurrentDark.ps1
~~~

生成 Windows x64 自包含单文件程序，默认目录为 artifacts/publish/win-x64。
用户运行发布版无需安装 .NET。

如需安装包，先安装 Inno Setup 7，再执行：

~~~powershell
./installer/Build-Release.ps1
~~~

默认查找当前用户 LocalAppData/Programs/Inno Setup 7/ISCC.exe。
输出位于 release/，发布文件可上传 GitHub Releases。

## 上传 GitHub

仅提交此源码目录。构建产物、依赖缓存、本地设置、数据库、凭据和测试结果已由 .gitignore 排除。
原有 Git 历史保留；本次整理不自动提交或推送。

本仓库未新增开源许可证；公开发布时可由项目所有者另行选择许可证。
