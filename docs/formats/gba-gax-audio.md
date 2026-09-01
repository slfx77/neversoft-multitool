# GBA GAX audio support

The seven Tony Hawk GBA games covered here use Shin'en's GAX Sound Engine.
Neversoft Multitool exposes the ROM audio through two dedicated CLI routes:

```text
NeversoftMultitool gba-audio game.gba --output samples
NeversoftMultitool gba-music game.gba --output songs
```

`gba-audio` extracts both complete sparse wave tables, preserving the one-based
sample slot in names such as `bank_01_sample_023.wav`. GAX 1.x/2.x samples are
signed PCM8; GAX 3.x samples are unsigned PCM8 centred at `0x80`. Because an
individual wave has no playback-rate field, `--rate` is an inspection-rate
override (11025 Hz by default).

`gba-music` follows each generation's song, channel, instrument and wave
pointers and renders the sequenced songs at their GAX hardware rate. GAX 1.99's
rate is call-site state, so THPS2 retains its title/non-title fallback; GAX 2/3
carry the requested rate in the song header.

## Corpus coverage

| Game | GAX | Songs | Populated wave slots | PCM8 |
| --- | --- | ---: | ---: | --- |
| THPS2 | 1.99d | 11 | 34 + 126 | signed |
| THPS3 | 2.11 | 14 | 53 + 48 | signed |
| THPS4 | 3.0 | 10 | 52 + 61 | unsigned |
| THUG | 3.03A | 7 | 55 + 24 | unsigned |
| THUG2 | 3.05 | 6 | 55 + 26 | unsigned |
| American Sk8land | 3.05A | 9 | 55 + 54 | unsigned |
| Downhill Jam | 3.05 | 11 | 57 + 98 | unsigned |

All 68 discovered songs have been rendered end-to-end from the local ROM
corpus and checked for non-silent PCM. Corpus tests also pin the exact song and
bank counts, header generation, representative decoded note counts, PCM
signedness and a one-second renderer-progress sample for every title.

THPS2 has the strongest fidelity evidence: its renderer is byte-exact against
the validated v1.99 reference output and retains the emulator-correlation test
hash. GAX 2/3 use the same sequencer/mixer model with their decoded layouts,
but have not yet been compared byte-for-byte with emulator captures. Treat
their support as structurally validated extraction and conversion, not proof of
sample-exact emulation.

These are dedicated ROM commands rather than routes in the generic `audio`
directory converter or the desktop Audio tab.
