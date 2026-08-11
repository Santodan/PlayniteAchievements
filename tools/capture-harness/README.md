# Capture harness

Diagnostic tools for the unlock-clip pipeline: recording, clip export, toast compositing, and unlock
screenshots.

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
