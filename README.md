# GitVisualizer

GitVisualizer 是一个面向 Windows 的中文 Git 桌面客户端。界面以提交关系图为中心，支持文件与差异块暂存、分支与标签管理、远程同步、冲突处理、安全恢复和内置文本编辑。

当前版本：`1.3.2`

## 开发环境

- Windows 10/11 x64
- .NET SDK 10.0.302（由 `global.json` 锁定）
- Git for Windows（仅开发和诊断需要；应用主要使用 LibGit2Sharp）

依赖通过 NuGet 还原，仓库不包含 SDK、运行时、离线包缓存或编译产物。

```powershell
dotnet restore GitVisualizer.slnx
dotnet build GitVisualizer.slnx --configuration Release --no-restore
dotnet test tests/GitVisualizer.Tests/GitVisualizer.Tests.csproj `
  --configuration Release --no-build
```

## 运行与发布

开发运行：

```powershell
dotnet run --project src/GitVisualizer.App/GitVisualizer.App.csproj
```

生成 Windows x64 自包含单文件程序：

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-CurrentDark.ps1
```

输出位置：`artifacts\publish\win-x64\GitVisualizer.exe`。

生成安装程序前请先完成上述发布，然后安装 Inno Setup 6 或 7 并运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

## 项目结构

- `src/GitVisualizer.App`：WPF 界面、视图模型、对话框和编辑器交互
- `src/GitVisualizer.Core`：领域模型与核心接口
- `src/GitVisualizer.Infrastructure`：Git、SQLite、文件系统、恢复和凭据实现
- `tests/GitVisualizer.Tests`：集成测试、WPF 测试和安全工作流测试
- `installer`：Inno Setup 安装脚本及卸载入口源码
- `docs`：依赖、开发环境和版本说明

v1.3.2 的 App 层源码来自最终验证版本的源码重建快照；Core、Infrastructure 和测试工程来自同一冻结版本。仓库保留此前版本的提交历史和标签。

## 本地数据与安全

设置、操作历史、恢复点及按天滚动的诊断日志保存在 `%LocalAppData%\GitVisualizer`。HTTPS 凭据保存在 Windows Credential Manager；SSH 使用 Windows SSH Agent。应用不会自动上传诊断数据。

## 许可

本仓库当前未附开源许可证。除非版权所有者另行授权，公开可见不等于授予复制、修改或再分发权利。
