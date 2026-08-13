# Ray glow tuner

Live preview for the rays glow, so its parameters can be judged by eye instead of by rebuilding the
plugin and scrolling a grid.

```powershell
.\build.ps1            # compiles and launches
.\build.ps1 -NoRun     # compiles only
```

It draws through the plugin's own `RayTrackBuilder` and `RayArrowLayout`, compiled straight from
`source\`, so the geometry, the silhouette tracing and the wave are the real ones rather than a
lookalike. Only the fill ladder and the draw loop are restated here, because those live in files that
drag in the whole plugin; the ladder starts out holding exactly the shipped numbers.

It does not reference `source\bin\Debug`, so it still builds while the rest of the plugin is mid-edit.

## Using it

Four stand-in subjects are shown by default — a square icon, a 2:3 cover, a cutout shape and a small
compact-list icon — because most parameters look different on each. `Load artwork...` adds a real
image, which is the only way to judge cutout art properly; the traced-silhouette path behaves quite
differently from the rounded rectangle that opaque icons fall back to.

The readout under the preview reports, per subject, the reach range, how slender the rays are, the gap
between neighbours, and how much of that gap the readable and the hazy parts of a ray occupy. It calls
out `RAYS RUN TOGETHER` when copies bright enough to define a ray start overlapping their neighbours.

`Copy values to clipboard` writes the current numbers back out as the C# they came from, including the
generated ladder arrays, ready to paste into `RayArrowLayout.cs`, `RayTrackBuilder.cs` and
`RarityAppearanceHelper.RayGlow.cs`.

`--snapshot <path> [laps]` renders one frame to a PNG and exits, for checking it without watching it.

## How the constants become sliders

The tuning numbers are `const` in the shipped source, and a slider cannot move a `const`. `build.ps1`
copies the two files into `obj\` with those specific constants rewritten as static fields, and compiles
the copies. Nothing in the repository is modified, and the plugin keeps its constants.

The rewrite matches each constant by name. Renaming one in the plugin without updating the list in
`build.ps1` fails the build with a message naming the constant, rather than quietly tuning something
that no longer exists.
