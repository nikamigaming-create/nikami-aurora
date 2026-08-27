# KOTOR one-output showcase video pipeline contract

## Purpose

`Export-KotorShowcaseVideo.ps1` is the only authorized final-recording entry
point for the current proof. It records the merged startup-to-action route from
the desktop presentation by default and leaves exactly one user-visible MP4.
The same wrapper can record the active OpenXR simulator spectator when
`-Presentation OpenXRSimulator` is explicitly requested.

The pipeline is source-only. It never publishes owned game assets and never
adds the generated movie to Git.

## Preconditions

The wrapper refuses to start unless:

- the requested final path ends in `.mp4` and does not already exist;
- Godot 4.7.1 .NET, FFmpeg, and FFprobe are available;
- the owned local module manifest already exists; and
- the launch uses `-ShowcaseRoute` and clean presentation. Meta XR Simulator is
  additionally required only for the explicitly selected simulator path.

`Start-KotorGodot.ps1 -MoviePath` requires either the showcase route or the
isolated first-encounter diagnostic. It accepts desktop or OpenXR Simulator
recording, but rejects a nondeterministic live-headset movie. It accepts only
Godot Movie Maker `.avi` or `.ogv` intermediates. The real Godot executable is
launched with a `ProcessStartInfo.ArgumentList` and `WaitForExit()` so arguments
remain correctly bounded and recording cannot race a detached GUI process.

## Recording flow

1. Resolve and validate an exact GUID-named directory beneath the system temp
   root.
2. Run Godot Movie Maker at the requested fixed FPS in the selected desktop or
   Meta XR Simulator presentation.
3. Exit only when `NIKAMI_AURORA_SHOWCASE status=pass` has been emitted.
4. Require the intermediate to exist and exceed 1 MiB.
5. Reject the runtime log unless it contains the selected presentation state,
   room-emitter, transmission, first-encounter, and complete-route pass
   telemetry. Desktop allows no console error; simulator mode allows only its
   exact post-shutdown engine diagnostics and rejects desktop fallback.
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

The foreground Godot console is also captured. Desktop recording requires zero
`ERROR:` lines. Godot 4.7.1 simulator recording emits exactly two
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
- desktop route with OpenXR explicitly disabled;
- no-HMD OpenXR fallback;
- active Meta XR Simulator route with non-black spectator output; and
- zero open PRs and no tracked private media.

The first authorized Godot 4.6.3 attempt used AVI and completed every route
gate, but the writer raised a native C++ exception while finalizing after frame
6,152. A second 4.6.3 attempt used OGV and again completed the route before
returning the same native exit code after the post-draw shutdown request. The
wrapper removed both incomplete outputs and both temporary directories, proving
that the AVI 4 GiB limit was not the complete cause.

The recording stack is therefore pinned to the ABI-matched Godot engine and
`Godot.NET.Sdk` 4.7.1. Its active-XR OGV close gate produced a 9.4-second,
1280×720 Theora/Vorbis file and exited normally before that one private test
directory was deleted. The pipeline keeps OGV quality 0.9 and audio quality 0.8
for its private intermediate; the validated public result remains H.264/AAC MP4.
