# Third-Party Notices

NewLife.Audio is MIT licensed. It vendors or adapts the following third-party components;
their notices are reproduced here and in the referenced source folders.

## SharpJaad.AAC (AAC / HE-AAC decoder)

`NewLife.Audio/Codecs/Aac/SharpJaad/` is vendored from
[SharpJaad](https://github.com/jimm98y/SharpJaad) commit
`3b2014c177d76ca7ec01d0be56e35f1874e21f4b`.

- Copyright (c) 2023 Lukas Volf
- License: MIT (see `NewLife.Audio/Codecs/Aac/SharpJaad/LICENSE`)
- Upstream: https://github.com/jimm98y/SharpJaad
- Original project: JAAD (Java AAC Decoder), https://sourceforge.net/projects/jaadec/

Local modifications are listed in `NewLife.Audio/Codecs/Aac/SharpJaad/THIRD-PARTY-NOTICES.md`.

## EggEncoder (AAC MDCT transform)

`NewLife.Audio/Codecs/Aac/AacMdct.cs` is adapted from
[EggEncoder](https://github.com/eggspot/EggEncoder) (`src/EggEncoder/Transform/Mdct.cs`).

- License: MIT
- Copyright (c) EggEncoder contributors

Details in `NewLife.Audio/Codecs/Aac/THIRD-PARTY-NOTICES.md`.

## MPEG audio Huffman tables

`NewLife.Audio/Codecs/Mp3/Mp3Huffman.cs` uses the compact big-value Huffman table
representation from [minimp3](https://github.com/lieff/minimp3) (CC0 1.0), which encodes
the ISO/IEC 11172-3 Table B.7 code words.

- License: CC0 1.0 Public Domain
- Upstream: https://github.com/lieff/minimp3

## FFmpeg (optional external process fallback)

`OpusProcessCodec` can invoke a locally installed `ffmpeg` executable for Opus encoding and
decoding when the managed codec is not available. FFmpeg is **not** bundled, linked or
redistributed by this library; users are responsible for their own FFmpeg installation and
license compliance (LGPL/GPL depending on the build).
