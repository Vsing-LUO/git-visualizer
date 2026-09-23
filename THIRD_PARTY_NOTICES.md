# 第三方许可证清单

检查范围：当前仓库的 Directory.Packages.props、项目 PackageReference、现有 project.assets.json，以及对应本机 NuGet 包的 .nuspec 和许可证文件。共识别 28 个已解析 NuGet 组件。此清单是当前还原结果的快照，不代替未来发布产物的依赖清单；未重新还原依赖，也未把本机缓存视为项目源码。

Apache-2.0 适用于项目版权人赵泽璇拥有的项目代码；第三方组件及生成器相关内容保留各自许可。没有改写第三方源码或删除依赖。

## NuGet 直接及传递依赖

下表“MIT 保留”指分发组件时保留版权和完整许可正文；“Apache 保留”指保留版权、Apache-2.0 全文和上游提供的适用 NOTICE，修改上游文件时标注修改。测试依赖通常不进入应用，但重新分发测试工具时仍须履行许可义务。

| 组件 / 版本 | 许可证 | 额外保留要求 | 与项目 Apache-2.0 的关系 |
|---|---|---|---|
| AvalonEdit/6.3.1.120 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| CommunityToolkit.Mvvm/8.4.2 | MIT | MIT 保留；包内 ThirdPartyNotices.txt | 未发现直接冲突；组件原许可继续有效 |
| coverlet.collector/6.0.4 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| LibGit2Sharp.NativeBinaries/2.0.324 | GPL-2.0-only + 上游链接例外；内含其他许可 | 完整 libgit2.license.txt、版权及适用源码提供义务 | 链接例外允许独立应用采用其他许可；须专项核对二进制及内含组件 |
| LibGit2Sharp/0.32.0 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Microsoft.CodeCoverage/18.0.1 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Microsoft.Data.Sqlite.Core/10.0.10 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Microsoft.Data.Sqlite/10.0.10 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Microsoft.Extensions.DependencyInjection.Abstractions/10.0.0 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Microsoft.Extensions.DependencyInjection/10.0.0 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Microsoft.Extensions.Logging.Abstractions/10.0.0 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Microsoft.NET.Test.Sdk/18.0.1 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Microsoft.TestPlatform.ObjectModel/18.0.1 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Microsoft.TestPlatform.TestHost/18.0.1 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| Newtonsoft.Json/13.0.3 | MIT | MIT 保留 | 未发现直接冲突；组件原许可继续有效 |
| SourceGear.sqlite3/3.53.3 | Public Domain（包内 LICENSE.txt） | 建议保留上游来源及公有领域声明 | 未发现直接冲突；组件原许可继续有效 |
| SQLitePCLRaw.bundle_e_sqlite3/3.0.4 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| SQLitePCLRaw.config.e_sqlite3/3.0.4 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| SQLitePCLRaw.core/3.0.4 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| SQLitePCLRaw.provider.e_sqlite3/3.0.4 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| xunit.abstractions/2.0.3 | 待核对（旧包仅提供可变 licenseUrl） | 确认 xunit.abstractions 2.0.3 对应版本完整许可 | 未定，不把其他 xUnit 包的许可证直接套用 |
| xunit.analyzers/1.18.0 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| xunit.assert/2.9.3 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| xunit.core/2.9.3 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| xunit.extensibility.core/2.9.3 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| xunit.extensibility.execution/2.9.3 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| xunit.runner.visualstudio/3.1.4 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |
| xunit/2.9.3 | Apache-2.0 | Apache 保留 | 未发现直接冲突；组件原许可继续有效 |

所有明确的 MIT / Apache-2.0 判断来自对应版本包元数据，LibGit2Sharp 和 SourceGear 来自包内正文。核对包内容可使用 https://www.nuget.org/packages/{组件名}/{版本}。清单包含测试工具及其传递依赖；是否实际进入安装包须以发布产物为准。

## libgit2 原生依赖需特别保留的条款

原生包版本为 LibGit2Sharp.NativeBinaries 2.0.324。其完整许可已保存为 [libgit2.license.txt](docs/licenses/LibGit2Sharp.NativeBinaries/2.0.324/libgit2/libgit2.license.txt)，未删减条款。该汇总文件提到的组件不一定全部编入 Windows 产物，需用精确原生构建配置确认，不能仅凭清单推断实际链接情况。

| 组件 | 上游声明 | 保留与兼容性说明 |
|---|---|---|
| libgit2 | GPL v2 only + LINKING EXCEPTION | 例外允许与其他程序组合分发，不要求仅因链接就将本应用整体改成 GPL；库自身修改、单独分发及适用源码义务仍需遵守，不能将库重新标成 Apache-2.0。 |
| zlib | Zlib | 保留来源、声明及修改标识；通常可共存。 |
| PCRE2 | BSD 风格许可，含特定二进制例外 | 保留适用版权、条件、免责声明；不得移除汇总正文。 |
| winhttp 定义文件（Francois Gouget） | LGPL-2.1-or-later | 检查实际编入内容及头文件使用方式，按适用条件保留许可、提供修改源码或重链接安排；不能直接宣称所有分发义务已满足。 |
| SHA1 collision detection、ntlmclient、Team Explorer Everywhere、llhttp | MIT | 保留各自版权和许可。 |
| wildmatch | BSD | 保留版权、条件、免责声明。 |
| OpenSSL 头文件 | 旧 OpenSSL / SSLeay 条款 | 包含署名及宣传相关条件，不能等同 OpenSSL 3 的 Apache-2.0；按实际使用核对兼容性和所需 attribution。 |
| RFC 6234 / Android 派生部分 | BSD 风格条款 | 保留各自版权和免责声明。 |
| LLVM 派生部分 | University of Illinois / NCSA 风格条款 | 保留署名、条件、免责声明，遵守不背书条款。 |
| Unicode 派生部分 | Unicode 自定义声明 | 保留其完整 notice。 |
| sheredom/utf8.h | Unlicense / 公有领域献与 | 保留原声明作为来源记录。 |
| RFC 1320 / RSA MD4 | RSA Data Security 自定义许可 | 保留原文及算法名称 attribution；提及算法或衍生物时遵守命名要求。 |

以上仅报告发现，不删除依赖、不变更上游许可，也不假定链接例外免除所有内含组件的条件。

## 仓库中的生成代码

- src/GitVisualizer.App/CommunityToolkit/Mvvm/ComponentModel/__Internals/ 下两个文件：明确标记 CommunityToolkit 8.4.0.0 生成器生成，未加项目版权头。
- src/GitVisualizer.App/--z__ReadOnlyArray.cs 和 --z__ReadOnlySingleElementList.cs：编译器生成辅助类型，未加头。
- MainWindowViewModel.cs 混有大量 GeneratedCode 成员：整文件保守跳过，不重构或重标生成部分。
- Properties/AssemblyInfo.cs 两个文件：程序集元数据保守排除。

已附上当前 CommunityToolkit.Mvvm 8.4.2 包的 [MIT 许可证](docs/licenses/CommunityToolkit.Mvvm/8.4.2/License.md) 和 [第三方声明](docs/licenses/CommunityToolkit.Mvvm/8.4.2/ThirdPartyNotices.txt)。它们不是旧 8.4.0 生成器输出来源的独立证明；已有重建源码中的第三方/生成部分来源仍应保留记录。LibGit2Sharp 的 [MIT 正文](docs/licenses/LibGit2Sharp/0.32.0/App_Readme/LICENSE.md) 也已保存。其他包的原许可未被改动。

## 构建及运行环境

- 自包含发布还会带入 .NET / Windows Desktop Runtime：主要使用 MIT，并携带 THIRD-PARTY-NOTICES.TXT；实际发布时应保留对应运行时版本的 LICENSE.TXT 和完整第三方声明。本次没有把机器上的全部运行时包都认定为程序依赖。
- Inno Setup：外部安装器构建工具，适用独立 [Inno Setup License](https://github.com/jrsoftware/issrc/blob/main/license.txt)，不属于 Apache-2.0。保留其既有版权与网址；其附带模块需按实际工具版本检查。
- 系统 Git CLI：可选外部程序；调用它不等于复制其源码。本仓库没有将 Git CLI 作为源码依赖。若以后捆绑 Git for Windows，须另行满足 GPL 及其组件分发义务。
- 图片、图标及二进制未添加文件头。仅从仓库无法独立证明每个资源的原始权属，本次依据项目所有者对自有项目的授权，不代替素材来源记录。

## 发布前尚需处理的事项

1. 当前手动打包脚本已将 LICENSE、NOTICE、THIRD_PARTY_NOTICES.md 和 docs/licenses 中的现有完整声明加入安装版与便携 ZIP。清单不代表对所有原生组件分发义务的最终审定。
2. 为实际进入 EXE / ZIP / Setup 的全部组件汇总原许可、NOTICE、attribution，尤其是 libgit2 及运行时；按适用条款落实源码提供方式。
3. 确认 xunit.abstractions 2.0.3 的精确版本许可。该包属于测试链，不因此擅自移除它。
4. 确认生成代码来源与第三方声明覆盖范围。没有发现需直接把项目自有源码整体改为 GPL 的证据，但不能把本次检查作为全部二进制分发已合规的保证。
