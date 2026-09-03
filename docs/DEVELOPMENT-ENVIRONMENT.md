# 运行与开发环境

## 最终运行环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10/11 x64 |
| 发布 RID | `win-x64` |
| 发布形式 | 自包含单文件、不裁剪 |
| .NET Runtime | 已包含在发布程序中 |
| 应用数据 | `%LocalAppData%\GitVisualizer` |

编辑器草稿使用 Windows DPAPI CurrentUser 加密，不应直接复制给其他 Windows 用户账户。

## 开发环境

| 项目 | 冻结值 |
| --- | --- |
| .NET SDK | `10.0.302` |
| 目标框架 | `net10.0-windows` / `net10.0` |
| C# | 14 |
| UI | WPF + 少量 Windows Forms 系统对话框 |
| 处理器目标 | x64 |

`global.json` 锁定 SDK 版本。依赖版本集中定义于 `Directory.Packages.props`，通过 NuGet 还原。

## 构建与测试

```powershell
dotnet restore GitVisualizer.slnx
dotnet build GitVisualizer.slnx --configuration Release --no-restore
dotnet test tests/GitVisualizer.Tests/GitVisualizer.Tests.csproj `
  --configuration Release --no-build
```

WPF 测试需要 Windows STA 线程。构建生成的 `bin`、`obj`、`TestResults` 和 `artifacts` 均已被 `.gitignore` 排除。

## 自包含发布

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-CurrentDark.ps1
```

脚本会重新生成应用图标、从 NuGet 还原依赖，并将 Windows x64 单文件程序输出到 `artifacts/publish/win-x64`。
