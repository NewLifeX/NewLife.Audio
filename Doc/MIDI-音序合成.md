# MIDI 音序与合成

> [← 返回文档索引](README.md) | [需求文档](需求文档.md) | [模块清单](模块清单.md)

MIDI 不是采样音频，而是**乐谱 + 控制指令**：`.mid` 只记录“第几个 tick 按下哪个键、力度多少、换什么音色”，必须经过音源才会变成 PCM。本模块位于采样链的**旁路**，负责读取/写入 SMF、把事件流渲染为 PCM，并交给现有 `AudioFile` 写出。

```
.mid ──SMF 解析──→ 事件流 ──tempo map──→ 秒/帧 ──波形合成──→ AudioPcm ──L2/L1──→ WAV/MP3/FLAC
```

## 1. 边界

**做**：

- Standard MIDI File 1.0 读写：Format 0/1/2、PPQ/SMPTE、VLQ、running status、Meta、SysEx。
- 纯托管波形合成：GM 音色分组、ADSR、力度/音量/表情/声像/弯音/延音、复音上限、软限幅。
- 渲染为 16 位 PCM，复用 `AudioFile.WritePcm` 输出 WAV/MP3/FLAC。
- AudioStudio `🎼 MIDI 音序` 页：波形、迷你钢琴卷、节拍网格、播放/定位/另存。
- 控制台 `Samples/MidiDemo`：`info`、`render`、`play`、`gen`。

**不做**：

- SoundFont/DLS 采样音源（后续可选包）。
- 实时 MIDI 输入/输出（WinMM `midiOut`、USB-MIDI、BLE-MIDI）与机械乐器驱动。
- DAW、钢琴卷帘编辑、多轨编辑、VST 宿主。
- MIDI 2.0 / MPE。
- 受版权保护曲目的旋律转录或生成。

**机械乐器说明**：MIDI 本来就是为控制合成器与设备设计的，Note On/Off、力度与时间戳可以直接驱动机械钢琴、电磁铁执行器、机械手指与舞台灯光。本模块生成/解析 `.mid`，外部 MIDI 控制器或机械乐器控制器负责执行；实时 MIDI 输出属于设备驱动层，不在本模块。

## 2. SMF 文件结构

```
MThd（头块：格式、轨道数、division）
MTrk（轨道块）
  delta-time（VLQ）
  event（0x9n Note On / 0x8n Note Off / 0xBn 控制器 / 0xCn 音色 / 0xFF Meta / 0xF0 SysEx）
  ...
  delta-time + 0xFF 0x2F 0x00（End of Track）
```

- **格式 0**：所有事件合并在一条轨道；**格式 1**：多轨同步；**格式 2**：互相独立的序列。
- **division**：PPQ（每四分音符 tick 数）或 SMPTE（帧/秒 + tick/帧）。
- **VLQ**：delta-time 用 7 位一组变长编码，最多 4 字节。
- **Running Status**：连续同类型通道事件可省略状态字节；Meta/SysEx 后清空。
- 主要坑：VLQ、running status、同 tick 事件顺序、tempo map、End of Track、未知 Meta/SysEx 原样保留、旧文件文本编码。

## 3. 核心类型

命名空间 `NewLife.Audio.Midi`。

| 类型 | 职责 |
|---|---|
| `MidiFile` | 头块、格式、division、轨道集合；`Parse/Read/Write/Save/ToArray`、`GetTempoMap`、`GetNotes`、`GetMaxPolyphony` |
| `MidiTrack` | 事件列表、轨道名、`GetNotes()` 配对 Note On/Off |
| `MidiEvent` | 统一事件模型 + `NoteOn/NoteOff/ControlChange/ProgramChange/PitchBend/Tempo/TimeSignature/TrackName/EndOfTrack` 工厂 |
| `MidiTempoMap` | tempo 分段积分、tick↔秒、拍号与小节线 |
| `MidiNote` | 配对后的音符（通道、音高、力度、起止 tick/秒、频率、音名） |
| `MidiSynthesizer` | 渲染为 `AudioPcm` |
| `MidiRenderOptions` | 采样率、声道、主音量、复音上限、释放尾巴 |
| `GeneralMidi` | 128 音色名称、16 组音色→波形/ADSR 映射 |

### 示例

```csharp
var midi = MidiFile.Read("song.mid");

foreach (var note in midi.GetNotes())
    Console.WriteLine($"{note.Name} {note.StartSeconds:n2}s {note.DurationSeconds:n2}s");

var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions
{
    SampleRate = 44100,
    Channels = 2,
    MasterVolume = 0.8,
});

AudioFile.WritePcm("song.wav", pcm);
```

## 4. 解析与写入

- 头块长度允许大于 6，扩展字节跳过；division 高位置 1 时按 SMPTE 解析。
- 轨道按 chunk 长度严格定界，未知 chunk 跳过。
- Note On 力度 0 按 Note Off 处理；同通道同音高的重叠音符按先进先出配对；未闭合音符在轨道结束时截断。
- Meta：tempo(0x51)、拍号(0x58)、轨道名(0x03)、marker/lyric 等；未知 Meta/SysEx 原样保留。
- 文本优先 UTF-8 严格解码，失败回退 Latin-1。
- 写入按 tick 稳定排序，Note Off 先于同 tick 的 Note On；自动补一个 End of Track；`MidiTrack.Name` 无对应 Meta 时自动写入轨道名。
- 读写往返以事件序列与未知数据为准，不要求字节级完全一致（running status 可能被规范化）。
- Format 2 的解析保留，但合成按所有轨道同时播放处理，属有限支持；日常歌曲以 Format 0/1 为主。

## 5. 合成器

- **tick→秒**：tempo map 分段积分；SMPTE 直接按 ticks/second。
- **音高**：`f = 440 × 2^((note-69)/12)`；弯音默认 ±2 半音。
- **波形**：GM 16 组映射到正弦/三角/锯齿/方波/噪声；带限波表按八度缓存，谐波限制在 Nyquist 内。
- **包络**：每族 ADSR（钢琴快起快落、弦乐慢起、铜管中起、打击乐短衰减）。
- **混音**：力度→幅度、CC7 音量、CC11 表情、CC10 声像（等功率）、CC64 延音踏板。
- **复音**：默认 64 声部；超过时丢弃新音符，避免无界增长。
- **鼓通道 9**：底鼓、军鼓、踩镲、吊镲、嗵鼓等按音高映射到噪声/正弦短脉冲。
- **输出**：默认 44.1 kHz / 16-bit / 立体声；软限幅 + 5 ms 首尾淡入淡出，避免削波与爆音。
- **定位**：波形合成是“电子 MIDI”风格，追求零素材、小体积与可复现，不追求 SoundFont 的真实乐器采样品质。

## 6. AudioStudio 波形

`MidiWaveformControl` 的视觉层次：

- 深色纵向渐变背景 + 细网格。
- 对称 min/max 包络，青→蓝→紫横向渐变填充 + 同色柔光描边。
- RMS 响度线（琥珀色）。
- 按 tempo/拍号绘制小节线与拍线。
- 粉色发光播放头 + 时间气泡；悬停十字线与时间提示；点击/拖动定位。
- 顶部 28% 只读迷你钢琴卷，按通道着色，展示多轨与复音结构（不是编辑器）。
- 峰值按控件宽度分桶缓存，播放头 30 fps 更新，不重复扫描 PCM。

## 7. 示例资产与版权

- `AudioStudio/Assets/Midi/` 内置 4 首**公有领域**旋律，由本项目 writer 生成：
  - `ode-to-joy.mid`（贝多芬《欢乐颂》）
  - `greensleeves.mid`（英国民谣《绿袖子》）
  - `canon-in-d.mid`（帕赫贝尔《卡农》）
  - `jasmine-flower.mid`（中国民歌《茉莉花》）
- 每个文件目标 < 20 KB，总计 < 200 KB；编曲由本项目生成，随项目以 MIT 发布。
- 不包含《东京爱情故事》等受版权保护曲目的旋律或转录；用户可读取自己合法获得的 `.mid`。

## 8. 验收

- SMF：VLQ 边界、running status、Format 0/1、Note On 力度 0、未知 Meta/SysEx 透传、EOT 补全、SMPTE、读写往返。
- Tempo map：默认 120 BPM、变速分段、tick↔秒往返、拍号/小节线。
- 合成：A4=440 Hz、时长、Note Off 释放、复音、声像、峰值、空文件静音。
- 资产：4 个内置示例可解析、可渲染、有能量、总大小 < 200 KB。
- 客户端：AudioStudio 启动后加载内置示例，播放、播放头、定位与另存正常。

## 9. 后续

- SoundFont/DLS 采样音源：独立可选包，需解决采样包体积与许可。
- 实时 MIDI 输入/输出：WinMM `midiOut`、USB-MIDI、BLE-MIDI，服务机械乐器与实时演奏。
- MIDI 2.0 / MPE：等设备与生态普及后再评估。
