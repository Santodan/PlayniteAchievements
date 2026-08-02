# WGC capture feasibility spike

Standalone `net462` console app that proves (or disproves) Windows.Graphics.Capture per-window capture for the plugin.
It captures a chosen window — the point is capturing it while it is **occluded or unfocused** — to a PNG, tone-maps HDR to SDR, and logs diagnostics against the go/no-go gates in `docs/notes/hdr-occlusion-capture.md`.

This project is deliberately separate from the plugin so its WinRT/D3D references cannot break the plugin build.
The plugin stays `net462`; this spike stays `net462` too (proving that is the first gate).

## Build

Open `prototype/WgcCaptureSpike/WgcCaptureSpike.csproj` in Visual Studio 2022 (or `dotnet build -c Debug`).
It restores two NuGet packages:

- `Microsoft.Windows.SDK.Contracts` — the WinRT projections (`Windows.Graphics.Capture`, `Windows.Graphics.DirectX`). The version (`10.0.19041.0`) must be a Windows 10 SDK you actually have; change it if restore fails.
- `Vortice.Direct3D11` — the D3D11 device + texture read-back. Adjust the version to whatever recent `3.x` restores.

If the WinRT projection types (`GraphicsCaptureItem`, `Direct3D11CaptureFramePool`, `DirectXPixelFormat`) do not resolve, that is the classic-.NET-Framework WinRT-reference wrinkle — see "If it does not build" below.

## Run

Must run as an **x64** process.

Capture a specific window (best for the occlusion/unfocus tests):

```
WgcCaptureSpike.exe --title "Elden Ring" --hdr auto --out shot.png
```

- To test **occlusion (gate 2)**: start the capture pointed at the game by title, then cover the game with another window (or have it already covered). The PNG should still show the game, not the covering window.
- To test **unfocused (gate 3)**: keep another window focused while capturing by title. The log prints whether the target was FOCUSED or UNFOCUSED.

Capture the foreground window after a countdown (alt-tab to / cover the target during it):

```
WgcCaptureSpike.exe --foreground --delay 5 --hdr auto
```

Options:

- `--hdr auto|on|off` — `auto` uses `HdrDisplayDetector` on the target's monitor; `on`/`off` force the float/8-bit path.
- `--white <float>` — manual Reinhard white point for HDR tone-mapping (default: auto = measured peak). Tune this while eyeballing the PNG.
- `--out <path>` — output PNG (default `wgc-capture.png`).

## Go/no-go gates (record results in the planning doc)

1. **net462 interop** — does activation + `CreateForWindow` succeed from this net462 exe? An `InvalidCastException` / "interface not supported" at `CreateForWindow` means gate 1 FAILED → WGC is a no-go, use Branch B. Hard requirement (no TFM bump).
2. **Occluded** — captures a window covered by another window.
3. **Unfocused** — captures a window that is not focused.
4. **D3D game / emulator** — captures real game content (not black). Test an actual game and an emulator.
5. **HDR** — with `--hdr auto` on an HDR display, the log's "max linear channel" is > 1.0 (real HDR content) and the PNG is correctly exposed (not blown out). Toggle Windows HDR off and confirm `auto` reports SDR.
6. **Border** — the yellow capture border is not in the PNG; the log says whether `IsBorderRequired=false` was accepted (needs Win11/build 20348+).
7. **Latency** — logged elapsed for one setup→frame→teardown, target < ~500 ms.
8. **OS build** — logged; need Windows 10 1903+ (build 18362+).

## If it does not build (WinRT references on classic .NET Framework)

Two known-good approaches; try the packaged one first (already configured):

1. `Microsoft.Windows.SDK.Contracts` PackageReference (this project). Simplest when it restores.
2. If (1) fails on your SDK, reference the winmd directly: add to the csproj
   `<PropertyGroup><TargetPlatformVersion>10.0.19041.0</TargetPlatformVersion></PropertyGroup>` and a
   `<Reference Include="Windows" />` plus `System.Runtime`, `System.Runtime.WindowsRuntime`,
   `System.Runtime.InteropServices.WindowsRuntime`. This is the same wiring the **plugin** will need
   in Branch A (the plugin is old-style packages.config, so it uses this path, not the NuGet).

Vortice may pull `System.Memory` / `System.Buffers` / `System.Runtime.CompilerServices.Unsafe`; `AutoGenerateBindingRedirects` (set) handles the net462 redirects.

## Files

- `NativeInterop.cs` — P/Invoke + the two COM interfaces (`IGraphicsCaptureItemInterop`, `IDirect3DDxgiInterfaceAccess`) + DisplayConfig structs.
- `HdrDisplayDetector.cs` — OS per-monitor HDR detection (copied to `source/Services/Recording/HdrDisplayDetector.cs` in Branch A).
- `WgcCapture.cs` — the one-shot WGC capture + HDR read-back/tone-map.
- `Program.cs` — CLI, window resolution, gate logging.
- `Hr.cs` — HRESULT check helper.

## Likely tuning / adjustment points

- Vortice API surface (`Texture2DDescription` field names, `Map`/`CopyResource` signatures) varies slightly by version — the compiler will point at any mismatch.
- The HDR tone-map operator (extended Reinhard + sRGB OETF) is a starting point; `--white` and the operator itself need eyeballing on real HDR hardware.
- `DISPLAYCONFIG_MODE_INFO` is treated as an opaque 64-byte struct (we never read its union); if `QueryDisplayConfig` returns `ERROR_INSUFFICIENT_BUFFER` repeatedly, the struct size is off for your SDK.
