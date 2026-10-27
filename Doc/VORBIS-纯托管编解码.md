# Vorbis 纯托管编解码实现

> 版本：v1.0 | 日期：2026-10-26 | 关联功能：CODEC-8a / CODEC-8b | [← 返回模块清单](模块清单.md)

## 1. 结论

Ogg Vorbis 可以在纯托管 C# 中完整实现，不依赖 libvorbis、ffmpeg 或任何原生库。当前实现包含：

- **解码器**：Ogg 容器、Vorbis 三头包、码本、floor0/floor1、residue 0/1/2、声道耦合、长短块切换、IMDCT、重叠相加与 granule 裁剪，覆盖 Vorbis I 的主要码流形态。
- **编码器**：长块基线编码器，输出可被 NVorbis 等第三方解码器正常解码的 Ogg Vorbis 流；支持 0~10 质量档、1~8 声道、8k~192k 采样率。
- **性能**：MDCT/IMDCT 使用 `Vector<Single>` 加速，10 秒 44.1kHz 立体声 q5 在开发机上编码约 0.23s、解码约 0.18s，约 40 倍实时。
- **验证**：解码器对 5 组参考样本达到 85~89 dB SNR；编码器结果与 NVorbis 解码结果一致（>96 dB），自解码 q5 正弦约 31~32 dB、q10 约 44~48 dB。

## 2. 设计目标与边界

### 2.1 目标

| 目标 | 说明 |
|------|------|
| 纯托管 | 不引入第三方音频编解码包、不 P/Invoke 原生编解码库 |
| 规范优先 | 按 Vorbis I 规范与参考算法实现，不依赖特定编码器产物 |
| 流式 | 解码器按 Ogg 包顺序增量解码；编码器按 PCM 块增量输出 |
| 可互操作 | 解码外部 Vorbis 流；编码结果可被第三方解码器读取 |
| 可移植 | net8.0 / netstandard2.1 / netstandard2.0 / net461 |

### 2.2 当前边界

- 编码器只使用**长块**，尚未实现瞬态短块切换。
- 编码器未启用**声道耦合**（mapping coupling steps = 0），立体声按独立声道编码。
- 编码器使用**固定 floor 包络 + 均匀量化**，没有心理声学模型、噪声分配与比特率控制。
- 因此编码质量是“可互操作基线”，不是 libvorbis 的音质对等实现；复杂噪声/瞬态素材在低质量档仍有明显差距。
- 解码器已实现 floor0/floor1、residue 0/1/2 与耦合，可解码常见 libvorbis 产物；极端畸形码流只保证不崩溃并抛出清晰异常。
- 多声道 residue type 2 按规范使用连续交错布局；NVorbis 0.10.5 在“声道数不能整除分区大小”的 3/5/6/7 声道流上存在已知偏移，6 声道互操作以自解码或 libvorbis 为准。

## 3. 代码结构

| 文件 | 职责 |
|------|------|
| `NewLife.Audio/Codecs/VorbisCodec.cs` | 门面：完整 `.ogg` ↔ 16-bit PCM |
| `NewLife.Audio/Codecs/VorbisOptions.cs` | 编码参数与校验 |
| `NewLife.Audio/Codecs/VorbisEncoder.cs` | 编码器：头包、setup、码本、长块分析、floor1、residue2、尾包 |
| `NewLife.Audio/Codecs/Vorbis/VorbisDecoder.cs` | 流式解码器与重叠状态机 |
| `NewLife.Audio/Codecs/Vorbis/VorbisSetup.cs` | identification / comment / setup 解析 |
| `NewLife.Audio/Codecs/Vorbis/VorbisBitReader.cs` | LSB-first 位读取 |
| `NewLife.Audio/Codecs/Vorbis/VorbisBitWriter.cs` | LSB-first 位写入 |
| `NewLife.Audio/Codecs/Vorbis/VorbisCodebook.cs` | 码本解析、stb 风格 Huffman、VQ 查找表 |
| `NewLife.Audio/Codecs/Vorbis/VorbisFloor.cs` | floor 数据模型与基类 |
| `NewLife.Audio/Codecs/Vorbis/VorbisFloor1.cs` | floor1 解析、展开、插值与线性幅度表 |
| `NewLife.Audio/Codecs/Vorbis/VorbisFloor0.cs` | floor0 LSP/Bark 实现 |
| `NewLife.Audio/Codecs/Vorbis/VorbisResidue.cs` | residue 0/1/2 解码 |
| `NewLife.Audio/Codecs/Vorbis/VorbisMapping.cs` | 子映射、声道耦合、floor/residue 流水线、IMDCT 调度 |
| `NewLife.Audio/Codecs/Vorbis/VorbisMode.cs` | 模式、窗口、包内有效区间 |
| `NewLife.Audio/Codecs/Vorbis/VorbisMdct.cs` | IMDCT / 前向 MDCT，`Vector<Single>` 点积 |
| `NewLife.Audio/Containers/OggFileReader.cs` | Ogg 页解析、Vorbis 头包保留、granule |
| `NewLife.Audio/Containers/OggFileWriter.cs` | Ogg 页写入、独立注释/setup 头页、granule |

## 4. 解码流水线

```
Ogg 页 → 包重组 → 三个头包
                     ├─ identification：采样率、声道、块大小
                     ├─ comment：厂商与标签
                     └─ setup：码本、time、floor、residue、mapping、mode
音频包 → 位读取 → 模式/窗口 → 各声道 floor
       → residue（按 submap/partition 解码）
       → 声道耦合逆变换
       → floor 包络乘 residue → IMDCT → 加窗
       → 与上一包重叠相加 → 按 granule 裁剪 → PCM
```

关键点：

- **模式与窗口**：长块 2048、短块 256（可配置）；根据当前/前一/后一块的 blockflag 计算 `startIndex`、`validLength`、`totalLength` 与窗函数。
- **首包**：第一个音频包只建立重叠状态，不输出样本；其第二半块作为下一包的 overlap。
- **重叠状态**：输出 `[startIndex, validLength)` 后，下一包从 `validLength` 继续消费上一包剩余的第二半块，避免重复输出。
- **granule**：`OggFileReader.TotalSamples` 取最后一页 granule，门面按总样本数裁剪尾部 padding。

## 5. 编码流水线

```
PCM → 分块（hop = blockSize/2）→ 滑动长块
    → Vorbis 窗 → 前向 MDCT → 2/N 归一化
    → floor1：取谱峰 → 幅度转 Y → 写两个端点 + 零增量
    → residue2：按 partition 归一化 → 选码本类 → Huffman 写值
    → 位流组包 → Ogg 页 → 文件
```

### 5.1 块序列

- 编码器**不**预置全零 priming 块；第一个音频包就是“第一半为零、第二半为输入首 hop”的块，解码器首包只建立重叠状态。
- 每个输入 hop 生成一个音频包；首个包 `samples=0`，后续包输出 hop 个样本。
- `Flush()` 先处理剩余不足一个 hop 的输入（补零），再追加一个全零尾块驱动最后一个 hop 输出；门面循环调用 `Flush()` 取完所有尾包，避免丢尾。

### 5.2 setup 与码本

编码器固定生成 6 个码本：

| 码本 | 用途 | 条目 | 码长 |
|------|------|------|------|
| 0 | floor1 Y 值 | 256 | 8 |
| 1 | residue 分类 | 4 | 2 |
| 2~5 | residue 值（4 个质量类） | 16 / 64 / 256 / 1024 | 4 / 6 / 8 / 10 |

residue 使用 type 2、begin=0、end=按质量截止频率对齐 32、partition=32、4 个 classification。

## 6. 关键实现决策与踩坑

| 问题 | 结论 |
|------|------|
| Vorbis 位序 | 全流 LSB-first；Huffman 码字按规范生成后需按位反转写入 |
| 均匀码本的编码 | 不能直接写原始索引；必须写 canonical Huffman 码字（均匀码本等价于按位反转的索引） |
| floor1 前两个 post | 直接写 `yBits` 原始值；后续 post 是**预测残差**，不是绝对 Y |
| 恒定 floor 包络 | 前两个 post 写目标 Y，后续 32 个 post 全部写 0，解码端展开后得到恒定包络 |
| residue type 1 | 按码本条目连续读取（entry-major），不是 type 0 的 dimension-major |
| residue type 2 | 声道交错；`interleavedChannels=1` 时按单声道交错布局处理 |
| MDCT 归一化 | 前向 `2 / spectrumSize`，与解码器 IMDCT/折叠/重叠的尺度配套 |
| 解码重叠指针 | 输出后 `previousStart = validLength`，与 NVorbis 的读指针推进一致 |
| 首包对齐 | 不额外插入全零 priming 块，否则整体延迟一个 hop |
| 尾包 | `Flush()` 可能产生多个包，门面必须循环取完 |

## 7. 验证

### 7.1 解码一致性

参考样本位于 `XUnitTest/Assets/vorbis`，由 OggVorbisEncoder 生成音频、NVorbis 解码为 16-bit 参考 PCM：

| 样本 | 采样率 | 声道 | 样本数 | 解码 SNR |
|------|--------|------|--------|----------|
| mono-44k-q5 | 44100 | 1 | 25436 | 89.0 dB |
| stereo-44k-q5 | 44100 | 2 | 25436 | 87.7 dB |
| stereo-48k-q8 | 48000 | 2 | 27776 | 86.6 dB |
| mono-22k-q2 | 22050 | 1 | 12718 | 87.8 dB |
| transient-44k-q5 | 44100 | 2 | 25436 | 85.5 dB |

### 7.2 编码验证

- 自解码与 NVorbis 解码在全部矩阵用例上一致（>96 dB），说明生成码流语义正确。
- 正弦素材：q0 约 20~21 dB、q5 约 31~32 dB、q10 约 44~48 dB。
- 6 声道（分区大小不能被声道数整除）自解码约 32 dB，覆盖 residue type 2 的连续交错布局。
- NVorbis 互操作测试覆盖单/立体声、q5/q10；门面输出长度、采样率、声道数与 granule 一致。
- AOT 冒烟包含真实 Vorbis 编解码往返，20/20 通过。

### 7.3 测试命令

```powershell
dotnet test XUnitTest\XUnitTest.csproj -c Release --filter "FullyQualifiedName~Vorbis"
dotnet run --project AotSmoke\AotSmoke.csproj -c Release
```

## 8. 性能

开发机（Windows x64，.NET 10 JIT）10 秒 44.1kHz 立体声正弦，q5：

| 阶段 | 耗时（预热后） | 实时倍率 |
|------|----------------|----------|
| 编码 | 约 228 ms | 约 44x |
| 解码 | 约 181 ms | 约 55x |

优化手段：

- `VorbisMdct` 的 DCT-IV 点积改为 `Vector<Single>` 分块累加，net8.0 下由 JIT 生成 AVX2/NEON 指令；低版本 TFM 自动退化为标量循环。
- 余弦表按块大小缓存，工作缓冲按实例复用。
- 编码器按 hop 流式处理，不在内存中保留整段 PCM。

## 9. 使用示例

```csharp
using NewLife.Audio.Codecs;

var codec = new VorbisCodec();

// 16-bit 小端交错 PCM → 完整 Ogg Vorbis 文件
var ogg = codec.FromPcm(pcm, new VorbisOptions
{
    SampleRate = 44100,
    Channels = 2,
    Quality = 5,
});

// 完整 Ogg Vorbis 文件 → 16-bit 小端交错 PCM
var decoded = codec.ToPcm(ogg.GetSpan(), null);
```

## 10. 后续

| 项 | 说明 |
|----|------|
| 心理声学模型 | 掩蔽阈值、噪声分配、ATH 曲线，替代当前固定 floor |
| 短块切换 | 瞬态检测与 blockflag 序列，降低 pre-echo |
| 声道耦合 | 立体声 M/S 与耦合步，减少码率 |
| 码率控制 | 目标码率/ABR，而不是固定质量档 |
| FFT MDCT | 用 FFT 替代 O(DCT-IV N²)，进一步降低大块开销 |
| 更多互操作样本 | libvorbis/ffmpeg 生成的多语料测试集 |
