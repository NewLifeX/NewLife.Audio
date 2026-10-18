# NewLife.Audio - Библиотека аудиокодеков

![GitHub License](https://img.shields.io/github/license/NewLifeX/NewLife.Audio?logo=github)
![NuGet Downloads](https://img.shields.io/nuget/dt/NewLife.Audio?logo=nuget)
![NuGet](https://img.shields.io/nuget/v/NewLife.Audio?logo=nuget)
![NuGet prerelease](https://img.shields.io/nuget/vpre/NewLife.Audio?label=dev%20nuget&logo=nuget)
[![test](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml)
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-blue)
[![contributors](https://img.shields.io/github/contributors/NewLifeX/NewLife.Audio?logo=github)](https://github.com/NewLifeX/NewLife.Audio/graphs/contributors)

<!-- lang:start -->
🌐 [中文](Readme.MD) | [繁體中文](README.zh-TW.md) | [English](README.en.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

> Перевод может отставать от китайской версии.

NewLife.Audio предоставляет кодеки и потоковую обработку для PCM, ADPCM, G.711, G.722, WAV и AAC — для голосовой связи и медиаприложений.

## Установка

```bash
dotnet add package NewLife.Audio
```

## Быстрый старт

```csharp
var codec = new G711Codec();
var pcm = codec.Decode(encoded);
```

## Возможности

- Потоковая обработка и связь в реальном времени
- Кроссплатформенность: Windows, Linux, macOS, ARM64/LoongArch64
- Полностью управляемая реализация без нативных зависимостей

## Документация

> 📖 Полная документация (на китайском): [документы репозитория](Doc)

## Лицензия

MIT License © 2002-2026 Команда NewLife

[GitHub](https://github.com/NewLifeX/NewLife.Audio) | [Website](https://newlifex.com)
