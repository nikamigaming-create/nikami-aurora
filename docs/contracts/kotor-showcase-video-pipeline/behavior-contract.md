# KOTOR one-output showcase video pipeline contract

## Purpose

`Export-KotorShowcaseVideo.ps1` is the only authorized final-recording entry
point for the current proof. It records the merged startup-to-action route from
the active OpenXR spectator and leaves exactly one user-visible MP4.

The pipeline is source-only. It never publishes owned game assets and never
adds the generated movie to Git.

## Preconditions

The wrapper refuses to start unless:

- the requested final path ends in `.mp4` and does not already exist;
- Godot 4.6.3 .NET, Meta XR Simulator, FFmpeg, and FFprobe are available;
- the owned local module manifest already exists; and
- the launch uses `-OpenXRSimulator`, `-ShowcaseRoute`, and clean spectator
  presentation.

`Start-KotorGodot.ps1 -MoviePath` separately requires both the showcase route
and OpenXR simulator. It accepts only Godot Movie Maker `.avi` or `.ogv`
  intermediates. The real Godot executable is launched with a
  `ProcessStartInfo.ArgumentList` and `WaitForExit()` so arguments remain
  correctly bounded and recording cannot race a detached GUI process.

## Recording flow

1. Resolve and validate an exact GUID-named directory beneath the system temp
   root.
2. Run Godot Movie Maker at the requested fixed FPS, active Meta XR Simulator,
   Vulkan mobile renderer, and HMD-following root spectator.
3. Exit only when `NIKAMI_AURORA_SHOWCASE status=pass` has been emitted.
4. Require the intermediate to exist and exceed 1 MiB.
5. Reject the runtime log unless it contains active OpenXR, spectator,
   transmission, first-encounter, and complete-route pass telemetry, with no
   `ERROR`, `status=fail`, or desktop fallback.
6. Encode one temporary MP4 with H.264 CRF 18, `yuv420p`, AAC 192 kb/s, and
   fast-start metadata.
7. Require exactly one 1280×720 video stream, at least one audio stream, and a
   duration from 90 to 150 seconds.
8. Move the validated MP4 to the previously absent destination and report its
   SHA-256, byte count, duration, codecs, geometry, and FPS.
9. Validate the temporary path again and recursively remove the GUID directory,
   including the Godot intermediate.

If any step fails, the wrapper removes a partial destination that it created.
It never overwrites an existing output.

The foreground Godot console is also captured. Godot 4.6.3 emits exactly two
known diagnostics after Aurora reports its post-draw OpenXR shutdown request: two
interaction-profile RIDs at engine exit and a spatial-entity signal disconnect.
The wrapper allowlists only those exact post-shutdown signatures and requires
exactly two occurrences. Any earlier, additional, or changed `ERROR:` line
fails recording. These are engine teardown diagnostics after route/movie
completion, not suppressed runtime failures.

## Filesystem safety

Recursive cleanup is allowed only when the resolved target:

- starts with the resolved system temporary root; and
- has a leaf name matching `nikami-aurora-showcase-*`.

The repository, workspace root, user profile, game install, and caller-selected
output directory can never become recursive cleanup targets.

## Video authorization gate

Adding this wrapper does not authorize a recording by itself. Before the single
final invocation, current merged `main` must pass:

- fresh owned import;
- Release build, formatting, .NET acceptance, and Python audio tests;
- desktop opacity, face/LIP, audio, and complete-route gates;
- no-HMD OpenXR fallback;
- active Meta XR Simulator route with non-black spectator output; and
- zero open PRs and no tracked private media.

No AVI, OGV, or MP4 was generated while implementing this pipeline.
