# lib — 随包分发的 WebView2 SDK

界面跑在 WebView2 里，SDK 就固定在这个目录，构建时不需要联网、不需要 NuGet。
版本：`Microsoft.Web.WebView2 1.0.2903.40`，来自 nuget.org 官方包，未做任何改动。

## 文件说明

| 文件 | 作用 |
| --- | --- |
| `Microsoft.Web.WebView2.Core.dll` | 托管层，CoreWebView2 环境与控制器 |
| `Microsoft.Web.WebView2.WinForms.dll` | WinForms 宿主控件 |
| `runtimes/win-x86/native/WebView2Loader.dll` | 32 位进程用的原生加载器 |
| `runtimes/win-x64/native/WebView2Loader.dll` | 64 位进程用的原生加载器 |
| `runtimes/win-arm64/native/WebView2Loader.dll` | ARM64 原生进程用的加载器 |

## 为什么要放三套加载器

`WebView2Loader.dll` 是原生 DLL，位数必须和当前进程一致，加载错了会直接崩。

主程序编译成 AnyCPU，同一个 exe 在不同机器上进程位数并不相同：

- 64 位 Windows → 64 位进程 → 读 `win-x64`
- 32 位 Windows → 32 位进程 → 读 `win-x86`
- ARM64 Windows → .NET Framework 走 x86 仿真，进程是 32 位 → 读 `win-x86`

WebView2 SDK 会先按当前进程架构去 `runtimes/win-<arch>/native/` 找加载器，
找不到才退回 exe 同目录。所以三套一起放进安装包，安装后不用挑版本，任何架构都能起来。

这也意味着**安装目录必须保留 `runtimes` 子目录结构**，别把它们拍平到 exe 旁边。

## 运行环境要求

目标机器需要装 **WebView2 Runtime**（Win11 自带，Win10 1803 以后多为预装或随 Edge 更新装上）。
缺失时主程序不会崩，会弹一个原生提示窗口引导安装；安装程序也会在装之前先检查一遍。

## 许可证

`WebView2-LICENSE.txt`、`WebView2-NOTICE.txt` 为官方包内原文，随二进制一并保留。
