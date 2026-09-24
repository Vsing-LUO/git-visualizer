# v2.0.0 发布基线

核对日期：2026-09-24。源码、Windows EXE 与安装程序的版本为 **2.0.0**。

## 本地验证

- .NET SDK 10.0.302 还原成功；Release 编译为 0 警告、0 错误。
- 自动化测试：335 通过，0 失败，0 跳过。
- Windows x64 自包含单文件程序、便携 ZIP 和 Inno Setup 安装程序均已生成。
- 便携 ZIP 包含程序、使用说明、LICENSE、NOTICE、第三方许可证清单及四份上游许可证文本；安装程序也包含这些许可文件。
- 仓库保留手动构建和打包脚本；安装包由项目所有者手动上传到 GitHub Release。

## 本地生成文件的 SHA-256

| 文件 | SHA-256 |
| --- | --- |
| GitVisualizer.exe | `64E3BB967F06E97B92996DE55C6AC9B991233D9AD81AFB87166151FF1B4C61D8` |
| GitVisualizer-v2.0.0-portable.zip | `9A631BE0B980F9E9C147FCCB1163B76F260095B2199748CDA2D81F5BF45317D5` |
| GitVisualizer-v2.0.0-Setup.exe | `BB14AFF7868B1FE41733BE0E22BE8B2198836CFD16F2EE6A35F71BBDC8F20738` |

上述校验值只适用于本次生成的文件。重新构建或重新打包后，应重新计算并公布实际上传文件的 SHA-256。

## 发布边界

`v2.0.0` Tag 对应源码提交；ZIP 和安装程序需另行上传到同版本 GitHub Release。安装程序未进行数字签名，Windows 可能显示 SmartScreen 提示。第三方许可证的已知事项与待确认项目见 [第三方许可证清单](../THIRD_PARTY_NOTICES.md)。
