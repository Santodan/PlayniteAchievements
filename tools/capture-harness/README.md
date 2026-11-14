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

### Notification performance trials

Build the plugin in Release, then compile just the GPU workload and card benchmark:

```powershell
tools\capture-harness\build.ps1 -Configuration Release -Only TemplateBench,GameLoad
tools\capture-harness\Run-NotificationTrials.ps1 -ReferencePlugin C:\path\to\reference\Release -IncludeLiveMotion
```

The runner alternates reference/candidate order and writes logs to `artifacts\notification-perf`.
It uses the local Playnite notification settings, five cycles per run, a saturated borderless D3D11
workload, and no desktop capture by default. `-Scale 1` tests normal size; the default `-Scale 4`
stresses large cards. `-Priority 2` tests normal GPU scheduling; `-Priority 5` matches the plugin's
notification boost. `-IncludeLiveMotion` also tests continuous animation without the motion cache
or quiet scope. Run trials sequentially on the interactive desktop with other GPU benchmarks stopped.

Use `-Capture` in a separate run to sample desktop updates. The recorder runs in its own process at
normal GPU priority. Capture consumes GPU time, so those game FPS numbers must not be mixed with
capture-free results. `missed` counts desktop updates the recorder could not acquire; `changed/s`
is a lower bound when updates are missed. `wpf/s` counts distinct WPF rendering callbacks, not
displayed frames. Neither counter verifies hardware VRR/scanout. Disabled capture prints `n/a`.

For visual changes, `TemplateBench --template original.xaml --compare candidate.xaml --kind rare`
compares actual desktop pixels. Check common, rare, completion, and progress cards as well as
fractional scales; passing one text layout is insufficient. An identical-template control should
report zero differences. Realtime scheduling can increase toast cadence at the game's expense;
compare game FPS and notification cadence separately before calling a change an optimization.

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
    [--card-width 442] [--card-height 138] [--glow 12] [--nested]
    [--ease back:0.35|cubic|quad|quint|sine|expo:6|power:3|linear] [--duration 240]
    [--only A,B] [--verify] [--truth]
```

`--ease` and `--duration` set the slide curve (always ease-out) and length for every mechanism and for
`--truth`; the defaults are the shipped `BackEase` 0.35 over 240 ms.

The geometry flags matter more than they look: the default 442x138 card with a 12-radius shadow pins
every transform variant at 100% of refresh on a quick display, and a variant measured with no headroom
cannot show a win. Display scale is not settable here, so reproduce a scaled card by its device-pixel
equivalent - `--card-width 2328 --card-height 496 --glow 72` is the same rasterization work at 100% as
a 1164x248 card with a 36-radius border glow at 200%. `--nested` gives each text line the real
template's nested effect pair, which costs two full-width render intermediates per line instead of one.

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

#### At a user-sized card, the surface is the ceiling

Both tables above were taken at the default 442x138 card, where the transform variants sit at 100% and
so cannot separate. Re-measured on the same 165 Hz display at a reported user geometry -
`--card-width 2328 --card-height 496 --glow 72 --nested`, the device-pixel equivalent of a 1164x248
card with the border glow on at 200% scale - `Transform` alone drops to **22 frames, 82.5 Hz, 50% of
refresh**, with no GPU load at all.

The glow radius does not move that number. Holding the geometry and varying only the blur:

| card effect | `Transform` frames | sustained |
|---|---|---|
| `--glow 72` | 23 | 50% |
| `--glow 24` | 22 | 50% |
| `--glow 0` | 23 | 50% |

So the per-frame cost is **the layered window's surface blit, which scales with area**, not the card's
effects - consistent with the contended finding above that a retained tree does not re-rasterize a
static card on translate. A blur is paid once, at first paint; the blit is paid every frame.

This corrects one claim above: *the travel padding is not free at a large card.* It is free at 442x138
only because everything there is already at the ceiling. `TransformNoPadding` removes ~57% of the
surface (the reserved travel room, which at a top corner hangs off the monitor entirely) and is the
only variant observed to reach 100% at the large geometry. It remains unusable as a mode - an HWND
clips its content, so a slide needs that room - but it prices what the oversized surface costs, and it
means shrinking the window to the card *after the settle* is worth real frames for the hold, where 10
of a toast's ~10.2 seconds are spent.

A `BitmapCache` still buys nothing: `TransformCached` did not beat `Transform` at this geometry either,
which is now explained rather than merely observed - there is no per-frame rasterization for a cache to
save. Its numbers, and `TransformNoPadding`'s, varied run to run by enough (20-38 frames) that only
`Transform`'s stable 22-23 should be read as a measurement; repeat those two before acting on them.

#### Under GPU load, DWM is the ceiling, not the window

`TransformDwm` replaces the layered window with a non-layered one: `AllowsTransparency` off, a
transparent `HwndSource` background, and `DwmExtendFrameIntoClientArea` with -1 margins.
That removes the per-frame software readback and `UpdateLayeredWindow` call.
`--verify` confirms both window kinds show a magenta backdrop through the travel padding and draw the
card opaque, so the comparison is like for like.
The `dwmHz` column is DWM's own composed-frame count (`DwmGetCompositionTimingInfo.cFrame`) across the
slide.

Measured on the 165 Hz display at a reported user's physical card size
(`--card-width 1164 --card-height 248 --glow 36 --nested`, 7 repeats):

| load | `Transform` | `TransformDwm` | DWM itself |
|---|---|---|---|
| none | 165 Hz | 165 Hz | 135-159 Hz |
| `--load 1` | 82.5 Hz | 82.5 Hz | 78-91 Hz |
| `--load 3` | 41-55 Hz | 55 Hz | 48-53 Hz |

The slide runs at whatever rate DWM composes at, and DWM slows down when other processes saturate the
GPU.
No window mechanism in this process can beat that: the non-layered window matches the layered one
within run-to-run noise, and `--gpu-priority 4` (`D3DKMTSetProcessSchedulingPriorityClass` HIGH,
accepted with status 0) moved nothing either.
A notification over a GPU-bound game therefore animates at roughly the rate DWM is composing at the
time, whatever the toast window is made of.

#### Ground truth: real clock helps when idle, nothing helps under load

`--truth` (SlideTruth.cs) measures where the card actually was on screen at the instant each frame
was presented, via DXGI desktop duplication: each duplicated frame carries `LastPresentTime` on the
`Stopwatch` QPC clock, and one pixel column through the card over a magenta backdrop gives its edge.
Each slide is fitted to the curve with a free constant latency and position offset, so `residualSd`
counts uneven steps, not delay. `SLIDE_TRUTH_DUMP=1` prints every presented frame.
The `skewSd`/`errSd` columns of the cadence table cannot settle this: `TransformClock` is measured
against the clock it is driven by, so it scores near zero by construction.

`TransformClock` pauses the storyboard and seeks it every frame to `Stopwatch` time since Begin.
Measured on the 165 Hz display at `--card-width 1164 --card-height 248 --glow 36 --nested`,
9 repeats, median residual (spread of all runs):

| load | `Transform` (WPF clock) | `TransformClock` (real clock) |
|---|---|---|
| none | 7.4 px (4.1-15.5) | 1.9 px (1.0-18.8) |
| `--load 1` | 11.2 px (4.0-17.4) | 11.2 px (7.4-16.5) |
| `--load 3` | 21.6 px (15.9-28.7) | 21.3 px (14.8-27.3) |

Idle, the real clock removes most of the unevenness. Under GPU load both are equally uneven:
the error is now the varying delay between building a frame and DWM presenting it (raw dumps show
the same position presented three times, then a 79 px jump), and no clock read at build time can
predict that delay.

The default `BackEase` (amplitude 0.35) also concentrates the motion: the card reaches its rest
offset about 100 ms into the 240 ms slide and spends the rest in the overshoot, so most of the
visible travel happens in a handful of frames at peak speed. Residual in pixels is speed times
timing error, so a curve that spreads the travel over the whole duration lowers it in proportion.

#### At the user's device size, the layered surface is the ceiling and a DWM window lifts it

Re-measured at the device-pixel geometry of a reported VRR user's card
(`--card-width 2328 --card-height 496 --glow 72 --nested`; 1164x248 at 200%), 2026-10-08.
Frames of an ideal 40 at 165 Hz, idle, across three runs:

| mechanism | frames |
|---|---|
| `TransformClock` (layered, real clock: what ships) | 22-29 |
| `TransformNoPadding` | 34-35 |
| `TransformDwm` | 35-39 |
| `TransformDwmClock` (DWM window, real clock) | 40 |

`--truth`, 9 repeats, median residual:

| load | `TransformClock` | `TransformDwmClock` |
|---|---|---|
| none | 12.7 px | 4.7 px |
| `--load 1` | 16.4 px | 16.9 px |

So the earlier "a non-layered window does not help" holds only under GPU load. With headroom, the
layered window's per-frame surface copy, which grows with device pixels, is what drops frames, and a
DWM window removes it. On a VRR display DWM composes at the game's present rate, so frames this
process fails to deliver are dropped on top of the game's own unevenness.

What a DWM window loses, checked with `--verify`:

- Click-through. A layered window passes clicks through its fully transparent pixels; a DWM window
  takes every click in its rectangle, travel padding included (`WindowFromPoint` lands on it).
- `Window.Opacity`, which only applies with `AllowsTransparency`. `DWMWA_CLOAK` hides the window
  instead (the card reads as the backdrop while cloaked).
- Adding `WS_EX_LAYERED` back to a non-transparent WPF window to get both does not work: the bit is
  dropped from the extended style even through a `WM_STYLECHANGING` hook, and
  `SetLayeredWindowAttributes` then fails with error 87. `WS_EX_TRANSPARENT` and `WS_EX_NOACTIVATE` do stick.

#### Under load, the curve is the lever

`--truth --load 1` at the same geometry, 9 repeats, median residual (spread of all runs):

| curve / duration | `TransformClock` | `TransformDwmClock` |
|---|---|---|
| back 0.35 / 240 ms (shipped) | 17.7 px | 11.0 px |
| back 0.15 / 240 ms | 10.5 px | 12.1 px |
| cubic / 240 ms | 10.2 px | 10.2 px |
| sine / 240 ms | 8.1-8.3 px | 7.4-10.7 px |
| sine / 280 ms | 7.7 px | 9.5 px |
| sine / 300 ms | 6.6 px (5.7-9.8) | 8.6 px |
| sine / 320 ms | 6.8 px | 7.6 px |
| cubic / 360 ms | 8.2 px | 8.8 px |
| sine / 360 ms | 5.4-5.5 px | 5.9-6.8 px |
| quint / 400 ms | 6.2 px | 8.0 px |

Idle, `TransformDwmClock` with sine / 300 ms measured 2.1 px; the idle DWM column is the noisiest,
with single runs above 10 px at most settings.

Removing the overshoot and spreading the travel across the slide is what helps once DWM sets the
rate: sine ease-out moves on most presented frames (29 of 35 at 360 ms) where the shipped curve
moves on 7 of 21, so a late frame is a small step instead of a jump. Sine / 300 ms keeps most of the
360 ms gain at a quicker entrance.

## The hold probe

```powershell
tools\capture-harness\bin\HoldProbe.exe [--card-width 2328] [--card-height 496] [--glow 72]
    [--repeats 5] [--seconds 4] [--load N] [--only A,B] [--shape notch|rect|soft] [--fps 50]
    [--scale 1] [--compare-only] [--dump <dir>]
```

Prices the seconds a notification rests on screen, and checks on screen that cheaper layerings look the
same. The card mirrors the bundled `AchievementToast.xaml`: a glow layer with the border-glow
`DropShadowEffect` pulsed through `Effect.Opacity` (as `RarityGlowPulse` does with `Target="Effect"`)
over a backing copy of the background, then an effect-free content layer with the visible background
copy and text lines carrying the nested shadow pair. The background is a synthetic animated bitmap
updated like `GifPlayer` updates its `WriteableBitmap`; `notch` gives it binary alpha that is the same
in every frame (a GIF's case), `soft` a fading edge (a translucent WebM's case). Windows are
DWM-composited, as revealed notifications are.

Per variant it reports the composition rate the UI thread held, DWM's composed frames, and this
process's CPU time per second. Then each candidate is frozen at the same background frame and pulse
value over a solid backdrop and compared pixel for pixel with Shipped (the backdrop matters: without it
the transparent areas read the console behind the card as it prints).

Measured 2026-10-08 on the 165 Hz display at the default geometry (a 1164x248 card at 200%), medians of
5 interleaved runs:

| variant | idle cpu ms/s | `--load 1` composed | on screen vs Shipped, notch | soft |
|---|---|---|---|---|
| Shipped | 363-406 | 59-62% | - | - |
| FrozenBacking (backing copy a still) | 336-394 | 58-60% | 3 px, max 1 level | 252k px, max 49 levels |
| CachedGlow (BitmapCache, pulse on layer Opacity) | 344 | 61% | 185k px, max 4 levels | max 42 levels |
| CachedText (BitmapCache per text line) | 266 | 63% | 90k px, max 114 levels | same |
| SplitGlow (glow layer in its own window behind) | 223-246 | 66-72% | 3 px, max 1 level | - |
| SplitAllLive (glow, background and text in three windows) | 223 | 97% | 1 px, max 1 level | 162k px, max 1 level |
| NoTextShadow (price, not a candidate) | 188-191 | 97-98% | - | - |
| PulseOff (price) | 133-141 | - | - | - |
| NoGlow (price) | 125 | - | - | - |

What it establishes:

- WPF does not keep an effect's output. Any redraw of a region re-runs every effect in it, and the pulse
  redraws the whole card at the refresh rate, so the text shadows run 165 times a second. They are what
  drops frames under load: without them the card holds 97-98% instead of about 60%.
- `BitmapCache` stops the re-runs but changes pixels: up to 4 levels on the glow, and visibly heavier
  glyph edges on cached text (up to 114 levels), with or without `SnapsToDevicePixels`.
- WPF tracks redraws per window, and DWM blends the windows' retained surfaces. Splitting the layers into
  windows stacked in the template's order leaves each one redrawing only when its own content changes:
  the text window never, after its first frame. On screen it matches the single window to within
  1 level (8-bit rounding of the blend order), on binary and soft alpha alike.
- Freezing the backing copy is lossless only for binary alpha that is the same in every frame, and buys
  nothing on its own while the pulse runs; with the layers split it is not needed.

Forcing `TextOptions.TextHintingMode` (Fixed, Animated) or `TextRenderingMode` (Grayscale, Aliased) on
a cached line (`--cache-hint`, `--cache-render`) left the difference exactly unchanged (89,746 pixels,
max 114 levels), so it comes from how the cached layer is composited, not from text rasterization;
`--cache-snap` made it worse.

### Motion: `--motion slide|fade|zoom`

With `--motion`, the background and pulse stand still (as the quiet gate holds them during a
notification's motion) and the card cycles its entrance and exit continuously at the shipped sine /
300 ms, on the slide host's transform group as the plugin animates it. `CachedHost` caches that host
for the motion; `--pose` freezes the comparison at a shown fraction (1 at rest), `--snap` / `--snap-live`
round the slide offset to whole device pixels, `--host-cache-snap` sets the cache's
`SnapsToDevicePixels`, and `--host-cache-scale` its `RenderAtScale`.

Composed rate held, `--load 1`, default geometry, medians of 3:

| motion | Shipped | NoGlow | NoTextShadow | CachedHost |
|---|---|---|---|---|
| slide | 61% | 67% | 98% | 99% |
| fade | 42% | 51% | 96% | 97% |
| zoom | 43% (55% idle) | 53% | 98% | 99% |

On screen against the live card:

| case | difference |
|---|---|
| at rest, every motion | 0 px |
| slide mid-motion, fractional offset | max 29-32 levels |
| slide mid-motion, cache `SnapsToDevicePixels` vs live at the rounded offset | 0 px |
| fade mid-motion | max 1 level |
| zoom mid-motion | max 145-207 levels at `RenderAtScale` 1 or 2 (a scaled cache is resampled) |
| `--scale 1.5` ancestor, cache at `RenderAtScale` 1 | max 249 levels even at rest |
| `--scale 1.5` ancestor, cache at `RenderAtScale` 1.5: rest, rounded slide / mid-fade | 0 px / max 1 level |

So during a motion the card's effects are what drop frames (the moving card is redrawn, and every effect
re-run, each frame), and caching the host for the motion's span removes that cost. It matches the live
card exactly when the cache renders at the card's full scale and snaps to device pixels, which rounds
the slide to whole pixels; a fade is within 1 level; a zoom is not reproducible from a cache.

WPF does not fold the display scale into the cache's resolution. Measured on a monitor set to 150%
(`--at <x,y>` creates the windows per-monitor-v2 there and places them in physical pixels;
`HoldProbe.exe.config` turns on WPF's per-monitor scaling, as Playnite runs with it, and the probe
prints the scale the window actually renders at): a cache at `RenderAtScale` 1 differed by up to 225
levels at rest, and at 1.5 matched exactly at rest for every motion, within 1 level mid-fade and on a
snapped mid-slide, with zoom resampled as at 100%. The cache's scale is therefore the window's own
device scale.

### Split windows while moving: `SlideCadenceProbe --split-truth`

```powershell
tools\capture-harness\bin\SlideCadenceProbe.exe --split-truth [--repeats 10] [--load N]
    [--ease sine] [--duration 300] [--card-width 2328] [--card-height 496] [--glow 72]
```

`SplitTruth.cs` slides three stacked DWM windows (the pulsing glow, a 50 fps animated background, text
with its shadows) through one shared `TranslateTransform` set from the clock each frame, and reads every
frame DWM presents through desktop duplication. Each window carries a solid marker in its own screen
column (the back one in the glow margin); a frame is misaligned when the three marker edges are more than
1 px apart.

Measured 2026-10-08, sine / 300 ms at the default geometry, 10 slides each:

| load | moving frames | misaligned | worst spread |
|---|---|---|---|
| none | 430 | 1 | 2 px |
| `--load 1` | 269 | 21 | 58 px |

Idle the windows move together; under GPU load each window's frame reaches DWM on its own, and one in
thirteen presented frames shows them apart. A card split across windows must therefore stand still while
split, and any change between one window and several is not guaranteed to land in a single frame under
load. A group opacity (fade, zoom) also cannot be reproduced per layer: each layer faded separately lets
the background show through the text mid-fade.

## The thumbnail probe

```powershell
tools\capture-harness\bin\ThumbnailProbe.exe [--presenter noredirect|layered] [--dump <dir>]
tools\capture-harness\bin\TemplateBench.exe --game 4000 --thumbnail [--software] [--tiny-animation]
```

`ThumbnailProbe` shows a card (translucent fill, blurred glow, shadowed text) in a DWM-composited WPF
window over a solid backdrop, captures the screen, cloaks the window, and shows its DWM thumbnail
(`DwmRegisterThumbnail`) in a second window that has no surface of its own
(`WS_EX_NOREDIRECTIONBITMAP`, or layered with a colour key).
`TemplateBench --thumbnail` does the same with the real card: the card window stays cloaked for the whole
run, and entrance and exit move the thumbnail's destination rectangle from a thread that waits on
`DwmFlush`, with no WPF frame involved.

Measured 2026-11-13 on a 60 Hz virtual display (2880x1920) against `GameLoad --work 4000` (about 102 fps
alone), card scale 1, medians of 3 cycles.
The recorder missed updates in every run, so `changed/s` is a lower bound.

| mode | entrance | hold | exit | game fps in hold |
|---|---|---|---|---|
| window, GPU priority normal (Performance) | 21.8/s | 10.3/s | 25.0/s | 98-102 |
| window, GPU priority realtime (Quality) | 36.6/s | 29.3/s | 43.0/s | 90-97 |
| thumbnail, normal | 43.7/s | 9.7/s | 36.1/s | 101-102 |
| thumbnail, normal, card window `--software` | 59.0/s | 16.2/s | 53.6/s | 101-103 |
| thumbnail, realtime | 46.7/s | 29.6/s | 46.9/s | 95-96 |

What it established:

- A thumbnail of the cloaked card window matches the window itself on screen: 0 of 582,400 pixels
  differ at rest and after moving the destination by (37, 53), with either presenter kind. Per-pixel
  alpha is kept, and the thumbnail's opacity property fades it.
- Moving the thumbnail costs the game nothing and does not wait in the GPU queue behind it, since this
  process issues no GPU work for the motion.
- The hold is unchanged by the thumbnail: its frames still come from WPF.
- At realtime priority the game cost of the hold comes from redrawing the card, not from presenting.
  With the card still and a 6 px square animating in the window corner (`--tiny-animation`, about 59
  frames/s) the game held 101.9-102.3 fps against 103.5 with nothing animating; with the card's own
  animations running (about 42 frames/s) it held 96.3-96.7. Card scale 2 measured the same as scale 1.
- A card window rendered in software (`--software`) leaves the game untouched but its hold is limited by
  the CPU: 16.2/s at scale 1, 3.3/s at scale 2.

Limits: a virtual 60 Hz display, where the game is composed by DWM in every case, so nothing here
prices a game leaving independent flip, and nothing is measured at 165 Hz.
The pacing thread sets whole-pixel positions at the time it wakes; residual against the ideal curve
(`--truth`) is not measured for it.

### The plugin's presenter and hold layers: `TemplateBench --presenter [--hold-layers]`

```powershell
tools\capture-harness\bin\TemplateBench.exe --game 2500 --presenter [--hold-layers [--early-layers]]
    [--recorder-priority 5] [--no-capture] [--layers-compare --snapshot <prefix>]
```

`--presenter` drives the plugin's own `ToastThumbnailPresenter`: the card window stays cloaked, the slide
storyboards run on the service's empty motion stand-in, and the service mirrors the stand-in's transform
and opacity onto the thumbnail from a pump that waits on `DwmFlush`.
`--hold-layers` also calls the service's `BeginHoldLayersAsync` / `EndHoldLayersAsync` around the hold: the
cards' backgrounds and content are shown from two software-rendered layer windows (`ToastHoldLayers`) over
the card window, which holds its animated images on a still and hides everything but what is drawn behind
the card.
`--recorder-priority 5` runs the capture child at the realtime GPU class, so it is not starved by the
game; read cadence from such a run and game fps from a `--no-capture` one.
`--layers-compare` captures the card on screen before and after the split.

Measured 2026-11-13 on the 165 Hz display (2560x1440) against `GameLoad --work 2500` (about 112 fps alone),
the user's settings (animated WebM background, border glow pulse, card rays), card scale 1, medians of 3
cycles. Desktop updates arrived at about 105-110 per second in every run, so that is the ceiling for
`changed/s`.

| mode | entrance | hold | exit | game fps in hold | game worst frame |
|---|---|---|---|---|---|
| window, GPU priority normal (Performance) | 28.1/s | 8.5/s | 25.4/s | 111.6-112.0 | 9.5 ms |
| window, GPU priority realtime (Quality) | 78.0/s | 40.7/s | 76.0/s | 105.8 | 15.5 ms |
| presenter, normal | 89.6/s | 8.8/s | 92.2/s | - | - |
| presenter and hold layers, normal | - | 59.0/s | 94.7/s | 109.3-109.5 | 9.7-9.9 ms |
| presenter and hold layers, normal, `--no-rays` | - | 65.0/s | - | 109.5 | 9.7 ms |
| presenter and hold layers with a rays layer, normal | - | 56.7/s | - | 111.6 | 10.3 ms |

The entrance of a `--hold-layers` run is left out: the bench disposes the previous cycle's layer windows
right before it, which a wave does only after its exit.

What it established:

- With the card still in its window, WPF raised `CompositionTarget.Rendering` about 3 times a second
  under the game, and a slide mirrored from that event stepped at that rate (entrance 3.0/s). Stepped from
  a thread that waits on `DwmFlush` and posts to the dispatcher, it reached 78-93/s at normal priority.
- WPF presents every window of the process from one render thread. A software-rendered background layer
  beside hardware layers reached the screen 3.3 times a second at normal priority, the rate of the starved
  hardware frames; with both layers in software it reached 59/s.
- Splitting layers does not lower the cost of the realtime class: three hardware windows cost the game
  about 8 fps of 112 against about 6 for the one window.
- The layers match the single window on screen. With hardware content: 1 level on about 11,500 pixels
  and at most 7 levels on three corner pixels, measured while the background video differed between the
  two captures. With software content: at most 10 levels, in the soft shadows around text and icons.
- An animated image stops decoding when its `Image` is not visible, so the background layer must not hide
  the template's zero-size decoding image.
- Without card rays the border glow's pulse is reproduced by the main thumbnail's opacity and the card
  window draws nothing during the hold (`mainStill=True, pulsePresented=True` in the log line).

- The rows above the last were taken with the card's rays left in the hardware card window, which then
  kept redrawing (`mainStill=False`). With the rays in a third software layer drawn under the card
  window (a second thumbnail of the card window is registered after it, since thumbnails stack in
  registration order), the card window draws nothing during the hold, the longest gap between updates
  fell from 124 ms to 50 ms, and the game held 111.6 fps against 112.2 with the card hidden.

Measured 2026-11-14 on the same display and load, with three changes in the plugin: the layers are built
before the entrance (`--early-layers` calls `PrepareHoldLayers` there), the slide is sampled into a motion
track that the frame pump's thread shows at every composition, and a card with a still opaque background
image is split as well.
Medians of 3 cycles; the game with the card hidden ran at 112.4-112.7 fps in the `--no-capture` runs.

| mode | entrance | hold | exit | game fps in hold | game worst frame |
|---|---|---|---|---|---|
| animated WebM background, presenter and early layers | 90.3/s | 56.7/s | 88.5/s | 111.5 | 9.6 ms |
| the same with `--still-background` | 83.6/s | 57.3/s | 90.9/s | 111.5 | 10.2 ms |
| `--still-background`, presenter without layers | 94.7/s | 8.3/s | 93.9/s | - | - |
| still PNG background, presenter and early layers | 96.0/s | 44.8/s | 91.9/s | 111.6 | 9.5 ms |
| still PNG background, presenter without layers | 93.3/s | 8.5/s | 93.8/s | - | - |

- Building the layers took 70 to 185 ms of the UI thread.
  Built before the entrance they were shown 0 to 7 ms into the hold (`Hold layers: shown N ms into the
  hold`); built at the settle with the 400 ms wait they were shown about 400 ms in.
- Built while the card slid in (`--layers-mid-slide`), the entrance fell to 61.9/s with a longest gap of
  150.9 ms, though the pump thread stepped the thumbnail every 11 ms at most (`Presenter track:
  steps=33 maxGapMs=11.0`).
  Creating the windows held the thumbnail on screen whichever thread moved it.
- Built before the entrance with the motion still stepped on the UI thread, the entrance reached
  45.9-48.3/s with gaps of 165 ms: the UI thread was up to 380 ms late for a frame while the new windows
  loaded.
  Stepped from the pump thread it reached 90.3-92.8/s with a longest gap of 20-34 ms.
- Seeking a begun, paused storyboard to sample it raises its `Completed` event on a later tick.
  The slide ignores a `Completed` that arrives before its clock has run the duration.
- With the wait before the layers are shown set to 0, none of the 30 compositions captured after the
  split was missing content (6 cycles under the game, `--layers-compare`); the plugin waits 100 ms.
- `--layers-compare` also renders the card as the clip sampler does (`TryRenderToastItemBytes`, effects
  stripped, rays hidden) from the card window before the split and from the layer windows after it.
  The two matched except where the card animates between the captures: 694 of 111,584 pixels on the
  still PNG card, along the countdown bar and in the animated icon.

What sets the hold's rate, measured 2026-11-14 with no game and under `GameLoad --work 2500`
(card window 634x392 px, medians of 3 cycles):

- The background video file is 50 frames a second (`DefaultDuration` 20 ms).
  With every glow animation off (`--still-background --no-pulse`; the layers still play the video) the
  hold changed 49.4 times a second.
  `--no-pulse` switches `AnimateRarityGlows` off, which stops the rays as well as the pulse.
- The glow pulse is read on the frame pump's thread (`MainOpacitySource`): 165 steps a second with no
  game and 107 under it, longest gap 7.8 and 11.3 ms, of which about 20 a second changed the
  thumbnail's 8-bit opacity (`Presenter pulse: steps=825 maxGapMs=7.7 levels=98` over 5 s).
- The rays are invalidated by `RayAnimationDriver` at 30 a second.
  At 60 (`--ray-fps 60`) the still PNG card's hold went from 43.2 to 60.0 changed/s with no game and
  from 42.8 to 56.6 under it, longest gap 56.2 to 46.9 ms; at 165 it reached 59.8.
  The WebM card measured 56.8 and 55.8.
  The process took the same processor time at 30 and 60 (about 1.22 and 1.25 s per second with the WebM
  card, 1.03 and 1.03 with the PNG card; 0.80 and 0.57 without rays) and the game ran at 111.5-111.7
  fps in all four.
  The plugin sets 60 while the rays rest in their layer (`SetHeldFps`).
- The same card in one hardware window without the presenter took about 1.08 s of processor time per
  second, and 0.48 with the presenter and no rays.

With the held rate in the plugin: WebM card 148.1 / 59.0 / 144.1 with no game and 87.3 / 58.0 / 92.8
under it; PNG card 147.6 / 59.4 / 147.1 and 93.1 / 56.8 / 92.3; game 111.5 fps in the hold for both.

#### The rays' cost

Measured 2026-11-14, same display, card window 634x392 px, no game unless noted.
Processor time is the bench process's, sampled once a second through a 10 s hold (`--no-capture`).

The card's own burst (224 arrows in 6 copies, slot 550x92 DIP) was most of a resting card's cost, in every
mode:

| hidden with | layers, WebM card | one hardware window |
|---|---|---|
| nothing | 1.23 s/s | 1.08 s/s |
| `--no-icon-rays` (the icon's burst, 32 arrows) | 1.25 s/s | 1.08 s/s |
| `--no-card-rays` | 0.91 s/s | 0.35 s/s |
| `--no-rays` | 0.80 s/s | 0.48 s/s |

- The UI thread's share was 0.15-0.3 ms a redraw; the rest was WPF filling the geometry.
- With only the narrowest copy drawn the burst cost about nothing (0.93 s/s); with three of the six, 1.14.
  `EdgeMode.Aliased` changed nothing (1.22).
  The quads of the wide copies (width multiples 9.0 to 2.1) each overlap several neighbours inside one
  nonzero geometry.
- Each copy drawn as 16 geometries of quads that do not overlap each other took 1.00 s/s, with a
  brighter picture where quads overlap; the same inside an opacity group, which restores the picture,
  1.19.
- With the rays invalidated at every frame (`--ray-fps 165`, still PNG card) the hold reached 59.4
  changed/s as drawn, 82.9 split into sets, 119.3 with one copy and 156.2 without the card burst.

`RayQuadRasterizer` fills the same picture into a bitmap on the UI thread (`RarityRayBurst.IsRasterized`,
set on the card burst in the bundled template): each quad edge is clipped against the quads near it in
the loop, and what is left, the union's outline, is summed as signed areas.
`RayQuadRasterizerTests` compares it with a `RenderTargetBitmap` of the geometry: mean difference
0.07-0.13 levels of 255 a channel, at most 19 on a pixel, after offsetting by (1/16, 1/32) px, the offset at
which WPF's own fill differed least.
It took 1.4-1.5 ms a redraw for a 593x129 px bitmap (2.5 ms before the clipping stopped at the quads whose
boxes meet).
`--ray-geometry` puts every burst back on geometry.

Two more things kept the content layer redrawing with its 15 effects run again each time
(`--content-no-effects` removes them: hold 164.3 changed/s on both cards, against 93.6 and 83.2):

- the pulse of the card's glow, which is hidden in that layer and animates its effect's opacity at every
  frame (`--content-no-pulse` with `BENCH_PULSES=hidden` stops only it: 162.6 on the WebM card; stopping
  only the visible icon halo's pulse, 83.2);
- the hidden backing copy of an animated background, an `ImageBrush` on the shared bitmap, at each of the
  video's frames (with the pulse stopped and the brush kept, the WebM card held 104.0; the PNG card 164.8).

`ToastLayer` stops the pulse and takes the image brush off every hidden element of a layer window.
Collapsing the decoding `Image` in the content and rays layers (`--layers-stop-decode`) changed nothing.

With all of it, medians of 3 cycles (entrance / hold / exit, changed/s):

| card | no game | under `GameLoad --work 2500` | game fps in hold | process |
|---|---|---|---|---|
| WebM | 150.8 / 161.8 / 148.6 | 94.3 / 79.8 / 88.7 | 110.1, worst frame 9.6 ms | 0.80 s/s |
| still PNG | 147.2 / 164.6 / 147.3 | 93.6 / 76.2 / 92.0 | 109.8, worst frame 9.9 ms | 0.58 s/s |
| WebM, `--ray-geometry` | 149.1 / 59.0 / 142.9 | - | - | 1.17 s/s |
| one hardware window, WebM | - | - | - | 0.58 s/s (1.08 on geometry) |

- Under the game the desktop was presented 105-110 times a second in the hold and the recorder missed
  557 to 694 of the updates over the three cycles, so the hold's changed/s there is a lower bound.
- The glow pulse was stepped 103 times a second under the game (`Presenter pulse: steps=517` over 5 s).
- The game ran at 111.5 fps in the hold while the card changed 57 times a second (the rows above this
  section) and at 110.1 with it changing at the display's rate.
- The icon's burst is served 60 times a second while the card rests and the card's burst at every frame
  (`RayAnimationDriver.SetHeldFps`, `IRayAnimationTarget.TakesEveryHeldFrame`); at 30 for the icon's the
  hold measured 159.0 and 164.4.
- Serving the two bursts on alternate frames changed nothing (91.6 against 92.1, before the pulse was
  stopped).

#### Cards without a background image, and blurred cards

A card is split when the lowest thing its `ToastLayer.Role` `Surface` and `Background` elements draw is
opaque (`ToastLayer.CanSplit`).
The bundled template marks its card surface `Surface` and a blurred card's live image and tint `Background`,
so the background layer shows the surface where no image covers it, without the glow of the element it
sits in.
The bench gives the popup brushes a Playnite theme supplies (`PlayAch.Brush.PopupSurface`, opaque here);
with a translucent one a card without an image rests as one window.

Measured 2026-11-14 on the 60 Hz virtual display (card window 951x588 px) under `GameLoad --work 4000`,
medians of 3 cycles, hold changed/s and the game's fps in the hold (`--no-capture`):

| card | one window | layers |
|---|---|---|
| no background image | 12.4/s, game 99.0 | 59.0/s, game 99.9 |
| blurred background, no live image | 12.2/s, game 99.6 | 59.4/s, game 100.0 |
| WebM background | - | 58.4/s |

- With no game the two held at 59.2-59.4 and 59.7 of 60.
- `--layers-compare` snapshots of both cards before and after the split show the same picture; the sampler's
  render from the layers has the surface under the content.
- The bench does not start `ToastLiveBackdrop`, so a blurred card here is its tint over the surface.
  The live image reaching the background layer, and the card's own window standing still while it changes
  (`ToastLayer.ReleaseHiddenImages`), are not measured.

#### The card control, the hand-over, and waves of several cards

Measured 2026-11-14 on the 60 Hz virtual display (one card's window 951x588 px).

`controls:ToastCard` (Themes/Generic.xaml) carries the rays, glow, surface and background with their
`ToastLayer` roles; the bundled template is the control around its content.
`--compare` of the previous bundled template against it: 0 of 559,188 pixels differ on the WebM, still PNG,
no-image and blurred cards.
A five-line theme template built on the control was split and held 59.7/s under `GameLoad --work 4000`; one
with a plain `Border` root logs `Hold layers: not split, the template marks no element ...` and held 0.0/s
(nothing in it animates).

The hand-over when the wave comes to rest, `--layers-compare` under `GameLoad --work 4000`, the most pixels
of any of the 30 compositions after it that differ by more than 16 levels from the one before it:

| path | runs |
|---|---|
| layers built at the hold: glow held at its peak there, rays change hands | 57,198 / 57,212 / 13,405 |
| layers built before the entrance: glow held and rays shown from their layer before the reveal | 7,262 / 7,268 / 7,791 |

The second row is what the card's own animation changes in that time.
In the first the saved composition shows the glow brighter than before the hand-over.

Waves of several cards (`--cards N`), hold changed/s with no game unless noted:

| | 1 card | 2 cards | 4 cards |
|---|---|---|---|
| one window under `GameLoad --work 4000` | 12.4 | 6.0 | 1.0 |
| layers, as first measured (WebM, under the game) | 59.0 | 38.2 | 32.0 |
| layers now, still PNG | 59.8 | 57.2 | 42.8-44.4 |
| layers now, WebM | 59.6 | 42.6 | 40.0-42.6 |

- Building the layers took 118-166 ms for one card and 255-1609 ms for two to four when first measured, and
  187-414 ms for four after the changes below.
- With four cards: the icon halos' pulses and the icons' rays, in the content layer, each had the
  content's effects run again around every icon.
  Marked `ToastLayer.Role` `Underlay`, they are shown with the background: four cards without rays then held
  56.2-56.8, where with the halos pulsing in the content layer they held 48.2.
- The icon's burst rasterized (`IsRasterized`): icon rays alone, four cards, 42.8 to 47.0-55.8.
- One card's burst took 2.1-3.0 ms a frame at this size (fill 1.9 ms, bitmap 881x193).
  The driver fills the bursts of a tick across threads (`IRayBatchTarget`); the hold with four cards' card
  rays alone stayed at 39.4-43.2 with WPF's frame callback at 30 a second, so the fill is not what holds it.
  Drawing the bitmaps unfiltered changed nothing (44.4, 40.0).
  What a frame of four card bursts spends its time on is not measured.

Limits: the whole wave pipeline (`ShowWaveAsync`) is not run by the bench, only the methods named above.
The sampler's ray layer taken from the rays window (`ComputeLayeredRayDelta`) and the glow scale taken
from the presented pulse are not exercised by the bench.
A card without a background image is not split: its card surface is drawn inside the glow's element in
the card window, which the presented pulse would dim along with the glow.

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

## The theme toast clip probe

```powershell
tools\capture-harness\bin\ThemeToastClipProbe.exe <AchievementToast.xaml> [--theme <themeDir>] [--base <clip.mp4>]
    [--fps 60] [--out <dir>] [--label <name>]
```

Runs a theme's toast file through the clip path and writes `<label>.mp4` plus `<label>_motion.csv` to
`bin\theme_toast\`.
The surface, slide host, shadow capture, pixel prime, per-tick sampling, slide storyboards, track recorder
and overlay re-encoder are the plugin's own, driven by reflection on an uninitialized
`ToastNotificationService`; the probe supplies only the wave timeline (warm frames, capture delay, hold,
slide-out) and the sampling cadence, both mirrored from the wave loop.
Playnite's `ThemeFile` extension is replaced by the file path it resolves to, the PS5-Experience gradient
and the `LOCPS5*` strings are defined in the probe, and the base clip defaults to `harness_clip.mp4`.

At every sample it records where the card's template root actually is on screen and compares that with
where the track will composite it (the host slide offset plus the opaque left edge of the stored frame).
The summary counts samples where the live card moved but the recorded one did not, and reports the
position error.

What it established, on the PS5-Experience toast of 2026-09-30 at 60 fps:

- A slide authored as a `Loaded`-triggered `TranslateTransform` inside the template is invisible to the
  clip path. The primed frame was taken with the card 128 px short of rest; the track held it through the
  slide-in span and then snapped (26 of 36 moving samples frozen, 162 px worst error at the slide-out,
  which the clip replaced with a fade in place).
- The shadow layer is captured once, before the slide, at the card's position then. With the card moving
  inside its own frame the halo no longer lines up, and the clip shows a ghost copy of the card's text for
  the whole hold.
- The same motion authored in `ToastSlideIn`/`ToastSlideOut` against
  `(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)` is recorded as host
  offset and interpolated: 0 frozen samples, 0.0 px error, one stored frame, no ghost.

Limits: a live wave also settles DPI and places the window between the card's `Loaded` and the slide
start, so how far a template animation has run when the frame is primed differs from the probe. The
freeze and the misplaced halo do not depend on that distance, only its size does.

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
