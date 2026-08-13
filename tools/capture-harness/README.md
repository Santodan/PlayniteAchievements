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

`freezeAt`/`freezeFor` stop the window painting mid-recording, which is what a game that stops presenting
looks like to the capture. Wired but not yet exercised.

Per-frame offsets are written to `alignment_*.csv` next to the executable.

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

`worstX` is the verdict: the worst frame interval as a multiple of **that run's own median**. This is
deliberately not measured against the display's rate — moving a per-pixel-alpha window costs a full
redirection-surface blit, so the slide sustains an even ~82 Hz on a 165 Hz panel and cannot do better.
Uniform coarseness reads as smooth; one interval far out of line reads as a jump. The defect runs to
10x or more, ordinary jitter and the ray driver's own 30 fps redraws to two or three, so the threshold
is 4x.

`maxStep` is that gap's visible cost in pixels. Useful, but not the verdict: because `BackEase` is steep
early, an identical gap costs far more travel at the start of the slide than at the end.

Two earlier versions of this probe were wrong in ways worth not repeating. Injecting the cost into the
slide's first frame rather than the window's penalised every mode equally, so no warm ordering could ever
win. And deriving the "ideal" step from the run's own observed mean interval is circular — it hands a
starved slide a lenient target, and a three-frame slide passes.

### What it established

- A window at `Opacity=0` **does** rasterize its content: the transparent warm absorbs the whole injected
  first-paint cost (first gap 121 ms → 6 ms), so the near-transparent variant is unnecessary.
- The first storyboard resolve of a session costs **90–130 ms** on the UI thread, on the frame the first
  slide subscribes on. That is the first-notification jank.
- Later resolves cost only ~1.4 ms, so memoizing the dictionary does **not** explain a janky slide-out;
  look to the save pipeline's allocation churn instead, via the `[Toast] Slide out` log line.

### Limits

The card is toast-shaped — layered, chromeless, sized to content, carrying the same shadow and blur
effects — but it is not the real template bound to a real view model, so absolute first-paint costs are
lower than the live toast's. It measures the mechanism, not the card.

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

## Limits

- Runs against a GDI window at whatever size the window is, not a real game at 2560x1440. Anything that
  depends on the capture pump saturating may not reproduce here.
- The harness drives the plugin's internal types by reflection, so renaming them breaks it silently at
  runtime rather than at build time.
- Long-session behaviour is only sampled: a five-minute run showed no cumulative drift, but sessions run
  far longer than that.
