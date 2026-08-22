# KOTOR dialogue voice and facial-performance contract

## Evidence baseline

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- Dialogue: `end_trask01` from `end_m01aa`.
- First visible Trask entry sound: `nm01aatras02000_`.
- Playable installed payload: MP3, 34,296 bytes, SHA-256
  `03F0CC7439456BE65148A9306B18086B8F323C1E84A8CDD1AF774A981BB84457`.
- Matching LIP source: 51 frames, 5.579 seconds, SHA-256
  `CFB037F36AE671696E3AD24FA7308FD7E2CD8823D036C56804DFCD71899290CD`.
- Godot-decoded MP3 duration: 5.558 seconds. The 0.021-second difference from
  the LIP length is retained as contradictory timing evidence, not hidden.

## Resource contract

The DLG sound ResRef selects both voice and lip resources. Audio is resolved
through Override, StreamWaves, StreamSounds, then KEY/BIF precedence and is
converted only to its playable MP3/WAV payload. The matching LIP is read from
the module localization capsule. Generated audio and LIP JSON remain in the
ignored machine-local bundle and are never published.

The current `end_trask01` import resolves 50 nodes with both voice and LIP data.
Each manifest record carries its playable-audio hash, LIP-source hash, byte
count, duration, and frame count.

## Facial contract

`S_Male02` supplies a 0.5-second `talk` animation with 16 indexed mouth shapes.
For a LIP key shape `s`, its source pose time is:

```text
shape_time = clamp(s, 0, 15) / 16 * talk_length
```

At voice time `t`, the engine locates the surrounding LIP frames and linearly
interpolates their position samples and spherically interpolates their rotation
samples. The seven participating facial bones are upper mouth, left/right mouth
corners, jaw, left/right lower mouth, and tongue tip. Imported position tracks
are absolute local positions and replace the facial bone position; adding the
rest position a second time visibly stretches the mouth and is rejected.
Quaternion endpoints are normalized before slerp, with identity used only for a
zero-length sample.

The facial overlay runs through Godot's supported post-animation
`SkeletonModifier3D` stage, after `tlknorm`. Starting another line replaces the
voice/LIP state. Finishing a line removes the modifier influence and returns the
speaker to `pause1`. User response buttons remain disabled while the current
voice is playing.

## Verification and confidence

Runtime telemetry confirms:

- four inherited clips including `talk`;
- seven bound face bones and 14 talk tracks;
- voice start with decoded duration and LIP frame count;
- changing left/right shapes and interpolation factors throughout the line;
- clean voice completion and `pause1` restart after all 51 frames;
- distinct, non-stretched facial captures at separate voice times.

Resource selection, timing frames, face-bone binding, full-line completion, and
cleanup are `confirmed`. Exact MP3 decoder latency and retail facial blend order
are `probable` until compared against a hash-bound retail frame/audio trace.
The verified opening DLG has zero explicit animation records, so default talk is
the source-backed behavior here. Gesture-ID mapping in other conversations and
script-forced multi-speaker interruption remain pending experiments.
