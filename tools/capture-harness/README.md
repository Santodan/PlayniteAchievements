# Capture harness

Diagnostic tools for the unlock notification: the clip pipeline (recording, clip export, toast
compositing, unlock screenshots) and the on-screen slide.

The pipeline is hard to reason about from the outside because a clip looks plausible while being wrong.
Frames can be duplicated, reordered, or shifted wholesale in time, and none of that is visible by eye
against unfamiliar game footage.
These tools remove the guesswork by recording a window whose every frame states which frame it is, then
reading those statements back out of the finished artifacts.

Everything here is standalone: nothing in `source/` depends on it, and it is compiled on demand rather
than as part of the build.

## Building

```powershell
tools\capture-harness\build.ps1
```

Compiles each tool with Roslyn against .NET Framework 4.6.2 and the SharpDX assemblies from
`source\bin\Debug`, so **build the plugin first**.
Executables land in `tools\capture-harness\bin\` and are git-ignored.
`CaptureHarness.exe` is built x86 because it loads the plugin into Playnite's 32-bit process model;
the standalone artifact-analysis tools remain x64.

Needs Visual Studio 2022 or the Build Tools (the script finds Roslyn itself, whatever the edition) and the
.NET Framework 4.6.2 targeting pack.

### Sharing these

Only `CaptureHarness` needs the plugin: it drives the recorder, exporter and re-encoder by reflection, so
it wants a built `source\bin\Debug` and is really a developer tool.
`SlideProbe` stands alone unless `--dict` is passed, which loads the plugin's resource graph (and finds
`Playnite.SDK.dll` in `source\packages`, since it is not copied to the plugin's output).
The rest stand alone and can be handed to anyone on Windows:

- `Show-Mp4Timeline.ps1` has no dependencies at all — pure PowerShell over the MP4 boxes.
- `FrameDump`, `AttributeBisect`, `PacerProbe` and `GenerationLoss` need only the `SharpDX*.dll` files that
  `build.ps1` copies next to them, so the `bin\` folder works as a self-contained bundle.
- `Match-ShotToClip.ps1` needs `FrameDump.exe` beside it.

## The main harness

```powershell
tools\capture-harness\bin\CaptureHarness.exe <seconds> <fps> [pluginDir] [freezeAt] [freezeFor]
```

Opens a window that paints a frame counter as both a human-readable number and a 16-bit binary barcode,
records it with the real `WgcVideoRecorder`, then drives the real exporter and overlay re-encoder over the
result and reads the barcodes back.

What it reports, and why each check exists:

| Check | Catches |
|---|---|
| Segment frames, `stts` entries, media length vs the wall-clock gap between file names | Capture pacing that does not match real time |
| Frame order across the clip | Duplicated, stale, or reordered frames |
| **Absolute alignment** — does output time *t* show the frame that was on screen at `clipStart + t` | A whole-clip time shift, which every relative check passes happily |
| **Short-buffer case** — a window reaching further back than the buffer holds | Positions measured from the requested window rather than from where the clip actually begins |
| Screenshot alignment — a live grab's frame vs when the grab happened | How current a live screenshot can be, and that its path involves no mapping |
| Paint intervals during recording and compositing | Whether the pipeline stutters the application being captured |
| Encoder duration handling | Per-sample durations being flattened onto a fixed grid |
| **Spliced vs whole-clip re-encode** — same base clip through both passes, frame identities compared one to one, card presence read back per frame | A splice point that shifts, drops or duplicates frames, or lands the card on the wrong frames |
| Audio through the overlay pass — a synthetic 440 Hz loopback track and a 1 kHz composited chime, read back by Goertzel | The remux losing the track, shortening it, or mixing the chime at the wrong time (both AAC passthrough and PCM re-encode modes) |

`freezeAt`/`freezeFor` stop the window painting mid-recording, which is what a game that stops presenting
looks like to the capture. Wired but not yet exercised.

Per-frame offsets are written to `alignment_*.csv` next to the executable.

The composition phase runs the overlay re-encoder three times over the same base clip: spliced (the
default, `MediaFoundationOverlayReencoder.SpliceEnabled = true`) and whole-clip with a production-shaped
card at the end of the clip and a chime mixed into the audio, then a card two seconds in with no chime.
The spliced output must decode to the same source frame at the same output time as the whole-clip one,
carry the card on exactly the frames the window covers, and keep its audio; the last case makes the plan
copy after the card instead of before it and takes the AAC passthrough path. Each pass prints the plugin's
own `Toast splice` lines (runs copied and re-encoded, per-run reader/sink/frames/finalize cost) so the
saving is measured, not inferred. The harness records no real audio, so `ExportClip` writes one synthetic
`aud_*.wav` chunk per video segment, named and timed like the audio recorder's, before exporting.

The avcC check after it is what makes the splice possible at all: copied and re-encoded GOPs can share one
track only if the two encoders emit byte-identical parameter sets. Measured on the NVIDIA MFT they do, at
the higher re-encode bitrate too, but only when the re-encode declares the capture's frame rate: the rate
is written into the SPS timing fields, and a base clip whose capture stalled averages below it (56 fps for
a 60 fps capture, in one run), so deriving the rate from the clip produced a different SPS. The plugin
declares the captured rate for re-encoded runs and compares sequence headers before it splices.

### Re-running the composition over an existing clip

```powershell
tools\capture-harness\bin\CaptureHarness.exe --reencode <clip.mp4> <fps> [toastStartSeconds] [trimLeadSeconds] [pluginDir]
```

Runs only the composition phase (the three passes above and their checks) over a base clip the full run
left behind, so a clip that exposed something — one with a capture stall, say — can be worked on without
recording again. It first prints a compressed-vs-decoded timestamp comparison under each reader mode.
That comparison is what found that the source reader's *advanced* video processing includes frame-rate
conversion: it re-times every decoded frame onto the type's declared frame rate, an average the MP4
source derives from the file, so on a stalled clip decoded timestamps drifted from the compressed ones
by the whole stall (404 ms measured) while *basic* processing and the decoder's own NV12 output kept
them exact. The plugin and this harness now decode NV12 with no processing attribute; barcodes are read
from the luma plane and the card from the chroma plane.

The same investigation moved the plugin's compositing off RGB altogether: frames stay in NV12 from the
decoder through the in-place card blend to the encoder, which removed both colour converters from the
pass, and the D3D device manager was dropped from the encoding sink because the NVIDIA transform
rejects system-memory NV12 samples while one is bound (`E_INVALIDARG` on the first write) yet is
selected as the hardware encoder without it. Two more things had to be true for the NV12 path to be
correct, both found by comparing dumped frames against the base clip: each decoded frame is copied out of
the decoder before it is written, because the decoder reuses its output buffers while the encoding sink
still holds the queued sample (luma and chroma from different frames otherwise), and each copy is
repacked from the decoder's own pitch and macroblock-aligned height (1088 rows for 1080p at 1080p) to
the packed frame, because that padding puts the chroma plane below where the encoder and the compositor
address it (every picture's chroma sat 16 rows below its luma while the card, blended by the same
assumption, looked right). Media Foundation's own contiguous copy does not solve this: it packs rows to
the frame width but keeps the aligned height, which `GetContiguousLength` reporting 3133440 rather than
3110400 is exactly what says. The aligned height is derived from that number instead of assumed, so a
vendor's choice of pitch or alignment is never guessed at. With both in place the composited frames
match the base clip at 71-74 dB PSNR outside the card, where the RGB path managed 53 dB, and the card
region matches the old path within codec noise (53-57 dB).

`--software` on the `--reencode` line keeps hardware transforms off the encoding sinks, so the passes run
on Microsoft's software H.264 encoder — the path any machine without a usable vendor transform takes, and
the one the plugin falls back to by itself when a hardware sink cannot be set up. Its parameter sets
differ from the capture's, so this also exercises the splice detecting the mismatch and falling back to
the whole-clip pass. It composites at the same rate and reads 60 dB against the base clip (the encoder is
simply noisier), so the fall-back costs encode speed, never the card.

Only the NVIDIA encoder was available here. What stands behind AMD and Intel is that fall-back and the
existing one below it: a sink that cannot be created, a transform that refuses the frames, or parameter
sets that do not match all end in a working clip — with the software encoder, without the splice, or in
the last resort without the card, never with a corrupt one.

The parameter-set check costs nothing to reach that verdict. The encoder publishes its sequence header
on the sink's own transform as soon as writing begins, so the plugin compares it there, 1-2 ms after the
sink exists and before a single frame is encoded; a mismatch abandons the run having done no work. The
comparison is per NAL unit rather than over the raw blob, because the same encoder reports four-byte
start codes on its output type and three-byte ones in the file it writes, and comparing the blobs whole
calls that a mismatch. Decoding the two signatures with `Decode-Sps.ps1`-style field parsing showed the
NVIDIA capture and the Microsoft software encoder differ in exactly three places: `max_num_ref_frames`
(1 vs 2), the VUI colour description (present vs absent) and the timing tick ratio (1000/120000 vs 1/120,
both 60 fps). Only the first is settable through the codec API, so making two different encoders sign
alike is not achievable, and the pass detects rather than pursues it.

### Native lifetime/page-heap stress

```powershell
tools\capture-harness\bin\CaptureHarness.exe --stress <seconds> <fps> [pluginDir] [exportEverySeconds] [teardownCycles] [width] [height]
```

Stress mode keeps the real recorder active while it repeatedly exports finalized MP4 segments and
overlay-reencodes those exports. It then disposes a live recorder and repeats short teardown cycles at
the next-writer preparation boundary. This exercises the Media Foundation, D3D11 and WGC lifetime
overlaps that a normal correctness run does not.

For a guarded native-heap run, install **Debugging Tools for Windows** from the Windows SDK and launch
an elevated PowerShell:

```powershell
tools\capture-harness\Invoke-PageHeapSoak.ps1 -Seconds 600
```

The script builds the x86 harness, enables full page heap only for `CaptureHarness.exe`, runs it under
the x86 CDB debugger, writes a log under `artifacts\pageheap`, and disables page heap in a `finally`
block. Its 1280x720 default is deliberate: full page heap expands every native allocation and a 32-bit
1080p re-encode can exhaust virtual address space before it reaches a guard-page fault. Pass
`-Width 1920 -Height 1080` for a shorter crash-resolution run when address-space pressure permits.

Do not enable page heap for `Playnite.DesktopApp.exe`; this harness isolates the plugin's native
capture paths without destabilizing the user's Playnite installation.

If `--stress` reports `0x80070424` because the interactive user's per-user `CaptureService` is stopped,
the focused fallback still covers D3D11 texture encoding plus overlapping MF export/re-encode and the
shared runtime lease:

```powershell
tools\capture-harness\Invoke-PageHeapSoak.ps1 -MediaFoundationOnly -Seconds 300
```

That fallback cannot validate the WGC pump/session teardown; rerun the primary mode after signing back
into an interactive Windows session so `CaptureService` is available.

### The short-buffer case matters most

It is the regression test for a defect that shipped: `SegmentTimeline.PlanClip` begins a clip at the later
of the requested window start and the oldest segment it can use, so a young session or a pruned buffer
makes the clip start after the window did.
Positions inside the clip must be measured from where the clip actually begins.
Measuring from the window put the composited toast card early by the shortfall — seconds, in practice.

Note what the harness reports in that case: the footage is still perfectly aligned *to the clip's own
start*.
The mapping was never broken; only the reference point was wrong.
That is why every earlier run of this harness passed while the bug was live, and why the short-buffer case
had to be added explicitly.

## The slide probe

```powershell
tools\capture-harness\bin\SlideProbe.exe [--load <ms>] [--duration <ms>] [--repeats <n>] [--dict <pluginDir>]
```

Measures the notification's slide-in the way the plugin performs it, and answers whether the motion got
the frames it needed.

The slide is not a WPF animation. `ToastNotificationService.RunPhysicalSlide` hooks
`CompositionTarget.Rendering` and, per composed frame, reads that frame's composition timestamp, eases
the elapsed fraction and moves the HWND with `SetWindowPos`. So it is duration-correct by construction
and can fail only one way: by running out of frames. A late second frame means the eased clock has
already advanced, and the card jumps rather than slides — while still finishing in exactly 240 ms, which
is why a stopwatch and the naked eye both miss it.

The probe replicates that interpolation and the `RenderTickCounter` timestamp handling exactly, on a real
per-pixel-alpha window, and runs three orderings side by side:

| Mode | Ordering |
|---|---|
| `None` | Slide on the same UI-thread turn as `Show` — the ordering before the fix. The **control**; it is expected to jump under `--load`, and is not judged. |
| `Transparent` | Wait for two composed frames at `Opacity=0`, then slide — what the plugin does now. |
| `NearTransparent` | The same at `Opacity=1/255`, which defeats any `Opacity==0` culling. |

`--load <ms>` arms a one-shot cost consumed by the window's **first composed frame**, wherever that
lands: in the warm phase when warming, otherwise on the slide's own first frame. That is what makes the
defect deterministic instead of dependent on a cold process, and it is what the control contrast checks.
`--dict` additionally times the storyboard resolve the slides used to do inline.

### Reading it

`firstX` is the verdict: the gap **after the slide's first frame**, as a multiple of that run's own
median. All ratios are against the run's own median rather than the display's rate, because moving a
per-pixel-alpha window costs a full redirection-surface blit — the slide sustains an even ~82 Hz on a
165 Hz panel and cannot do better. Uniform coarseness reads as smooth; the specific thing that reads as a
jump is the frame *after the first* arriving late.

Measured over six control and twelve warmed runs, `firstX` separates with an order of magnitude of margin
either side of the 3x threshold: the unwarmed control runs **4.3–11.2**, a warmed slide **0.0–0.5**.

`worstX` (the worst gap anywhere) is only a loose stall backstop at 8x. It deliberately is *not* the
verdict: a warmed run on a machine that is merely busy reaches 4x, which overlaps the control's low end,
so judging on it cries wolf about one run in ten. `maxStep` is a gap's visible cost in pixels — context,
not a threshold, since `BackEase` is steep early and an identical gap costs far more travel at the start
of the slide than at the end.

Four earlier versions of this probe were wrong in ways worth not repeating:

- Injecting the cost into the **slide's** first frame rather than the **window's** penalised every mode
  equally, so no warm ordering could ever win.
- Deriving the "ideal" step from the run's own observed mean interval is circular: it hands a starved
  slide a lenient target, so a three-frame slide passes.
- Sampling `CompositionTarget.Rendering` while idle to measure the display rate reports the rate the
  sampler asked for frames, not the display's — 31.8 Hz on a 165 Hz panel, inflating every target twofold.
  It comes from `EnumDisplaySettings` now.
- Waiting for warm frames with `Dispatcher.PushFrame` validated the *idea* of warming but not the code
  that does it, and once the warm became async the nested pump deadlocked against its continuation. The
  whole probe is await-based now, mirroring how the plugin sequences it.

### What it established

- A window at `Opacity=0` **does** rasterize its content: the transparent warm absorbs the whole injected
  first-paint cost (first gap ~121 ms → under 0.3 ms), so the near-transparent variant is unnecessary.
- The warm wait is satisfied by frames arriving, not by its timeout — **6–25 ms** on a fully static
  `Opacity=0` window with nothing animating, because subscribing to `Rendering` itself keeps the render
  loop ticking. So it adds no notification latency, and its 150 ms ceiling should *not* be shortened
  toward a frame period: the ceiling's other role is bounding how much first paint the warm can absorb.
- The first storyboard resolve of a session costs **90–150 ms** on the UI thread, on the frame the first
  slide subscribes on. That is the first-notification jank.
- Later resolves cost only ~1.3 ms, so memoizing the dictionary does **not** explain a janky slide-out;
  look to the save pipeline's allocation churn instead, via the `[Toast] Slide out` log line.

### Limits

The card is toast-shaped — layered, chromeless, sized to content, carrying the same shadow and blur
effects — but it is not the real template bound to a real view model, so absolute first-paint costs are
lower than the live toast's. It measures the mechanism, not the card.

## The storyboard probes

Two tools cover the slide as it works now — a WPF `Storyboard` on the card host's translate inside a
stationary window, rather than a per-frame `SetWindowPos`.

### `SlideStoryboardProbe.exe`

Loads the real bundled storyboard out of the built assembly and runs it through the service's own
resolve/retarget logic against a real layered window, reporting the travel it produced.

```powershell
tools\capture-harness\bin\SlideStoryboardProbe.exe [pluginDir]
```

`movedDip=0` is the failure it exists for. Source-text tests structurally cannot catch it: XAML
normalises `Storyboard.TargetProperty` to indexed placeholders (`(0).(1)[1].(2)`) with the real
properties in `PathParameters`, so matching the authored path string never succeeds, and an unrecognised
slide child gets no `From`/`To`. A `DoubleAnimation` with neither animates a property from its own value
to its own value — no motion, no exception, nothing logged. That shipped.

Its stop-and-revert check does **not** faithfully model the app: it passed against knowingly-broken
seeding. Do not treat that half as a guard.

### `SlideCadenceProbe.exe`

Measures the composition rate actually sustained *during* the motion, per mechanism, against the
display's own period read from the OS.

```powershell
tools\capture-harness\bin\SlideCadenceProbe.exe [--repeats 5] [--load N]
```

`--load N` spawns N child processes of the probe itself, each rendering large animated blurs every
frame. GPU contention arrives from other processes when a game runs, so the load deliberately lives
outside the measuring process: it competes at the GPU and DWM, never inside this render loop.

Measured on a 165 Hz display:

| mechanism | sustained | % of refresh |
|---|---|---|
| `WindowMove` (the pre-storyboard slide) | 55–82 Hz | **33–50%** |
| `Transform` (what ships) | 165 Hz | **100%** |
| `TransformCached` (+ `BitmapCache`) | 165 Hz | 100% |
| `TransformNoPadding` | 165 Hz | 100% |
| `TransformWithSampling` (+ overlay-track `RenderTargetBitmap` at 60 fps) | 165 Hz | 100% |

What that establishes: moving a per-pixel-alpha layered HWND once per composed frame costs roughly half
the display's rate, and animating content inside a stationary one costs none of it. The travel padding
the window reserves is free, a bitmap cache buys nothing because the transform path is already at the
ceiling — so it is not worth its invalidation risk against the countdown bar and animated backgrounds —
and overlay-track sampling does not pull the slide off the refresh rate either.

Measured on a 60 Hz display with `--load 3` (GPU saturated by three child render processes):

| mechanism | sustained | % of refresh |
|---|---|---|
| `Transform` | 30 Hz | 50% |
| `TransformCached` (+ `BitmapCache`) | 30 Hz | 50% |
| `TransformNoPadding` | 30 Hz | 50% |
| `TransformWithSampling` | 30 Hz | 50% |

Under contention every transform variant degrades identically: the per-frame cost that loses frames is
the layered window's own composition against a saturated GPU, not card re-rasterization — the retained
tree already avoids re-rasterizing a static card on translate. A `BitmapCache` therefore buys nothing in
the contended regime either, which is why the plugin's quiet-slide scope does not cache the slide host.

Counting the animation's own value changes would prove nothing: a WPF timeline advances once per composed
frame by construction, so that only re-measures the render loop. The rate the loop itself holds is the
number.

## The composer probe

```powershell
tools\capture-harness\bin\ComposerProbe.exe
```

A/B pixel comparison of the recorder's frame path: the shipped three-pass route
(`GpuHdrToneMapper` -> `CopySubresourceRegion` crop -> `FrameScaler` downscale) against the single
`FrameComposer` pass meant to replace it, over identical synthetic input. All three classes are
compiled in from `source\Services\Capture\`, so it always compares the current code.

It exists because the two defects the fold can introduce are both invisible in a plausible-looking
clip: a crop that lands on the wrong pixels, and a sampler that pulls window chrome into the frame
edge. Every pixel of the SDR fixture states its own coordinates, so an off-by-one crop produces a
numerically different image rather than a similar-looking one.

Geometry and colour are judged separately, and that separation is the point:

- **Geometry** is proven only by the 1:1 cases and by SDR, where the two paths must be *bit-identical*.
- **Colour** differs by design on HDR. The shipped route tone-maps at full resolution and then
  averages the sRGB-encoded result; the fold averages linear scRGB and then tone-maps. Averaging in
  linear light before the transfer curve is the correct order, so the fold differs from the reference
  exactly where the reference is wrong.

The luminance centroid locates the picture, but **only while the two paths agree on colour**. An
earlier version judged geometry by centroid on every case and failed the production shape at
1.24 px — not a crop error, but linear-light averaging brightening every dark-side checker edge and
moving the centroid photometrically. Cases with a deliberately extreme order difference are marked
`GeometryCheck = false` for that reason.

Measured (all ten cases pass):

| case | maxDelta | meanDelta |
|---|---|---|
| every SDR case, incl. sub-rect, odd-size and downscale | **0.0** | 0.000 |
| HDR identity 1:1, HDR sub-rect crop 1:1 | **0.0** | 0.000 |
| HDR production 2544x1401 -> 1920x1080, realistic ramp | 1.0 | 0.020 |
| HDR blown-highlight checker at the same non-integer ratio | 160.0 | 13.333 |

So the fold is geometrically exact and, on realistic content, within one 8-bit step of the path that
ships. The 160 is the worst case by construction: a 4x4 checker alternating linear 4.0 and 0.02
straddles the tone-map shoulder, where averaging before the curve gives 255 and averaging after it
gives 147. Real frames do not look like that; the ramp case is the representative number.

## Supporting tools

- **`Show-Mp4Timeline.ps1 <file.mp4>`** — dumps `mdhd` durations and the `stts` table per track. A single
  uniform `stts` entry means per-frame durations were flattened; many entries mean real timing survived.
- **`FrameDump.exe <clip> <outDir> <startSeconds> <endSeconds> [maxWidth]`** — writes frames as PNGs with
  their timestamps in the file names, plus a per-frame change score.
- **`Match-ShotToClip.ps1 -Clip <clip> -Shot <screenshot>`** — finds which frame of a clip matches a
  screenshot. Because a screenshot is taken at a known instant, the matching frame pins how clip time maps
  to wall clock. This is what localised the toast-placement defect on real footage.
- **`AttributeBisect.exe <outDir>`** — feeds the H.264 encoder uneven per-sample durations under different
  media-type attribute combinations. Documents that the shipped combination flattens durations onto the
  declared frame rate, which is why capture paces itself by frame count instead of by timestamp.
- **`PacerProbe.exe`** — compares `Thread.Sleep` against a high-resolution waitable timer at a frame
  interval. Beware `TimeSpan.FromSeconds`: it rounds to the nearest millisecond, so `1.0/60` becomes 17 ms
  and pins a 60 fps loop to 58.8 fps.
- **`GenerationLoss.exe <source.mp4> <outDir> [bitrateKbps...]`** — re-encodes a clip at several bitrates
  and reports PSNR against the source, for sizing the export-time bitrate headroom. Slow: each rate costs a
  decode plus an encode plus two comparison decodes.

## The clip remnant probe

```powershell
tools\capture-harness\bin\ClipRemnantProbe.exe <clip.mp4> <sound file> [--volume 0.5] [--floor 0.06] [--block 0.25]
```

Measures what an exported clip still carries of a notification sound, from the clip alone. It decodes
the clip's audio and the sound file to the export format, finds every occurrence of the sound by
normalized correlation against its first second, and prints each occurrence's level relative to the
played volume plus a per-block row of signed gain and best lag offset. The composited chime reads
0 dB at lag 0 in every block, and it should be the ONLY occurrence: clip tracks exclude the sound
host's process, so a second occurrence anywhere means the live sound reached the capture and the
exclusion failed. Rows inside the composited chime's own span with correlation around 0.1-0.2 are the
jingle correlating with its own later notes, not a second copy. The per-block gain and lag columns
date from the cancellation era and still read the same way: a flat row is a level mismatch, a sloped
row a time-varying level, and a row whose lag steps partway through is a capture alignment tear. Needs no capture
buffer, so it works on clips a user sends.

## The chime separation probe

```powershell
tools\capture-harness\bin\ChimeSeparationProbe.exe          # plays two quiet tones for ~7 s
tools\capture-harness\bin\ChimeSeparationProbe.exe --tone <freqHz> <seconds> [amp] [amHz]
```

End-to-end proof that process-scoped loopback separates a chime from emulator audio on real WASAPI
sessions, without Playnite.
The probe recreates the process topology of a Playnite-launched emulator — it plays a 440 Hz "chime"
from its own process (the sound host's role) while a spawned child process plays an AM-warbled
1320 Hz "game" tone (RetroArch's role) — then captures three streams with the plugin's real
`ProcessLoopbackCapture` (compiled in from source): include-tree on the child (the Game Only clip
track), include-tree on itself (the sound host's own render; the child is inside that tree, so this
is the 0 dB reference for the other two), and exclude-tree on itself (the Full System clip track).
Goertzel power at the two frequencies then verifies each scope: the child-scoped capture must not
carry the parent's chime, and the excluded capture must carry neither tone.
There is no cancellation step; the plugin keeps the unlock sound out of clips by excluding the sound
host's process, so the only thing to prove is that the scopes are what the filters say they are.
The exe carries a Win10 `supportedOS` manifest (`win10.manifest`) because
`ProcessLoopbackCapture.IsSupported` reads `Environment.OSVersion`, which lies (6.2) in unmanifested
processes; inside Playnite the plugin never sees this.
The two include-tree captures are immune to other applications' audio by construction; only the
exclude-tree check is informational when something else is playing.

## The chime burst probe

```powershell
tools\capture-harness\bin\ChimeBurstProbe.exe [--keep] [--no-haptics] [--cold]
```

The burst scenario — two toast waves of three achievements — on the REAL recorder plumbing.
Unlike the separation probe's raw loopback clients, this drives two actual `AudioLoopbackRecorder`
instances concurrently, one Game Only and one Full System, wired exactly as
`UnlockRecordingService` wires them (game pid and sound-host pid delegates), so the mixer graph,
direct packet timestamping, wall-clock main pump, gap padding, chunk rotation, the 8-channel process
captures and their stereo reduction are all exercised. The topology is production's: a spawned
"game" child plays the game tone, a spawned "sound host" child plays the wave chimes on schedule and
reports each launch stamp, and the probe itself plays nothing during the waves.
A wave plays one chime regardless of its card count, so two waves of three means two chimes at wave
cadence (~7.5 s apart with the default 6 s toast), each at a distinct frequency (440 / 587 Hz) so a
chime leaking into a slice is directly measurable. The game tone rides on band-limited noise, so a
chime bin's leakage is measured against the OTHER wave's chime bin in the same window, whose own
chime is 7.5 s away and which is therefore pure noise there. Same window and a neighbouring
frequency, so neither a slice that is quieter overall nor the noise floor's slope can read as a
chime. Referencing the same bin in a later window, or a control bin in a different window,
differences two independent single-bin noise estimates and adds their scatter: measured 2026-09-06,
that left under 1 dB of margin and produced a false failure, while the same-window form spans -22
to +6 dB against a 12 dB limit, with a real leak reading 15 dB or more.
Per mode and per wave it reads the toast-plus-tail slice of the clip track (`aud_`) and, in Game
Only, of the exclude-host fallback track (`alt_`), and asserts that each carries the game marker tone
and shows no rise at the chime frequency while the live chime plays. It also checks the recorders'
own account of what they captured (`ClipTrack`, `HasFallbackTrack`, `ExcludedSoundHostProcessId`)
and runs the production `ChimeCompositeDecision` (compiled in from source) to confirm both modes
receive the composited chime.
When exactly one controller endpoint is connected, the game child also renders a 180 Hz actuator
tone for the whole run. The probe then runs a plain stereo process capture of the game tree beside
the recorders: that capture folds the actuator channels into L/R, the way every recorder capture did
before the 8-channel format. How far the actuator bin rises above that capture's own noise floor
(a 250 Hz control bin, clear of every tone and of 180 Hz's harmonics) is the contamination
reference, and every clip track must rise at least 20 dB less than it. Both terms come from the same
signal, so the figure does not move with the game marker, which an earlier marker-normalised form
did: the marker swung 11 dB between slices and manufactured a haptic failure on a cold start whose
actuator bin was sitting at the noise floor. Measured 2026-09-06 with a DualSense connected, a
stereo capture rises 39.6 to 41.2 dB and every clip track -1.9 to +3.3 dB. `--no-haptics` skips that layer for an A/B; `--cold` skips the
sound host's warm-up so its first render stream starts cold.
The run takes ~35 s and plays whisper-level tones; `--keep` retains the chunk directories (failures
keep them automatically).

## The channel-map probe

```powershell
tools\capture-harness\bin\ChannelMapProbe.exe [--endpoint <index>] [--channels 4|6|8] [--tone-channel 2] [--hz 180]
```

Answers one question: does process loopback keep channel identity when the capture asks for a
multichannel format? A child renders a tone on one channel of a 4-channel stream to the chosen
endpoint (the controller when one is connected, else the default output); the parent captures
include-tree on the child both stereo (the recorder's format today) and at the requested channel
count, and reports the tone's power per capture channel. A DualSense on USB exposes a 4-channel
endpoint whose channels 2/3 carry the haptics, so a preserved channel means an exclude-host capture
at 4 channels can drop the actuators by channel and the haptics never need cancelling. Measured
2026-09-05: against a stereo default endpoint the engine accepts 4, 6 and 8-channel process-loopback
formats but the tone lands in the front channels, because the stereo endpoint downmixed the stream
before the tap. Against a 7.1 endpoint a tone rendered on back-left arrived on capture channel 2 of
a 4-channel capture (and channel 4 of an 8-channel one) with channels 0/1 at -140 dB: the engine
keeps each channel at its speaker position when the capture has room for it. The same endpoint
also showed why the capture must be as wide as the widest endpoint: the tap converts every stream
to its endpoint's mix format and then averages it down to a narrower capture, so a 4-channel capture
read 6.7 dB low and a stereo one 13.5 dB low while an 8-channel capture was at true level, whatever
the source stream's own width (`--source-channels`). A running game can be measured against all
three at once with `--pid`. That is what the recorder now relies on (8 channels, folded to stereo by
`SurroundDownmix`); the controller endpoint itself still wants one run with a pad connected.

## The haptic endpoint-isolation probe

```powershell
tools\capture-harness\bin\HapticProbe.exe
tools\capture-harness\bin\HapticProbe.exe --auto
tools\capture-harness\bin\HapticProbe.exe --measure <endpoint-index>
```

With no arguments, the tool inventories active render endpoints and reports the safe microphone
selection. `--auto` requires exactly one detected controller output and drives native actuator
channels 2 and 3 separately while a normal game tone plays to the default output.

For each actuator it compares the endpoint audio production records with endpoint-agnostic process
loopback. The endpoint track must reject the controller tone by at least 30 dB while retaining the
game tone within 3 dB. This tests the actual invariant used by both recording settings: a separate
controller endpoint never enters the main WAV. If the controller is itself the default output, the
probe follows production by keeping native front L/R and discarding actuator channels 2/3.
`--measure` runs the same two-actuator proof on a listed controller. The complete report is saved as
`HapticProbe-report.txt` beside the executable.

Game Only subsequently removes a timestamped non-game sidecar with one full-clip stereo subtraction.
That separation is best effort; failure keeps the audible endpoint mix. It cannot reintroduce controller
audio or turn a clip into video-only. Full System uses the endpoint mix directly.

## Limits

- Runs against a GDI window at whatever size the window is, not a real game at 2560x1440. Anything that
  depends on the capture pump saturating may not reproduce here.
- The harness drives the plugin's internal types by reflection, so renaming them breaks it silently at
  runtime rather than at build time.
- Long-session behaviour is only sampled: a five-minute run showed no cumulative drift, but sessions run
  far longer than that.
