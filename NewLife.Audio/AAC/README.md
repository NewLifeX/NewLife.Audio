# libfaac.dll（历史遗留）

本目录的 `libfaac.dll` 是 `FaacEncoder`（已标记 `[Obsolete]`）早期使用的外部原生库副本，
**不被任何 `.csproj` 引用，也不会进入 NuGet 包**。`NewLife.Audio` 的 AAC 编码/解码主链路
为纯托管实现（`AacCodec`），运行时不依赖本文件。

- 计划在下一个主版本随 `FaacEncoder` 一并删除（见 `Doc/模块清单.md` 的「收口记录」）。
- 在此之前请勿在项目中引用或重新分发该二进制；如确需 FAAC 能力，请自行安装并遵守其
  LGPL 许可条款。
