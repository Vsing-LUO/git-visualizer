# Git 可视化

一个面向 Windows 的中文 Git 桌面客户端。界面以提交关系图为中心，通过文件拖放、差异块暂存和安全操作预览完成日常 Git 工作流。

## 开发运行

要求 Windows 10/11 x64 和 .NET 10 SDK。

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/GitVisualizer.App
```

应用不依赖系统 Git，也不会自动上传诊断数据。当前源码仓库只使用本地 Git，不配置远程地址。

若当前终端尚未刷新用户 `PATH`，可直接运行本项目安装的 SDK：

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project src/GitVisualizer.App
```

## 发布可直接双击的 Windows 版本

使用自包含发布配置生成 Windows x64 单文件版本，目标电脑不需要另外安装 .NET：

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" publish `
  src/GitVisualizer.App/GitVisualizer.App.csproj `
  -p:PublishProfile=win-x64-self-contained
```

发布完成后直接双击：

```text
artifacts\publish\win-x64\GitVisualizer.exe
```

## V1 功能

- 打开、初始化和克隆仓库
- 工作区状态、文件与差异块暂存
- 提交、分支、标签、stash 和提交关系图
- merge、rebase、cherry-pick、revert、reset
- HTTPS/SSH 远程同步、推送目标选择、远程配置管理和推送过程监视
- 冲突查看与安全恢复点
- 内置轻量文本编辑器和常用文件操作

## 项目结构

- `GitVisualizer.App`：WPF 中文界面、MVVM、提交图、编辑器和对话框
- `GitVisualizer.Core`：领域模型及 Git、差异、恢复、凭据等核心接口
- `GitVisualizer.Infrastructure`：LibGit2Sharp、原生 `git_apply`、SQLite、文件系统、恢复点和 Windows Credential Manager
- `GitVisualizer.Tests`：临时仓库集成测试与文件安全测试

## 本地数据与远程认证

设置、操作历史、恢复点及按天滚动的诊断日志保存在
`%LocalAppData%\GitVisualizer`。诊断日志保留 14 天并过滤 Token、口令、
私钥及 URL 内嵌凭据。

HTTPS 支持用户名和 PAT；凭据保存在 Windows Credential Manager。SSH
通过 Windows SSH Agent 使用已经载入的私钥，并在界面中明确提示用户先将
密钥加入 Agent，不会静默接受未知主机。
