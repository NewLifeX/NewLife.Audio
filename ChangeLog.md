# NewLife.Audio 版本更新记录

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
- **架构设计文档**：M1~M6 六大模块完整架构设计文档

---

## v1.0.2026.0501 (2026-05-01)

- 初始版本，基础音频编解码框架

---
