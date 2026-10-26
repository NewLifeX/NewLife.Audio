# Third-Party Notices: SharpJaad.AAC

The files in this folder are vendored from [SharpJaad](https://github.com/jimm98y/SharpJaad),
commit `3b2014c177d76ca7ec01d0be56e35f1874e21f4b`, folder `src/SharpJaad.AAC`.

- Copyright (c) 2023 Lukas Volf
- License: MIT (see [LICENSE](LICENSE))
- Upstream: https://github.com/jimm98y/SharpJaad
- Original: https://sourceforge.net/projects/jaadec/

SharpJaad is a pure-managed C# port of JAAD (an AAC decoder written in Java). It supports
AAC-LC, HE-AAC (SBR), HE-AAC v2 (PS) and channel configurations up to 7.1. The code is
vendored here so that `NewLife.Audio` remains a single strong-named assembly with no
unsigned external references. Please keep this notice and the upstream license when
updating the vendored files.

Local modifications (2026-10-26, for HE-AAC/PS/7.1 correctness):

- `Sbr/HFAdjustment.cs`: replace jagged-array `Array.Copy` into `float[,,]` with row copies (fixes `RankException` on first SBR frame).
- `Syntax/SyntacticElements.cs`: only call `ProcessPS` when PS is signaled/detected; expose `IsPSPresent`; fix 7.1 output to 8 channels.
- `DecoderConfig.cs`: honor ASC SBR/PS signaling, parse the 0x548 PS sync extension and the direct trailing PS flag; ADTS keeps auto-detection.
- `Decoder.cs` / `AACException.cs`: preserve the original exception as inner exception.

See `.editorconfig` in this folder for analyzer configuration.
