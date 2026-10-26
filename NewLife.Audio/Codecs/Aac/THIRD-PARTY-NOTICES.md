# Third-Party Notices: AAC Encoder MDCT

`AacMdct.cs` is adapted from [EggEncoder](https://github.com/eggspot/EggEncoder)
(`src/EggEncoder/Transform/Mdct.cs`).

- License: MIT
- Copyright (c) EggEncoder contributors
- Adaptation: removed WMA/MP3-specific dependencies, replaced `BitOperations.Log2`
  with a portable loop for `net461`/`netstandard2.0`, and added direct-sum fallback
  caching for non-power-of-two sizes.

The rest of the AAC encoder (`AacEncoderBackend`, `AacBitWriter`, quantizer,
Huffman mapping and ADTS writer) is original NewLife.Audio code.

The decoder in `SharpJaad/` carries its own notice; see `SharpJaad/THIRD-PARTY-NOTICES.md`.
