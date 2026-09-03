# 安装程序源码

`GitVisualizer.iss` 是 Inno Setup 安装脚本，`Build-Installer.ps1` 会使用本地发布产物并调用 Inno Setup 6/7 生成安装包。

安装程序使用 `GitVisualizer.ico`，它与应用源码 `Assets/GitVisualizer.ico` 同源，包含 16、20、24、32、40、48、64、128 和 256 像素图层。

安装后，用户可直接运行安装目录根部的 `uninstall.exe`。该文件使用同一 Logo，并负责启动隐藏目录 `.uninstall` 中由 Inno Setup 管理的内部卸载引擎。开始菜单卸载快捷方式和 Windows 卸载列表图标均指向这个用户入口。保留内部引擎的编号文件名是为了兼容 Inno Setup 的升级日志机制，用户无需直接操作它。

先在仓库根目录生成应用，再构建安装程序：

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-CurrentDark.ps1
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

安装器支持：

- 中文简体和英文向导；
- 选择当前用户或管理员安装模式；
- 自定义安装目录；
- 开始菜单快捷方式；
- 默认勾选的桌面快捷方式；
- 可选的安装后任务栏固定指引；
- 安装完成后可选运行程序；
- 标准卸载入口。

微软要求任务栏固定必须由前台应用中的明确用户交互触发，并明确建议不要由安装器调用固定 API。因此本安装器不会绕过 Windows 或操作注册表强制固定，而是在用户选中该任务后启动程序并给出一次明确指引。

输出位于 `installer/output/GitVisualizer-v1.3.2-Setup.exe`。安装包未进行 Authenticode 签名。若用于商业分发，请确认所用 Inno Setup 版本的授权条款。
