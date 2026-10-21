# NewLife.Audio - مكتبة ترميز الصوت

![GitHub top language](https://img.shields.io/github/languages/top/newlifex/NewLife.Audio?logo=github)


![GitHub License](https://img.shields.io/github/license/NewLifeX/NewLife.Audio?logo=github)
![NuGet Downloads](https://img.shields.io/nuget/dt/NewLife.Audio?logo=nuget)
![NuGet](https://img.shields.io/nuget/v/NewLife.Audio?logo=nuget)
![NuGet prerelease](https://img.shields.io/nuget/vpre/NewLife.Audio?label=dev%20nuget&logo=nuget)
[![test](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml)
[![NativeAOT](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/aot.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/aot.yml)
<!-- metrics:start -->
[![AOT smoke](https://img.shields.io/badge/AOT%20smoke-19%2F19-brightgreen?logo=dotnet)](Doc/AOT兼容性分析.md)
[![TFM](https://img.shields.io/badge/TFM-3%20%28net461%20%E2%86%92%20netstandard2.1%29-512BD4?logo=dotnet)](https://www.nuget.org/packages/NewLife.Audio)
[![locales](https://img.shields.io/badge/locales-31-2ea44f?logo=dotnet)](Doc/多语言本地化.md)
<!-- metrics:end -->
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-blue)
![arch](https://img.shields.io/badge/arch-x64%20%7C%20x86%20%7C%20arm64%20%7C%20arm%20%7C%20loongarch64%20%7C%20riscv64%20%7C%20s390x-orange)
[![contributors](https://img.shields.io/github/contributors/NewLifeX/NewLife.Audio?logo=github)](https://github.com/NewLifeX/NewLife.Audio/graphs/contributors)

<!-- lang:start -->
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md)
<!-- lang:end -->

[简体中文](README.zh-CN.md) | العربية

مكتبة معالجة صوت .NET متعدّدة المنصات ومُدارة بالكامل، تدعم الترميز وإدخال/إخراج الأجهزة وتأثيرات DSP ومعالجة الكلام وإدخال/إخراج صيغ الحاويات والبث — كل ذلك في حزمة NuGet واحدة.

🎧 تتضمّن عميل عرض سطح مكتب [AudioStudio](AudioStudio/) متعدّد المنصات (Avalonia) بستّ وحدات أدوات: التشغيل، والتحويل، والأجهزة، والتسجيل، وDSP، والتشخيص.

المصدر: https://github.com/NewLifeX/NewLife.Audio  
NuGet: `NewLife.Audio`

> 📖 توثيق مفصّل (بالصينية): [متطلبات النظام](Doc/需求文档.md) | [قائمة وحدات الميزات](Doc/功能模块清单.md) | [متطلبات العميل](Doc/需求文档-客户端.md) | [قائمة ميزات العميل](Doc/功能模块清单-客户端.md) | [تحليل المنافسين](Doc/竞品分析报告.md) | [توافق AOT](Doc/AOT兼容性分析.md) | [تعدّد اللغات](Doc/多语言本地化.md) | [تصاميم معمارية الوحدات](Doc/)

## الميزات

### 🎵 محرّك الترميز (CODEC)
تحويل ثنائي الاتجاه إلى/من PCM لأكثر من 10 مرمّزات صوتية، وجميعها بتنفيذ C# مُدار بالكامل:
- **G.711** μ-law / A-law
- **G.722** / **G.726** ADPCM ضمن النطاقات الفرعية
- **IMA ADPCM** (DVI4)
- **Opus** (تنفيذ CELT مُدار بالكامل)
- **MP3** (فك ترميز Huffman + التكميم العكسي + IMDCT + تركيب النطاقات الفرعية)
- **AAC-LC** (تحليل ICS + فك ترميز Huffman + IMDCT + تداخل النوافذ بالجمع التراكبي)
- **FLAC** (LPC ثابت + ترميز البواقي Rice + MD5)
- **Ogg Vorbis** (ترميز/فك ترميز 3 حزم ترويسة)
- **Speex** (CELP ضيّق النطاق مُدار بالكامل)
- مصنع قابل للتوصيل عبر `CodecRegistry` + بيانات وصفية `ICodecInfo`
- تكييف ترويسات شرائح إنترنت الأشياء (Hisilicon / Dahua / Uniview)

### 🔊 إدخال/إخراج أجهزة الصوت (DEVIO)
تجريد أجهزة الصوت عبر المنصات:
- تعداد الأجهزة واكتشافها
- خلفيات **WaveOut** / **WASAPI** / **ASIO** على Windows
- التقاط الصوت والتسجيل الراجع (loopback)
- إدارة متزامنة لأجهزة متعددة عبر `AudioDeviceManager`

### 🎛️ خط معالجة DSP (DSP)
سلسلة معالجة إشارات صوتية قابلة للتركيب:
- إعادة أخذ العينات (استيفاء خطّي)
- تحويل القنوات (أحادي ↔ ستيريو)
- تحويل عمق البت / صيغة العينة (8/16/32 بت ↔ عائم)
- التحكّم بمستوى الصوت والتضخيم (خطّي / لوغاريتمي)
- خلط متعدّد المصادر مع التطبيع
- تحليل طيف **FFT** (Cooley-Tukey، نوافذ Hann/Hamming/Blackman)
- **مُعادل الصوت** (رسومي بـ 10 نطاقات + نغمة بـ 3 نطاقات، تتابع BiQuad)
- **مرشّح BiQuad** (معيار RBJ: LP/HP/BP/Notch/Peaking/LowShelf/HighShelf)
- ضاغط المدى الديناميكي (soft-knee، مغلّف RMS)
- تلاشي الدخول/الخروج والتلاشي المتقاطع (خطّي/لوغاريتمي/جيب التمام)
- تغيير السرعة (OLA) وتغيير طبقة الصوت (pitch shift)
- قابل للتسلسل `IAudioProcessor` + `AudioPipeline`

### 🗣️ معالجة الكلام (VOP)
واجهة أمامية للاتصال الصوتي في الزمن الحقيقي:
- **VAD** — كشف النشاط الصوتي (نموذج GMM، 6 نطاقات فرعية + فترة تعليق)
- **AEC** — إلغاء الصدى الصوتي (مرشّح تكيّفي NLMS + كشف الكلام المتزامن + ضجيج مريح)
- **AGC** — التحكّم التلقائي بالتضخيم (هجوم بطيء وإطلاق سريع، مغلّف RMS)
- **NS** — كبت الضجيج (طرح طيفي + FFT/IFFT + تركيب OLA)
- خط المعالجة المسبقة للصوت: HPF → VAD → AGC

### 📦 صيغ الحاويات (CONT)
قراءة/كتابة صيغ ملفات الصوت:
- **WAV** — تحليل وكتابة RIFF بالكامل
- **Ogg** — قراءة/كتابة على مستوى الصفحة (Opus/Vorbis)
- **MP4/M4A** — ISO BMFF: تحليل وتوليد صناديق ftyp/moov/mdat
- **FLAC** — تحليل كتل البيانات الوصفية الأصلية fLaC
- **PCM خام** — اكتشاف تلقائي أو تحديد يدوي للصيغة

### 📡 البث والنقل (STRM)
بث الصوت عبر الشبكة:
- تجميع/تفكيك حزم **RTP** (RFC 3550) + إدارة الجلسات
- عميل **RTSP** (TCP: OPTIONS→DESCRIBE→SETUP→PLAY→TEARDOWN، تحليل SDP)
- بث صوت عبر **HTTP** (Icecast/SHOUTcast)
- أنبوب صوت ثنائي الاتجاه عبر **WebSocket** (ترويسة إطار من 5 بايت)

## التثبيت

```bash
dotnet add package NewLife.Audio
```

## البدء السريع

```csharp
using NewLife.Audio;
using NewLife.Audio.Codecs;

// فك ترميز G.711 μ-law إلى PCM
var codec = new G711UCodec();
var pcm = codec.ToPcm(encodedData);

// ترميز PCM إلى G.711 A-law
var g711a = new G711ACodec();
var encoded = g711a.FromPcm(pcmData);
```

## خريطة المنظومة

NewLife منظومة متعدّدة الطبقات من مشاريع مفتوحة المصدر تُصدَر بإصدارات مستقلة. الخريطة أدناه مشتركة حرفيًا بين جميع مستودعات NewLife؛ وكل مشروع يرتبط بمستودعه وبحزمته الرئيسية على NuGet.

| المشروع | الوظيفة | التنزيلات |
| --- | --- | --- |
| **الأساس** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | التسجيل والتهيئة والتخزين المؤقت والشبكات والتسلسل والجدولة وتتبّع أداء APM. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | وسيط بيانات: جداول مفردة بمقياس عشرات المليارات، وتقسيم تلقائي للجداول، وفصل القراءة عن الكتابة، وأكثر من 10 قواعد بيانات. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | مكتبة شبكات عالية الأداء: 1.77 مليون pps / 14.5 Gbps على جهاز واحد (TCP 1KB، 2026-10)، و4 ملايين اتصال TCP طويل الأمد. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | إطار اتصال بروتوكولي وRPC لتطبيقات العميل/الخادم وأجهزة إنترنت الأشياء. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | يثبّت تطبيقات .NET كخدمات حارسة لنظام التشغيل: خدمات Windows وsystemd في Linux. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | قوالب مشاريع `dotnet new` لتطبيقات الطرفية وweb وAPI والخدمات والبيانات وإنترنت الأشياء. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **البيانات والمراسلة** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | عميل Redis بزمن استجابة بالميكروثانية، وملايين العمليات في الثانية، وقوائم رسائل مدمجة. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | عميل RocketMQ مُدار بالكامل لـ Apache RocketMQ وخدمة رسائل Alibaba Cloud. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | عميل وخادم MQTT، بما في ذلك الاتصال بـ Alibaba Cloud IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | قاعدة بيانات هجينة بلغة C#: مدمجة أو شبكية، وعلائقية، وسلاسل زمنية، وقائمة رسائل، وKV في واحد. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | برنامج تشغيل MySql وطبقة مساعدة مبنية على مكدّس بيانات NewLife. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web والذكاء الاصطناعي والأنظمة الموزّعة** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | إطار منصة إدارة سريعة: الصلاحيات، والدخول الموحّد SSO، وخادم OAuth، وتوليد واجهات CRUD. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | منصة خدمات موزّعة: إدارة العقد، وAPM، ومركز الإعدادات، ومركز التسجيل، ومركز النشر. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | منصة موزّعة للمهام وجدولة البيانات الفورية، مع التقسيم وإعادة المحاولة وإعادة التعبئة. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | عميل موحّد لـ 46 مزوّد LLM بالإضافة إلى ChatAI، تطبيق ويب جاهز للمحادثة. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **الصناعة وإنترنت الأشياء** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | مكتبة معايير إنترنت الأشياء التي تعرّف عقود البروتوكولات وتجريدات الأجهزة. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | تنفيذ Modbus TCP/RTU/ASCII فوق NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | بروتوكول Siemens PLC فوق NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | بروتوكول Omron PLC فوق NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | بروتوكول Mitsubishi Melsec PLC فوق NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | عميل OPC للوصول إلى البيانات الصناعية. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | مكدّس بروتوكول BACnet لأتمتة المباني. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | منصة إنترنت أشياء مفتوحة المصدر وخفيفة، مع إدارة البوابات والأجهزة. | — |
| **تليماتيكا المركبات** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | بروتوكول تحديد مواقع المركبات JT/T 808: أكثر من 70 جسم رسالة، 2011/2013/2019. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | بروتوكول JT/T 809 بين المنصات لجهات إدارة النقل. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | فيديو داخل المركبة JT/T 1078: تحليل RTP وإعادة التجميع إلى FLV/fMP4 والبث المباشر. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **مكوّنات المجال** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | حزمة واحدة لـ Excel وWord وPPT وPDF وأكثر من 15 صيغة مستندات. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | صوت عبر المنصات: مرمّزات، وإدخال/إخراج الأجهزة، وDSP، وحاويات، وبث. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | مرمّزات الفيديو وصيغ الحاويات ومعالجة الإطارات والبث. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | واجهة موحّدة فوق Baidu وAMap وTencent وTianditu مع الترميز الجغرافي وتحويل الإحداثيات. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | تحديد الموقع الجغرافي لعناوين IPv4 بقاعدة بيانات غير متصلة مُعيّنة في الذاكرة، وواجهة API عبر الإنترنت كبديل. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **الأدوات والمنتجات** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | صندوق أدوات المطوّر: الشبكات والمنفذ التسلسلي والتشفير والتعبيرات النمطية وModbus وMQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | نظام استضافة ذاتية لإدارة الشيفرة المصدرية Git. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | يثبّت بيئة تشغيل .NET المطابقة تلقائيًا عند التشغيل من مضيف أقدم. | — |

> تعرض الشارات الحزمة الرئيسية لكل مشروع؛ أما حزم السمات والإضافات والحزم التابعة فمذكورة في صفحاتها على NuGet. أما الحزم التي تتجاوز 30 حزمة وغير المذكورة هنا (FTP وLoRa وإضافات Office وبرامج تشغيل البروتوكولات وغيرها) فهي متاحة في [منظمة NewLifeX](https://github.com/NewLifeX) وعلى [newlifex.com](https://newlifex.com).

## الفريق

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

تأسّس فريق NewLife في عام 2002، وأمضى أكثر من عقدين في منظومة .NET وإنترنت الأشياء، ويصون ما يقارب 100 مشروع مفتوح المصدر (34 منها أساسية في هذه الخريطة)، ويقدّم حلولًا متكاملة للبرمجيات والأجهزة لقطاعات الكهرباء والخدمات اللوجستية والرعاية الصحية والتحكّم الصناعي والإنترنت. ونلتزم بمبدأ «لا أسبقية في التعلّم — الإتقان يكسب الاحترام».

نُزّلت المكوّنات مفتوحة المصدر <!--nuget-total:start-->6.48M<!--nuget-total:end--> مرة من NuGet (على مستوى المنظومة بأكملها، حتى 2026-10).

الموقع: https://newlifex.com · منظمة المصادر المفتوحة: https://github.com/NewLifeX

<details>
<summary>حساب WeChat الرسمي: 智能大石头 (امسح الرمز للمتابعة)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

## الترخيص

ترخيص MIT © 2002-2026 فريق NewLife

[GitHub](https://github.com/NewLifeX/NewLife.Audio) | [الموقع](https://newlifex.com)
