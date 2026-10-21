# NewLife.Audio - Библиотека аудиокодеков

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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

[简体中文](README.zh-CN.md) | Русский

Кроссплатформенная библиотека обработки аудио для .NET на полностью управляемом коде, поддерживающая кодеки, ввод-вывод аудиоустройств, DSP-эффекты, обработку речи, чтение и запись контейнерных форматов, а также потоковую передачу — всё в одном пакете NuGet.

🎧 В комплекте — кроссплатформенный демонстрационный настольный клиент [AudioStudio](AudioStudio/) (Avalonia) с шестью инструментальными модулями: воспроизведение, конвертация, устройства, запись, DSP и диагностика.

Исходный код: https://github.com/NewLifeX/NewLife.Audio  
NuGet: `NewLife.Audio`

> 📖 Подробная документация (на китайском): [Требования](Doc/需求文档.md) | [Список функциональных модулей](Doc/功能模块清单.md) | [Требования к клиенту](Doc/需求文档-客户端.md) | [Список функций клиента](Doc/功能模块清单-客户端.md) | [Анализ конкурентов](Doc/竞品分析报告.md) | [Совместимость с AOT](Doc/AOT兼容性分析.md) | [Локализация](Doc/多语言本地化.md) | [Архитектура модулей](Doc/)

## Возможности

### 🎵 Движок кодеков (CODEC)
Двунаправленное преобразование PCM для более чем 10 аудиокодеков, полностью на управляемом C#:
- **G.711** μ-law / A-law
- **G.722** / **G.726**, субполосный ADPCM
- **IMA ADPCM** (DVI4)
- **Opus** (реализация CELT на полностью управляемом коде)
- **MP3** (декодирование Хаффмана + обратное квантование + IMDCT + субполосный синтез)
- **AAC-LC** (разбор ICS + декодирование Хаффмана + IMDCT + перекрытие-сложение окон)
- **FLAC** (Fixed LPC + кодирование остатков Райса + MD5)
- **Ogg Vorbis** (кодирование и декодирование трёх заголовочных пакетов)
- **Speex** (узкополосный CELP на полностью управляемом коде)
- Подключаемая фабрика через `CodecRegistry` + метаданные `ICodecInfo`
- Адаптация заголовков чипов IoT (Hisilicon / Dahua / Uniview)

### 🔊 Ввод-вывод аудиоустройств (DEVIO)
Кроссплатформенная абстракция аудиоустройств:
- Перечисление и обнаружение устройств
- Бэкенды **WaveOut** / **WASAPI** / **ASIO** в Windows
- Захват аудио и запись loopback
- Одновременное управление несколькими устройствами через `AudioDeviceManager`

### 🎛️ Конвейер DSP (DSP)
Составная цепочка обработки аудиосигнала:
- Передискретизация (линейная интерполяция)
- Преобразование каналов (моно ↔ стерео)
- Преобразование разрядности и формата сэмплов (8/16/32 бита ↔ float)
- Управление громкостью и усиление (линейное / логарифмическое)
- Смешивание нескольких источников с нормализацией
- **FFT**-анализ спектра (Кули-Тьюки, окна Ханна/Хэмминга/Блэкмана)
- **Эквалайзер** (10-полосный графический + 3-полосный тембр, каскад BiQuad)
- **Фильтр BiQuad** (стандарт RBJ: LP/HP/BP/Notch/Peaking/LowShelf/HighShelf)
- Компрессор динамического диапазона (soft-knee, RMS-огибающая)
- Плавное появление/затухание и кроссфейд (линейный/логарифмический/косинусный)
- Изменение скорости (OLA) и сдвиг высоты тона
- Цепочка `IAudioProcessor` + `AudioPipeline`

### 🗣️ Обработка речи (VOP)
Фронтенд голосовой связи в реальном времени:
- **VAD** — обнаружение голосовой активности (модель GMM, 6 субполос + hangover)
- **AEC** — акустическое эхоподавление (адаптивный фильтр NLMS + обнаружение двойного разговора + комфортный шум)
- **AGC** — автоматическая регулировка усиления (медленная атака, быстрое восстановление, RMS-огибающая)
- **NS** — подавление шума (спектральное вычитание + FFT/IFFT + синтез OLA)
- Конвейер предобработки голоса: HPF → VAD → AGC

### 📦 Контейнерные форматы (CONT)
Чтение и запись аудиофайлов:
- **WAV** — полный разбор и запись RIFF
- **Ogg** — чтение и запись на уровне страниц (Opus/Vorbis)
- **MP4/M4A** — ISO BMFF: разбор и генерация боксов ftyp/moov/mdat
- **FLAC** — нативный разбор блоков метаданных fLaC
- **Raw PCM** — автоопределение или ручное задание формата

### 📡 Потоковая передача и транспорт (STRM)
Сетевая потоковая передача аудио:
- Пакетирование и депакетирование **RTP** (RFC 3550) + управление сеансами
- Клиент **RTSP** (TCP: OPTIONS→DESCRIBE→SETUP→PLAY→TEARDOWN, разбор SDP)
- Потоковое аудио по **HTTP** (Icecast/SHOUTcast)
- Двунаправленный аудиоканал **WebSocket** (5-байтовый заголовок кадра)

## Установка

```bash
dotnet add package NewLife.Audio
```

## Быстрый старт

```csharp
using NewLife.Audio;
using NewLife.Audio.Codecs;

// Декодирование G.711 μ-law в PCM
var codec = new G711UCodec();
var pcm = codec.ToPcm(encodedData);

// Кодирование PCM в G.711 A-law
var g711a = new G711ACodec();
var encoded = g711a.FromPcm(pcmData);
```

## Карта экосистемы

NewLife — это многоуровневая экосистема независимо версионируемых проектов с открытым исходным кодом. Приведённая ниже карта дословно используется во всех репозиториях NewLife; каждый проект ссылается на свой репозиторий и свой основной пакет NuGet.

| Проект | Что делает | Загрузки |
| --- | --- | --- |
| **Фундамент** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | Логирование, конфигурация, кэширование, сеть, сериализация, планирование и APM-трассировка. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | Промежуточное ПО для данных: отдельные таблицы масштабом в десятки миллиардов строк, автоматическое секционирование, разделение чтения и записи, более 10 баз данных. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | Высокопроизводительная сетевая библиотека: 1,77 млн пакетов/с и 14,5 Гбит/с на одной машине (TCP 1 КБ, 2026-10), 4 млн долгоживущих TCP-соединений. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | Протокольная связь и RPC-фреймворк для клиент-серверных приложений и IoT-устройств. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | Устанавливает .NET-приложения как демоны ОС: службы Windows и systemd в Linux. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | Шаблоны проектов `dotnet new` для консольных приложений, web, API, служб, данных и IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **Данные и обмен сообщениями** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Клиент Redis с микросекундной задержкой, миллионами операций в секунду и встроенными очередями сообщений. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Клиент RocketMQ на полностью управляемом коде для Apache RocketMQ и Alibaba Cloud Message Queue. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | Клиент и сервер MQTT, включая подключение к Alibaba Cloud IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | Гибридная база данных на C#: встроенная или сетевая, реляционная, временных рядов, очередь сообщений и KV в одном. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | Драйвер MySql и вспомогательный слой на основе стека данных NewLife. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web, AI и распределённые системы** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | Фреймворк быстрого создания админ-платформ: права, SSO, сервер OAuth и CRUD-скаффолдинг. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | Распределённая сервисная платформа: управление узлами, APM, центр конфигурации, реестр и центр публикации. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | Распределённая платформа задач и планирования данных в реальном времени с секционированием, повторами и дозаполнением. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | Единый клиент для 46 поставщиков LLM плюс ChatAI — готовое веб-приложение для чата. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **Промышленность и IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | Стандартная библиотека IoT, определяющая контракты протоколов и абстракции устройств. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | Реализация Modbus TCP/RTU/ASCII поверх NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | Протокол Siemens PLC поверх NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | Протокол Omron PLC поверх NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | Протокол Mitsubishi Melsec PLC поверх NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | Клиент OPC для доступа к промышленным данным. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | Стек протокола автоматизации зданий BACnet. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | Лёгкая платформа IoT с открытым исходным кодом для управления шлюзами и устройствами. | — |
| **Телематика транспорта** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | Протокол позиционирования транспорта JT/T 808: более 70 типов сообщений, 2011/2013/2019. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | Межплатформенный протокол JT/T 809 для органов управления транспортом. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | Автомобильное видео JT/T 1078: разбор RTP, ремультиплексирование в FLV/fMP4 и прямая трансляция. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **Прикладные компоненты** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | Один пакет для Excel, Word, PPT, PDF и более 15 форматов документов. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | Кроссплатформенное аудио: кодеки, ввод-вывод устройств, DSP, контейнеры и потоковая передача. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | Видеокодеки, контейнерные форматы, обработка кадров и потоковая передача. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | Единый интерфейс к Baidu, AMap, Tencent и Tianditu с геокодированием и преобразованием координат. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | Геолокация IPv4 с офлайн-базой данных, отображаемой в память, и онлайн-API в качестве резерва. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **Инструменты и продукты** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | Инструменты разработчика: сеть, последовательный порт, шифрование, регулярные выражения, Modbus и MQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | Самостоятельно размещаемая система управления исходным кодом Git. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | Автоматически устанавливает подходящую среду выполнения .NET при запуске на старом хосте. | — |

> Бейджи показывают основной пакет каждого проекта; пакеты тем, расширений и сателлитов перечислены на соответствующих страницах NuGet. Более 30 пакетов, не перечисленных здесь (FTP, LoRa, расширения Office, драйверы протоколов и другие), доступны в [организации NewLifeX](https://github.com/NewLifeX) и на [newlifex.com](https://newlifex.com).

## Команда

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

Команда NewLife основана в 2002 году; более двух десятилетий она работает в экосистеме .NET и IoT, поддерживает около 100 проектов с открытым исходным кодом (34 из них — ключевые для этой карты) и предлагает комплексные программно-аппаратные решения для отраслей энергетики, логистики, здравоохранения, промышленной автоматизации и интернета. Мы следуем девизу «в учении нет старших — мастерство приносит уважение».

Компоненты с открытым исходным кодом скачали <!--nuget-total:start-->6.48M<!--nuget-total:end--> раз из NuGet (по всей экосистеме, по состоянию на 2026-10).

Сайт: https://newlifex.com · Организация с открытым исходным кодом: https://github.com/NewLifeX

<details>
<summary>Официальный аккаунт в WeChat: 智能大石头 (отсканируйте, чтобы подписаться)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

## Лицензия

MIT License © 2002-2026 Команда NewLife

[GitHub](https://github.com/NewLifeX/NewLife.Audio) | [Сайт](https://newlifex.com)
