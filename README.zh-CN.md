# OpenUtau 增强分支（HuanMoovo fork）

[English](README.md) · **简体中文**

[![Fork release](https://img.shields.io/github/v/release/HuanMoovo/OpenUtau?style=for-the-badge&label=fork%20release&color=4ea6ea)](https://github.com/HuanMoovo/OpenUtau/releases/tag/v0.1.565-fork.1)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=for-the-badge)](LICENSE.txt)
[![Based on openutau/OpenUtau](https://img.shields.io/badge/based%20on-openutau%2FOpenUtau-6E9FD4?style=for-the-badge)](https://github.com/openutau/OpenUtau)

> **本仓库是 [openutau/OpenUtau](https://github.com/openutau/OpenUtau) 的分支（增强分支），不是上游官方仓库。**
> 在上游 MIT 许可与原有功能之上，做了性能优化、Synthesizer V 风格界面改版、歌手模型图（立绘）自动识别和一批 BUG 修复。

## 本分支是什么

- 是 OpenUtau 的**增强分支**，跟踪**上游 `master`**。本分支 `master` 当前**领先上游 24 个提交**（[对比](https://github.com/HuanMoovo/OpenUtau/compare/13184085f2cee51c174de9ee99163a76ee970e8c...master)）。
- **MIT 许可**，与上游相同 —— 见 `LICENSE.txt`（© StAkira 及 OpenUtau 贡献者）。**未删除任何上游代码**；上游原版 README 完整保留在 [README.md](README.md) 末尾。
- 改动范围：大工程性能、仿 Synthesizer V Studio 的黑灰默认界面、歌手立绘自动识别，以及一批上游跟踪的 BUG 修复。

## 亮点更新

### ⚡ 性能
- **TimeAxis 段查找重写**为关键字数组 + 二分查找：4096 段工程实测提速 **4.8～35.5 倍**，小工程不受影响。
- **并行渲染**（能力开关控制）：内置 Worldline 渲染器多线程渲染乐句，线程数可配置；DiffSinger / Classic 为安全起见保持串行。
- **扁平行主序缓冲**取代 `SynthFeatures`、歌手嵌入曲线与重采样帧路径中的逐帧 NumSharp 运算。
- **钢琴卷帘重绘与对齐线**：绘制过程零几何分配；吸附使用不可变快照，首帧绘制前也正确。
- **渲染 / 音频分配清理**：SDL3 缓冲复用、`MixPlanner` 增量重建、staging 与声库加载缓存、英文 VCCV 音素器二分查找。

### 🎨 界面与交互
- **仿 Synthesizer V Studio 的黑灰默认主题**，柔和蓝色强调色（`#6E9FD4` / `#7FB0E0`），重绘 Logo 并更新全部图标 / icns / png 资源。
- **现代化工作区布局**：单行紧凑工具栏；编排 / 钢琴卷帘 / 歌声 面板标题；右侧歌声检视器含歌手模型图横幅；最右侧 48px 图标条切换声库列 / 钢琴卷帘 / 音符参数面板；底部鼠标手势提示条随按住的功能键高亮。
- **速度标记拖拽**（两条时间轴均可）：吸附预览、合并为一次撤销、按 Esc 或离开标记即取消。
- **可配置全局快捷键**（首选项页）：重复或非法手势会被拒绝，菜单显示已绑定的手势。
- **歌手相关**：歌手对话框新增删除歌手的操作；紧凑歌手选择器；**立绘自动识别**（`character.yaml portrait:` → `portrait.*` → `character.*` → `立绘.*` → 声库自带图标），无需修改 `character.yaml` 即可显示声库模型图。

### 🐞 修复
- 上游跟踪的 BUG 修复：切换歌手或编辑音符后的**残留 PCM**；退化速度值导致的 **NaN `TimeSpan`**；超长音符导致的**音高数组内存溢出**；**多声道波形部件加载失败**；越界 skip / trim **导致播放崩溃**。
- 测试规模：Core **643 → 716**，UI **10 → 43**（UI 套件为无头 Avalonia 测试，驱动真实指针 / 键盘事件并保存截图）。

## 下载与安装

[![Download OpenUtau-fork-win-x64.zip](https://img.shields.io/static/v1?style=for-the-badge&logo=github&label=download&message=v0.1.565-fork.1&labelColor=FF347C&color=4ea6ea)](https://github.com/HuanMoovo/OpenUtau/releases/tag/v0.1.565-fork.1)

最新分支版本：**[v0.1.565-fork.1](https://github.com/HuanMoovo/OpenUtau/releases/tag/v0.1.565-fork.1)** → `OpenUtau-fork-win-x64.zip`（约 168 MB）

1. 从 Release 页**下载** zip，**解压**到任意目录（如 `D:\OpenUtau`）。
2. 双击 **`OpenUtau.exe` 运行** —— 这是 **self-contained Windows x64 构建**：已内置 .NET 运行时，**无需安装任何额外软件**（Windows 10 / 11 x64）。
3. **声库安装**：把每个声库文件夹放入 `OpenUtau.exe` 同目录下的 `Singers` 文件夹（如 `<解压目录>\Singers\<你的声库>`），首次启动会自动创建；重启后在歌手列表中可见。
4. **首次启动默认使用新的黑灰主题**；主题可在首选项（设置）中切换。

本分支目前只发布 Windows x64 版本，其他平台未构建、未验证。

## 从源码构建与测试

需要 **.NET 10 SDK**（目标框架 `net10.0-windows`）：

```bash
git clone https://github.com/HuanMoovo/OpenUtau.git
cd OpenUtau

# 构建应用（Debug 为默认配置，测试也基于它运行）
dotnet build OpenUtau/OpenUtau.csproj

# Core 测试：716 通过，2 跳过（在线测试）
dotnet run --project OpenUtau.Test/OpenUtau.Test.csproj

# 无头 UI 测试：43 通过
dotnet run --project OpenUtau.UiTest/OpenUtau.UiTest.csproj
```

注意：测试使用 xunit v3 / Microsoft.Testing.Platform，`dotnet test --filter` 在此不可用，请直接运行整个项目（runner 参数放在 `--` 之后）。

## 与上游的关系

- 基于 [openutau/OpenUtau](https://github.com/openutau/OpenUtau) 的 `master`；本分支领先 **24 个提交**，**未删除任何上游代码**。
- **MIT 许可**，与上游相同（`LICENSE.txt`，© StAkira 及 OpenUtau 贡献者），本分支的全部改动沿用同一许可发布。
- 与上游同步：

```bash
git clone https://github.com/HuanMoovo/OpenUtau.git
cd OpenUtau
git remote add upstream https://github.com/openutau/OpenUtau.git
git fetch upstream
git merge upstream/master
```

## 致谢

OpenUtau 本体 —— 编辑器、重采样器、音素器、插件系统、翻译 —— 是 **[StAkira](https://github.com/stakira) 与 OpenUtau 贡献者**的成果，本分支只是在其上继续开发。感谢上游项目与社区（[Discord](https://discord.gg/UfpMnqMmEM) · [Wiki](https://github.com/openutau/OpenUtau/wiki) · [中文使用说明](https://opensynth.miraheze.org/wiki/OpenUTAU/%E4%BD%BF%E7%94%A8%E6%96%B9%E6%B3%95)）。本分支改动由 [HuanMoovo](https://github.com/HuanMoovo) 维护。

## 上游功能简介（摘要）

- 现代化操作体验：鼠标 / 滚轮与键盘流畅导航，功能丰富的 MIDI 编辑器（支持导入 VSQX / Vocaloid 4 轨道）。
- 可扩展的实时音素编辑，内置多种音素系统（VCV、CVVC、Arpasing 等）与多语言支持（英、日、中、韩、俄……）。
- 表达式（Expressions）替代 UTAU flags 调音；内置 WORLDLINE-R 重采样器支持曲线调校。
- 国际化 UI 与文件系统编码支持；ENUNU AI 歌手支持；插件系统；兼容多数 UTAU 重采样器；支持 Windows / macOS / Linux。

上游功能的完整原文（英文）与全部徽章、链接均完整保留在 [README.md](README.md) 的原版 README 部分，也可直接访问上游仓库 [openutau/OpenUtau](https://github.com/openutau/OpenUtau)。
