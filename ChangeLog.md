# NewLife.Audio 版本更新记录

## v1.3.2026.1024 (2026-10-24)

### 🌐 运行时多语言（新增）
- **31 种语言词表**：接入 NewLife.Core 12 编译期词表机制，运行时消息从 11 种语言扩展到 31 种语言，缺失译文自动回退英文
- **程序集元数据本地化**：`AssemblyTitle` / `Description` 改为英文源文并纳入词表，反射读取后可经 `L.T()` 按当前语言展示
- **带前缀日志**：新增 `InfoPrefixed` / `ErrorPrefixed` / `WritePrefixed`，日志前缀不参与查表，模板可正常翻译
- **NuGet 元数据双语**：`PackageDescription` 采用英文在前、中文在后的形式，便于 nuget.org 同时展示

### 🚀 NativeAOT 与跨平台（新增）
- **NativeAOT 支持**：库全面适配 NativeAOT，AOT 冒烟工程覆盖 19 项检查（含 31 种语言）与多平台矩阵
- **移动端冒烟**：新增 Android / iOS 冒烟工程，Android 支持 JIT / AOT / NativeAOT 三种打包模式并在模拟器自动化验证
- **指令集矩阵**：新增 riscv64 / s390x 冒烟与 CI 校验，CI 扩展到多操作系统 × 多指令集
- **多 RID 单元测试**：win-x64 / linux-x64 / linux-arm64 / loongarch64 等 RID 纳入测试矩阵

### 🐛 Bug 修复
- **[fix]** 修复 FLAC 36 位总采样数符号扩展错误与除零问题
- **[fix]** 修复 RTP 解包器序列号回绕时重组循环死锁
- **[fix]** 修复容器工厂探测魔术字节后未复位流位置，导致原始 PCM 丢首字节
- **[fix]** 修复 Ogg 测试对主机字节序的依赖，使大端平台可正常通过

### 测试与质量
- **测试框架升级**：迁移到 xunit v3 / Microsoft.Testing.Platform，共 315 个单元测试全部通过
- **本地化用例**：新增 31 种语言词条全量覆盖与异常消息本地化用例
- **API 兼容门禁**：统一多 TFM 程序集版本，接入 Microsoft Package Validation 二进制兼容校验
- **XML 注释清零 CS1591**：补全公共 API 文档注释

### 工程与文档
- **解决方案升级**：`.sln` 迁移为 `.slnx` 新格式
- **脚本归位**：构建与校验脚本统一迁移到 `tools/` 目录
- **README 全翻译**：11 种语言 README 与许可证章节同步完善
- **依赖升级**：NewLife.Core 12.0.2026.1024 正式版

---

## v1.2.2026.0802 (2026-08-02)

### 🆕 AudioStudio 客户端（新增）
- **Avalonia 跨平台桌面客户端**：基于 NewLife.Audio 库的音频演示与工具客户端，六大模块 28 项功能全部完成
- **PLAY 播放**：常见格式播放、播放列表（拖拽排序/循环模式）、实时波形显示（自定义 WaveformControl PCM 降采样绘制）
- **CONV 转换**：单文件/批量格式互转、音频参数调整（重采样/位深/声道）、进度与历史记录
- **DEVICE 设备**：设备枚举与详情、测试音发生器、录制测试、后端切换
- **REC 录制**：麦克风录制、系统环路录制、参数配置（采样率/位深/声道）、实时电平表
- **DSP 处理**：实时 FFT 频谱分析（Cooley-Tukey + 多窗函数）、10 段图示均衡器、低通/高通滤镜、变速变调、可视化 DSP 管线编辑器
- **TOOL 工具**：编解码器注册表查看器、PCM 十六进制分析、RTP 包解析、VAD 测试工具

### 编解码引擎优化
- **全面 Span 化**：音频编解码器内存分配优化，热点路径改用 Span 降低 GC 压力
- **编解码重构**：提升性能与可维护性，重构内部状态管理
- **AAC 升级**：引入 AudioSpecificConfig 独立封装，全套测试与兼容改造
- **ADPCM 重构**：状态类重构，修复 WAV 头长度计算
- **健壮性修复**：编解码器与容器接口优化，异常路径与边界处理完善

### 测试与质量
- **核心库 47 项功能 100% 完成**：269 个单元测试全部通过（0 失败/0 跳过）
- **客户端 28 项功能 100% 完成**：实现审计全覆盖修复

### 文档与协作
- **模块编号统一命名体系**：M1~M6 改为助记前缀 CODEC/DEVIO/DSP/VOP/CONT/STRM，新增功能无需重编号
- **新增客户端文档**：需求文档-客户端、功能模块清单-客户端
- **依赖升级**：NewLife.Core 11.18 正式版、Avalonia 12.1.1

---

## v1.1.2026.0702 (2026-07-02)

### 编解码引擎
- **Opus 编解码**：新增纯托管 CELT 实现，支持 Range 解码→能量解码→频谱重建→IMDCT→窗重叠相加
- **Speex 编解码**：新增纯托管窄带 CELP 实现，支持 LSP 解码→激励重建→LPC 合成滤波
- **AAC 编解码**：完善 ICS 解析 + Huffman 解码 + 反量化 + IMDCT + MS 立体声
- **MP3 编解码**：完整实现 Huffman 解码 + 反量化 + IMDCT + 子带合成 + MS 立体声
- **FLAC 编解码**：完整实现 Fixed LPC + LPC 子帧 + Rice 残差编码 + Mid/Side 立体声 + MD5
- **Ogg Vorbis 编解码**：实现 3 头包解析/编码
- **G.722 / G.726 编解码**：完整实现电信语音编解码器
- **编解码工厂重构**：CodecRegistry 注册表 + ICodecInfo 元数据 + 插件化路由

### 容器格式
- **MP4/M4A 容器**：完整 ISO BMFF 实现，支持 ftyp/moov/mdat box 解析与生成
- **Ogg 容器**：完善页解析与写入，支持 Opus/Vorbis
- **FLAC 原生容器**：实现 fLaC 元数据块解析

### 语音处理
- **声学回声消除（AEC）**：纯托管 NLMS 自适应滤波器 + 双讲检测 + 舒适噪声注入
- **噪声抑制（NS）**：频谱减法 + FFT/IFFT + OLA 合成
- **自动增益控制（AGC）**：慢启快放 + RMS 包络跟随
- **语音活动检测（VAD）**：GMM 模型 + 6 子带 + Hangover
- **语音前置管线**：VoicePreprocessor 串联 HPF→VAD→AGC

### 流媒体传输
- **RTSP 客户端**：完整 TCP 协议栈实现，支持 OPTIONS→DESCRIBE→SETUP→PLAY→TEARDOWN，SDP 解析，RTP interleaved 帧接收
- **RTP 封包/解包**：RtpPacket + RtpPacketizer + RtpDepacketizer + RtpSession 会话管理
- **HTTP 音频流**：Icecast/SHOUTcast 拉流支持
- **WebSocket 音频管道**：双向 5 字节帧头管道

### DSP 处理
- **信号链管线**：IAudioProcessor + AudioPipeline 链式处理架构
- **变速变调**：SpeedChanger OLA + PitchShifter 重采样变调
- **动态压缩器**：软拐点 + RMS 包络跟随
- **均衡器**：10 段图示 + 3 段音调，BiQuad 串联

### 芯片头适配
- **大华/宇视芯片头**：扩展 IoT 厂商音频头识别与处理

### 测试与质量
- **单元测试全覆盖**：250 个测试用例，覆盖编解码、容器、DSP、语音、流媒体全部模块
- **音频核心接口升级**：统一 IAudioCodec/IAudioProcessor/IAudioContainer 接口体系

### 文档与协作
- **新增英文 README**：Readme.en.MD 服务国际化客户
- **中英文文档互链**：README 与 Doc 目录全部文档添加中英文导航链接
- **架构设计文档**：CODEC/DEVIO/DSP/VOP/CONT/STRM 六大模块完整架构设计文档

---

## v1.0.2026.0501 (2026-05-01)

- 初始版本，基础音频编解码框架

---
