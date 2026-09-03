# 依赖与版本

## 主要应用依赖

| 依赖 | 版本 | 用途 |
| --- | --- | --- |
| .NET Windows Desktop Runtime | 10.0.10 | WPF 自包含运行时 |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM 命令与属性通知 |
| AvalonEdit | 6.3.1.120 | 文本编辑器 |
| LibGit2Sharp | 0.32.0 | Git 仓库操作 |
| LibGit2Sharp.NativeBinaries | 2.0.324 | libgit2 Windows 原生库 |
| Microsoft.Data.Sqlite | 10.0.10 | 操作日志和本地数据 |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.4 | SQLite 原生库绑定 |
| SourceGear.sqlite3 | 3.53.3 | SQLite 原生实现 |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | 依赖注入 |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | 日志抽象 |
| System.Security.Cryptography.ProtectedData | 10.0.0 | Windows DPAPI 草稿加密 |

## 安装程序构建依赖

| 依赖 | 版本 | 用途 |
| --- | --- | --- |
| Inno Setup | 7.1.0 | 生成 Windows x64 安装向导和卸载程序 |
| .NET Framework C# Compiler | 4.8.9232.0 | 构建带 Logo 的 `uninstall.exe` 用户入口 |

安装脚本及重建入口位于 `installer` 目录。当前使用的 Inno Setup 编译器显示为“Non-commercial use only”；商业分发前应确认并取得适用授权。

## 测试依赖

| 依赖 | 版本 |
| --- | --- |
| Microsoft.NET.Test.Sdk | 18.0.1 |
| Microsoft.CodeCoverage | 18.0.1 |
| xUnit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.4 |
| coverlet.collector | 6.0.4 |

## NuGet 还原

中心版本定义位于：

```text
Directory.Packages.props
```

仓库不提交 NuGet 缓存、SDK、运行时或第三方二进制副本。项目本身目前未提供独立的开源许可证文件；向第三方分发源码前，发布者应先明确项目自身的授权条款。
