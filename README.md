![GitVisualizer 图标](src/GitVisualizer.App/Assets/GitVisualizerLogo-256.png)

# GitVisualizer

面向中文用户和 Git 初学者的安全型 Windows Git 客户端。

看懂提交与分支关系，在操作前了解风险，为误操作留下恢复依据。

[**下载安装 →**](https://github.com/Vsing-LUO/git-visualizer/releases/latest) · [快速开始](#快速开始) · [功能一览](#功能一览) · [反馈问题](https://github.com/Vsing-LUO/git-visualizer/issues) · [开发指南](docs/DEVELOPMENT.md)

Windows 10 / 11 · x64 · 中文界面 · Apache-2.0

## 让 Git 操作更容易理解

GitVisualizer 将提交历史、分支关系、文件修改和远程操作放在同一个桌面界面中，适合正在学习 Git、使用 AI 编程，或希望通过图形界面管理项目的用户。

- **可视化理解提交、分支和差异**：通过提交关系图、文件差异与提交间对比，看清项目变化，再决定哪些内容进入下一次提交。
- **高风险操作前解释影响并建立恢复依据**：在 Reset、Rebase、丢弃修改等操作中提供风险提示，为受保护的操作创建恢复点，并通过恢复中心和操作记录检查后续状态。
- **本地运行，无需登录 GitVisualizer**：管理本地仓库不需要注册或登录本产品；选择保存的 HTTPS 凭据存入 Windows 凭据管理器。访问私有远程仓库仍需对应平台的认证与权限。

## 下载与安装

当前源码版本：**v2.0.0**。安装包与便携包上传后，请以 [最新 Release](https://github.com/Vsing-LUO/git-visualizer/releases/latest) 为准。

| 下载方式 | 适合谁 | 如何使用 |
|---|---|---|
| [Windows 安装版 · v2.0.0](https://github.com/Vsing-LUO/git-visualizer/releases/download/v2.0.0/GitVisualizer-v2.0.0-Setup.exe) | 日常使用，推荐；上传后可下载 | 运行安装向导，选择目录与快捷方式 |
| [便携 ZIP · v2.0.0](https://github.com/Vsing-LUO/git-visualizer/releases/download/v2.0.0/GitVisualizer-v2.0.0-portable.zip) | 希望解压后直接运行；上传后可下载 | 完整解压后启动 GitVisualizer.exe |
| [全部版本与更新说明](https://github.com/Vsing-LUO/git-visualizer/releases) | 查看版本变化或历史下载 | 阅读对应版本的 Release Notes |

支持 Windows 10 / 11 x64。发布包自带 .NET 运行时，无需另装 .NET；不提供 macOS、Linux 或 ARM64 原生版本。

常规 Git 操作由内置库处理；如需复用系统 Git 的 HTTPS 凭据助手，请安装并配置 Git CLI。访问远程仓库需要网络连接及对应账户权限。

> 安装包如未进行代码签名，Windows 可能显示 SmartScreen 提示。请从本仓库 Releases 下载，并核对文件来源；遇到不确定的安全提示时不要直接忽略。

GitHub 自动生成的 “Source code” 压缩包是源码，不是可直接运行的桌面程序。便携版免安装，但设置与恢复数据默认仍写入当前用户的应用数据目录。

## 快速开始

1. **启动程序**：安装后从快捷方式打开，或将便携 ZIP 完整解压后运行 GitVisualizer.exe。
2. **选择项目**：打开已有本地仓库；也可以初始化新仓库或克隆远程仓库。
3. **检查修改**：在文件树和差异视图中核对内容，将需要提交的文件或区块加入暂存区。
4. **完成提交**：设置 Git 身份，填写提交说明并提交；需要同步时再执行 Fetch、Pull 或 Push。

第一次尝试 Reset、Rebase 或丢弃修改时，建议使用练习仓库，先阅读操作提示。完整的安装、便携版和卸载说明见 [使用说明](installer/使用说明.txt)。

## 功能一览

| 工作场景 | 已有功能 |
|---|---|
| 理解项目历史 | 提交关系图、分支与 Tag、历史文件查看、提交间差异对比 |
| 整理本地修改 | 文件级与区块级暂存/取消暂存、文本编辑、差异查看、提交与 amend |
| 管理分支工作 | 分支创建与切换、Merge、Rebase、Cherry-pick、Revert、Reset、Stash |
| 同步远程仓库 | Clone、Fetch、Pull、Push、远程配置、Force-with-lease 风险确认 |
| 处理冲突与恢复 | 冲突解决、恢复点、恢复中心、操作日志 |
| 日常界面体验 | 中文深色界面、可调整面板、功能区文字缩放 |

## 隐私与本地数据

程序在本地管理仓库，不需要注册 GitVisualizer 账户；远程认证使用你所访问的代码托管平台账户。本地设置、日志、草稿和恢复归档默认位于：

~~~text
%LOCALAPPDATA%\GitVisualizer
~~~

选择保存的 HTTPS 凭据由 Windows 凭据管理器保存。远程操作会连接配置的仓库地址；提交 Issue 时，请先移除日志和截图中的 Token、私人仓库地址及不希望公开的路径。

恢复归档可能包含仓库文件内容，日志可能包含本地路径或仓库信息。请像保护源码一样保护这些本地数据；不要把应用数据目录直接上传到公开仓库或反馈附件中。

## 风险边界与恢复

恢复点用于辅助处理误操作，**不能替代独立备份**。它受空间、数量与保留期限限制；多文件恢复也不是一次不可分割的事务，中断可能留下部分完成状态。遇到失败，请先查看操作阶段和恢复记录，再决定下一步。

卸载安装版会清理当前用户的应用设置、日志、草稿、恢复点及本程序保存的凭据。Git 仓库不会被删除，但卸载前请备份需要保留的应用数据。

实现细节与适用边界：

- [恢复安全说明](docs/RECOVERY-SAFETY.md)
- [文本保存与编码安全](docs/TEXT-SAFETY.md)
- [异步操作与仓库隔离](docs/ASYNC-ISOLATION.md)

## 参与项目

欢迎通过 [Issues](https://github.com/Vsing-LUO/git-visualizer/issues) 提交问题、使用体验和功能建议，也欢迎提交 Pull Request。

报告问题时，请附上程序版本、Windows 版本、复现步骤、预期结果与实际结果；如果方便，可提供脱敏截图或最小复现仓库。涉及凭据泄露等安全问题时，请勿在公开 Issue 中粘贴秘密信息。

准备参与开发？请阅读 [开发指南](docs/DEVELOPMENT.md)，其中包含环境要求、源码构建、测试及打包命令。涉及仓库写入、文件保存或恢复流程的变更，请同时补充对应场景的测试。

## Roadmap · 后续方向

以下为计划方向，尚未全部实现，不代表已经支持或承诺交付日期：

- [ ] 完善新手教程与真实操作演示
- [ ] 建立安装包自动发布流程
- [ ] 完善发布包中的第三方许可声明与校验信息
- [ ] 改善代码签名与安装信任体验
- [ ] 评估 WinGet 与 Microsoft Store 分发

## 开源许可证

本项目采用 Apache License 2.0 开源许可证。

详细许可条款请参阅 [LICENSE](LICENSE) 文件。

Copyright 2026 赵泽璇。项目版权声明见 [NOTICE](NOTICE)；第三方组件保留各自许可证，详见 [第三方许可证清单](THIRD_PARTY_NOTICES.md)。仓库中的许可配置不代表历史 Release 附件已经更新。

## 发布包中的许可证

运行 `./Build-CurrentDark.ps1`，再运行 `./installer/Build-Release.ps1`，即可在 `release` 生成 ZIP 便携包、包含 .NET 运行时的完整安装程序及 SHA256 校验文件。两种包均包含 `LICENSE`、`NOTICE` 和 `THIRD_PARTY_NOTICES.md`；安装向导显示 Apache-2.0 许可证，并将三份文件安装到程序目录。
