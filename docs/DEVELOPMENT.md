# 开发指南

GitVisualizer 使用 .NET 10、WPF、LibGit2Sharp 和 SQLite。本文命令均在仓库根目录执行。

## 环境要求

- Windows x64
- .NET SDK 10.0.302（以根目录 global.json 为准）
- Git CLI
- PowerShell 7（推荐用于构建脚本）
- Inno Setup 7（仅生成安装包时需要）

NuGet 依赖从 nuget.org 还原，无需 Phase1 工作目录中的离线依赖或提取 DLL。普通用户运行自包含发布包不需要安装 SDK。

## 构建、测试与运行

~~~powershell
dotnet restore GitVisualizer.slnx
dotnet build GitVisualizer.slnx -c Release --no-restore
dotnet test tests/GitVisualizer.Tests/GitVisualizer.Tests.csproj -c Release --no-build
dotnet run --project src/GitVisualizer.App/GitVisualizer.App.csproj -c Release
~~~

也可以运行：

~~~powershell
./Build-And-Test.ps1
~~~

当前源码与发布文件验证见 [RELEASE-CURRENT.md](RELEASE-CURRENT.md)。

## 生成发布程序

~~~powershell
./Build-CurrentDark.ps1
~~~

生成 Windows x64 自包含单文件程序，默认输出到 artifacts/publish/win-x64。

安装 Inno Setup 7 后执行：

~~~powershell
./installer/Build-Release.ps1
~~~

脚本默认查找当前用户 LocalAppData/Programs/Inno Setup 7/ISCC.exe，发布文件输出到 release/。

现有手动打包脚本会带上 LICENSE、NOTICE、THIRD_PARTY_NOTICES.md 及 docs/licenses 中的许可正文；其他第三方核对事项见 [第三方许可证清单](../THIRD_PARTY_NOTICES.md)。没有安装包自动发布工作流。

## 目录结构

| 目录 | 内容 |
|---|---|
| src/GitVisualizer.App | WPF 应用、界面与资源 |
| src/GitVisualizer.Core | 领域模型与接口 |
| src/GitVisualizer.Infrastructure | Git、持久化、文件系统与凭据实现 |
| tests/GitVisualizer.Tests | 自动化测试 |
| tools/GitVisualizer.Benchmarks | 性能基准 |
| installer | Inno Setup 安装脚本与卸载程序 |
| docs | 设计、安全边界与维护文档 |

App 中部分文件保留源码重建时的命名和生成成员。修改前请区分项目代码与生成/第三方部分，保留已有许可证声明；许可范围说明见第三方清单。

构建产物、依赖缓存、本地设置、数据库、凭据和测试结果不应提交。贡献前检查差异及 git status，确认没有带入本机路径、凭据或用户数据。

[返回项目首页](../README.md)
