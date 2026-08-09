# THUG2 PC `.snd` — runtime capture runbook

Created 2026-08-07. Everything here is built and self-tested; what remains is
the part that needs real hardware and the game.

## Why a capture

`.snd` (788 files, THUG2 PC only) is a 4-bit IMA-family codec whose predictor
rule is unknown. `tools/research/snd-codec/snd_codec_fit.py` scores candidates against
350 known-plaintext pairs — basenames that ship as both PC `.snd` and Xbox
`.pcm` — but those are *two independent lossy encodes of the same audio*, so
even a perfect decoder could not score 1.0. Best model reaches 0.65.

A capture replaces that with **exact** ground truth, and combined with
chosen-plaintext probes it stops being a search at all:

| | current oracle | with capture |
|---|---|---|
| relationship | two different encodes, correlated | exact input → output |
| input | whatever the shipped audio happens to be | **chosen by us** |
| method | fit and score candidates | read the state machine off directly |

We already know the nibble order, step table and index table are correct
(first differences correlate 0.84–0.87 uniformly). Only the accumulated
predictor diverges. One good capture should settle it.

## What is already built

| File | Purpose | Status |
|---|---|---|
| `tools/research/snd-codec/snd_probe_gen.py` | Writes chosen-plaintext `.snd` probes with corpus-exact headers | done |
| `tools/research/snd-codec/snd_capture.js` | Frida hook: dumps decoded DirectSound buffers, labelled by the `.snd` just opened | done |
| `tools/research/snd-codec/snd_solve.py` | Recovers step table / diff form / leak from (probe, capture) pairs | done, **`--self-test` passes** |
| `tools/research/snd-codec/snd_codec_fit.py` | Scores a finished candidate against the 350 pairs | done |

`snd_solve.py --self-test` runs the whole analysis chain against a synthetic
engine with a deliberately non-textbook rule (leak 0.990, diff shift 3) and
recovers both to within 0.002. So the analysis is trusted *before* any real
capture exists — when one arrives, only the capture itself is unproven.

## Step 0 — try to avoid the VM entirely

An unpacked / no-CD `THUG2.exe` is strictly better than a VM: with the
protection gone the `secdrv.sys` requirement disappears, so it runs natively on
Windows 11, **and** its `.text` is decrypted, so the codec may be readable
statically with no capture at all. (The shipped exe is uniformly encrypted —
0 of 580 4 KB windows in `.text` fall below entropy 7.0 — so nothing can be
read from it as-is.)

You cannot make one without a VM (the loader must run to self-decrypt), but you
may not need to make one:

- [PCGamingWiki's THUG2 page](https://www.pcgamingwiki.com/wiki/Tony_Hawk's_Underground_2)
  documents SafeDisc workarounds for exactly the "owned game will not run on
  modern Windows" case.
- **THUG Pro** is a THUG2-based mod that runs on Windows 10/11 and depends on a
  working THUG2 install, so that community has already solved this.

The THAW build in `Sample/Builds` shows the same pattern: `Disk1/Crack/THAW.exe`
is 21 MB with `.text` entropy 6.6, against the 8 MB SecuROM retail at 7.998.

If you get an unpacked exe, **stop and tell me** — I would then hunt the codec
statically (the same signature approach that located the IMA tables in
`THAW.exe`), which is far less work than a capture.

## Step 0b — static decryption of THUG2.exe: measured and rejected

Asked 2026-08-07 whether the SafeDisc encryption could be broken statically,
avoiding the VM entirely. Measured rather than guessed, and the answer is no.

**No exploitable cipher structure in `.text`:**

| test | result |
|---|---|
| Index of coincidence, equal 2000-byte columns, periods 1-1024 | flat at 0.00391 (= random) for every small period; no keystream spike |
| Per-100 KB region entropy / IoC | 7.997 / 0.00391 in all 8 regions — locally uniform everywhere |
| Repeated 16-byte blocks | 651 of 148,479 (0.44%) |
| Repeated 32-byte blocks | 10 of 74,239 (0.014%) |

`.text` is full of `0xCC` inter-function padding in plaintext, so ECB mode or a
repeating page key would collide heavily. It does not. (The earlier apparent
rise in IoC with period was an artefact of columns spanning larger file regions
as the stride grew; with equal column lengths it vanishes.)

**And there is no readable decryptor to lift.** The hope was that the loader
sections are plaintext — they are not obfuscated to the same degree as `.text`,
but they are not readable code either:

- `stxt774` (entropy 6.25): 43 extractable strings, **all garbage** — another
  obfuscated stage, not code we can read.
- `stxt371` (entropy 5.65): 124 strings, exactly one meaningful — `GetProcAddress`.
- `Secdrv`, `DeviceIoControl`, `\.\`, `DrvMgt`, `00000001.TMP`: **absent from
  the entire file.** Every API and the whole driver interaction is resolved
  dynamically at runtime.

So there is no algorithm sitting in the file to transcribe, and no way to even
locate where the key comes from without executing the loader.

**What "crack it without a VM" would actually mean:** emulating the loader
until it decrypts itself, then dumping — the same trick this repo already used
for the N64 ERZ codec, where a retired Python emulator ran the
ROM's own decompressor under a minimal MIPS interpreter. The x86 equivalent is
Unicorn Engine plus stubbed Windows APIs. Tractable, and it would also reveal
whether the key is disc-derived — but it is a multi-day project against an
actively anti-emulation loader.

**UPDATE 2026-08-07 — the emulator was built, and it works.**
`tools/safedisc/safedisc_emu.py` runs the loader under Unicorn and has
already gone far enough to change what we know. Read its module docstring for
the current state; the headline results:

- The loader **extracts an ~800 KB DLL to a temp file and LoadLibrary's it** —
  *the decryption is not in THUG2.exe at all.* `--dump-temp-files` writes that
  DLL out. Its `.text` entropy is 6.871, i.e. **plaintext code**, and its
  mangled C++ exports name the whole design: `CTransformXor::PerformTransform`,
  `CTransformRandomAccumulate::PerformTransform`, `CKeyBasic::GetKeyData`,
  `CKeyMngr`, `CJumpRun::InstallJumpSystem`, `CModuleMonitor`, `CAltAsc`.
- **This artifact alone may settle the codec question**, and it exists now, with
  no VM, no disc and no driver. Statically reading `PerformTransform` +
  `GetKeyData` is a bounded RE task against readable code, whereas everything in
  Step 0b above was about *unreadable* code.
- Depth so far: 155,910 → 20,747,397 instructions. The loader reads its own
  exe, stages its payloads, loads SecServ, runs its CRT, and reaches the
  code-injection stage — then calls `ExitProcess(1)`, a deliberate refusal
  whose cause is not yet identified.

**The key is very probably NOT disc-derived.** All of the above happened with
**zero `DeviceIoControl` calls**. `secdrv.sys` supplies no key material —
SafeDiscShim answers it with hardcoded constants, which is only possible if
nothing downstream depends on their entropy. The disc is an authentication
gate, not a key source. Still genuinely open: the final hop through AuthServ
(`~efe2.tmp` is created but never written yet). Settle it with a differential
taint test — add a `--fake-secdrv` responder, run twice with different seeds,
diff the `.text`.

So this route is no longer "last resort, multi-day, one-off". It is the active
one, and it needs no acquisitions.

**CLOSED — the THUG1 PC Rosetta.** THUG1 is a generation earlier and may predate
the compression (ZenHAX notes its music containers already carried RIFF headers
where THUG2's did not), so plain-PCM `.snd` there would have settled the codec
outright. The user does not want to acquire THUG1 PC, so this is not a lead.
Do not re-propose it.

**Remaining routes, in cost order:**

1. **LegacyThps Discord** — cited as where the deep Neversoft format knowledge
   lives, not web-searchable, needs no acquisition and no VM. Cheapest by far.
2. **XP VM capture** — the toolchain in this runbook is built and self-tested;
   the VM exists but is currently inconvenient. Note the XP caveat in Step 1:
   modern Frida needs Win7+, so on XP the capture needs a `dsound.dll` proxy
   instead (compile x86 on the host, drop the DLL beside `THUG2.exe`). Ask and
   it gets written.
3. **Unicorn loader-emulation harness** — BUILT and working; see the 2026-08-07 update above. No acquisitions needed.

## Step 1 — VM (only if step 0 fails)

Windows 7 x86, because SafeDisc needs `secdrv.sys` and modern Frida needs
Win7+. Windows XP also runs the game but not current Frida, which would force
the proxy-DLL route instead.

The rip is complete — `SECDRV.SYS`, `DrvMgt.dll` and `00000001.TMP` are all in
the build directory, and it is **SafeDisc 3.20.22** (version fields at the
`BoG_ *90.0&!!` marker are 16-bit shifted: `0x30000` / `0x140000` / `0x160000`).

> `secdrv.sys` is CVE-2007-5587, a privilege-escalation vector, and Microsoft
> blocked it in KB3086255. Install it **inside the VM only**, never on the host,
> and keep the VM off the network.

Install the game from `Setup/`, confirm it launches and plays a sound.

## Step 2 — probes

```
python tools/research/snd-codec/snd_probe_gen.py -o TestOutput/snd-codec/probes/ --seconds 0.25
```

Six probes, each isolating one unknown:

- `ramp-max` / `ramp-min` — max-magnitude nibbles drive the step index to
  saturation in ~11 samples, so consecutive output deltas are a direct readout
  of the step table *and* the diff formula.
- `dither` / `settle` — alternating ±step/8 with a falling index. A pure
  integrator holds its level; a leaky one decays geometrically and the ratio
  **is** the leak. This measures precisely the term the fit says is wrong.
- `sweep` — every nibble value held 16 samples: per-nibble diff magnitude and
  index delta.
- `zero` — catches an asymmetric clamp.

Pick a sound that is easy to trigger repeatedly (a menu click, a bail). Back up
the original, then copy a probe over it:

```
copy "Game\Data\sounds\...\SOMESOUND.snd" SOMESOUND.snd.bak
copy TestOutput\snd-codec\probes\probe_ramp-max.snd "Game\Data\sounds\...\SOMESOUND.snd"
```

## Step 3 — capture

```
pip install frida-tools
mkdir C:\snd_capture
frida -f "C:\...\THUG2.exe" -l tools\research\snd-codec\snd_capture.js
```

Trigger the sound. Buffers land in `C:\snd_capture\` as raw s16le mono, named
after the `.snd` that was opened. Frida attaches by injection rather than as a
debugger, so SafeDisc's `IsDebuggerPresent` checks do not trip.

If `dsound.dll` is not loaded when you attach, wait for the main menu and
attach with `frida -n THUG2.exe` instead.

## Step 4 — solve

```
python tools/research/snd-codec/snd_solve.py \
    --pair ramp-max=TestOutput/snd-codec/probes/probe_ramp-max.snd,C:/snd_capture/SOMESOUND_0.raw \
    --pair settle=TestOutput/snd-codec/probes/probe_settle.snd,C:/snd_capture/SOMESOUND_1.raw
```

First thing to check in the output is `samples per payload byte`. It should be
**2.000**; anything else means the capture is not the decode of that probe
(wrong buffer, resampled, or stereo-doubled) and nothing downstream is
meaningful.

Then implement the recovered rule as a model in `snd_codec_fit.py` and confirm
it clears **0.97** against the 350 real pairs. That is the acceptance gate, and
it is what distinguishes "the probe maths worked" from "we can decode the
shipped audio".

## Step 5 — ship

With a passing model: port it to `Core/Formats/Audio/`, route `.snd` in the
three registration sites the way `.pcm` is (`AudioCommand`, the four
`AudioConverterTabOperations` switches, `FormatProbeAudio`), and replace the
`ProbeSndFile` "not yet decoded" reason. `ThugPcSndSurveyTests` already pins the
format's shape and should keep passing.

## If the capture will not come

The other lead is the **LegacyThps Discord**, repeatedly cited as where the deep
Neversoft format knowledge lives and not web-searchable. Public sources are
exhausted — see the `.snd` entry in `formats-todo.md` for the full list of what
was searched and what was ruled out.
