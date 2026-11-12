# WallInk — a mixed-reality math whiteboard for Meta Quest 3

Turn a wall, whiteboard or desk you choose into a virtual writing board seen through Quest 3 passthrough, and write on it with a controller, a fingertip, or (experimentally) an ordinary pen. Freehand 3D drawing is included.

## Read this first: what has and has not been verified

| | Status |
|---|---|
| **Implemented** | Everything described in this README. |
| **Verified in Unity 6000.5.6f1 (headless)** | Package resolution and import; compilation of all five WallInk assemblies plus the Meta SDK and MRUK with no errors; all three shaders compiled for Android/Vulkan with no errors; the setup command ran with all six steps OK and generated the scene; 55 / 55 EditMode tests pass in Unity's test runner; an Android IL2CPP ARM64 build succeeded (`Builds/WallInk.apk`, 60 MB) and its manifest carries passthrough, hand-tracking, scene and anchor entries for Quest 3 / 3S. |
| **Not verified** | The app has never been run: not in the editor's Play mode (headless only) and not on a headset. Nothing visual has been seen. Meta's Project Setup Tool has not been reviewed. |
| **Not tested on a Quest 3** | Nothing. No headset test, frame-rate measurement, latency measurement or accuracy result exists. Every accuracy number below is a provisional target, not a result. |

A build that compiles and packages is not a build that works. Expect first-run issues (layout, text size, passthrough compositing, input mapping) that only show up in the headset.

## Environment

| Item | Value |
|---|---|
| Unity | 6000.5.6f1 (the only editor installed; it is not an LTS release — Meta SDK 209 declares Unity 6000.0 as its minimum) |
| Render pipeline | URP 17.5.0 |
| XR | Unity OpenXR Plugin 1.17.1 + XR Management 4.5.4, with the Meta XR feature set |
| Meta | `com.meta.xr.sdk.core` 209.0.0, `com.meta.xr.mrutilitykit` 209.0.0 (scoped registry `https://npm.developer.oculus.com`) |
| Input System | 1.20.0 |
| Target | Quest 3 / 3S, Android ARM64, IL2CPP, Vulkan, min API 32 |
| Rig / input stack | One: `OVRCameraRig` + `OVRInput` + `OVRPlugin` hand state. No Interaction SDK, no XR Interaction Toolkit. |

Android Build Support is installed for this editor (`adb` ships inside it under `Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools`). No headset was connected during development.

## Setup (beginner steps)

1. **Sign in to Unity.** Open Unity Hub, sign in, and check *Preferences > Licenses* shows an active license.
2. **Android support** is already installed for 6000.5.6f1 (if you move machines: Unity Hub > *Installs* > gear icon > *Add modules* > **Android Build Support** with OpenJDK and SDK & NDK Tools).
3. **Open the project.** Unity Hub > *Add* > select this folder. The first open downloads the Meta packages (about 200 MB) and takes several minutes. If Unity asks to enable the Meta XR feature set or restart for the Input System, accept.
4. **Setup has already been run** (the scene `Assets/WallInk/Scenes/WallInk.unity` exists). To redo it, use the menu **WallInk > Setup Project and Scene**. It configures player settings, URP, OpenXR and Meta features, the Meta project config, and generates `Assets/WallInk/Scenes/WallInk.unity`. Read the report it shows; each step says OK or FAIL. Restart the editor if asked.
5. **Check Meta's own tool.** *Meta > Tools > Project Setup Tool*: fix anything listed as required for Android.
6. **Run the tests.** *Window > General > Test Runner > EditMode > Run All* (55 tests).
7. **Try it without a headset (optional).** **WallInk > Create Editor Simulation Scene**, press Play. See "Editor simulation" below.
8. **Deploy.** Put the Quest 3 in developer mode (Meta Horizon phone app > headset settings > Developer Mode), connect by USB-C and accept the prompt in the headset. *File > Build Profiles > Android > Switch Platform*, open the `WallInk` scene, then **Build And Run** (or **WallInk > Build Android APK**, which writes `Builds/WallInk.apk`).

Command-line equivalents, once the license works:

```bash
"C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -projectPath . -executeMethod WallInk.EditorTools.WallInkSetup.SetupBatch -logFile setup.log
```

```bash
"C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -projectPath . -buildTarget Android -executeMethod WallInk.EditorTools.WallInkSetup.BuildAndroid -logFile build.log
```

## Using the app

### Flow

1. Launch: you see your room through passthrough, with the palette floating in front of you.
2. **Board** tab > **Create Whiteboard**, then pick a method:
   - **Detected** — point at a wall or table from the headset's room scan and press the draw control. Approximate (centimetres). If there is no scan, the app says so and offers Room Setup; if permission is denied, use a manual method.
   - **3+ points** — touch the tip to the surface and press the draw control at three or more points, at least 20 cm apart and not in a line; 5–8 spread points are better. Press **Finish**. Collinear, bunched or non-flat points are rejected with a reason. This is the most precise method.
   - **Pointer** — the board hangs on your pointer ray (thumbstick changes distance); press to drop it.
3. **Adjust**: compare the blue outline, centre cross and normal arrow with the real surface. Use Out/In (depth, the important one), Left/Right/Up/Down, Yaw/Tilt/Roll, W/H, and **Step** (fine = 1 mm / 0.2°). Press **Confirm Alignment**.
4. **Calib** tab:
   - **Tip calibration** (controller, pen): rest the tip on one fixed spot and rock the handle in a wide cone for 6 s. Solves where the tip is relative to the tracked body.
   - **Touch offset** (all inputs): rest the tip on the board for 1.5 s. Stores the height that input reports at contact.
   - **Pen quick calibrate** (pen only): put the pen tip on the orange cross.
5. **Accuracy check**: touch each highlighted cross (5 targets × 3). **Jitter test**: hold still 4 s. **Writing guide**: 20, 10 and 5 mm ruled lines to judge small handwriting by eye. Reports go to the Info tab and to `wallink-accuracy-*.txt` in the app's data folder.
6. **Draw** tab: write. Pen, Straight line (snaps to horizontal/vertical within 4°), Eraser (whole stroke), Undo, Redo, Clear (hold), colours, width in mm, grid/ruled background.

### Controls

| | Controller | Fingertip | Pen (experimental) |
|---|---|---|---|
| Writing point | Virtual tip (default 6 cm ahead of the controller; calibrate it) | Tracked index fingertip joint | Estimated from thumb tip, index tip and index knuckle |
| Draw control (Explicit mode) | Hold index trigger | Pinch thumb to middle finger (changeable: index pinch, or other-hand pinch) | Pinch thumb and index of the other hand |
| Contact-like mode | Ink when the tip is within the contact distance of the board | same | same |
| Palette | Point and pull trigger | Poke a button with either index finger | Poke with the other hand |

B or Y collapses/expands the palette; the left menu button summons it in front of you or returns it to its dock beside the board. The palette docks on the left of the board (Input > Palette side to change), so it never covers the writing area, and pointing at it never makes ink.

**Explicit vs Contact-like** (Draw tab, "Ink:"). Explicit is the default and the fallback: depth noise cannot start or break strokes. Contact-like infers contact from the tip's distance to the calibrated plane with separate enter (4 mm) and exit (10 mm) thresholds, adjustable in millimetres on the Calib tab; run Touch offset first.

**Air mode** (Draw > Mode): freehand 3D ribbons while the draw control is held.

**Realign Board** (Board tab) pauses writing and re-enters adjustment. Ink is stored in board-local coordinates, so it moves with the board and is never distorted. **Save / Load Board** stores strokes and, when available, a spatial anchor; a loaded board is shown in orange and stays read-only until you confirm it still matches the surface.

### Editor simulation

Mouse aims; left button = trigger; right button or Space pushes the tip onto the board; P = pinch; hold L = tracking loss; R = recenter; WASD/QE move and turn; arrows = thumbstick; Z/X/M = primary/secondary/menu.

## Accuracy: what is promised and what is not

Nothing here is millimetre-accurate by promise. Errors come from separate sources, handled separately:

| Error source | What addresses it | What cannot be fixed in software |
|---|---|---|
| Surface-plane alignment | Multi-point plane fit, visual overlay, 1 mm / 0.2° adjustment | Room-scan planes are centimetre-grade; a 3-point fit inherits tip error at each touch |
| Writing-tip position | Pivot calibration (controller, pen), touch offset, pen quick calibration | An ordinary pen is not tracked: its tip is an estimate that breaks when the grip changes or fingers are hidden |
| Tracking noise | Light One Euro filter (off / light / medium) | Smoothing hides jitter only; it is deliberately not used to hide offsets |
| Filtering delay | Speed-adaptive cutoff; raw samples kept so rendering can be re-derived | Any smoothing adds some lag |
| Drift / relocalization | Spatial anchor, alignment state, Realign | Anchors reduce drift; they do not eliminate it |

Provisional engineering targets (to be measured on device and revised; none has been measured):

| Target | Value | How to measure |
|---|---|---|
| Plane fit residual (≥5 points) | ≤ 2 mm RMS | Shown after 3+ point placement |
| Repeatability, controller / fingertip / pen | ≤ 2 / 4 / 6 mm RMS | Accuracy check |
| Still-tip jitter in plane | ≤ 1 mm RMS | Jitter test, hand rested on the surface |
| Tip calibration consistency | ≤ 3 mm RMS | Shown after Tip calibration |
| Added smoothing lag at 5 cm/s | ≤ 15 ms (theoretical: about 12 ms on "light") | Computed from filter settings; not an end-to-end measurement |
| Visible tip-to-ink offset | ≤ 3 mm controller, ≤ 5 mm fingertip | By eye with the writing guide; no instrument in the app can measure this |

Repeatability is not accuracy: the check targets are virtual, so a board 5 mm off the wall still scores well. Physical alignment is judged by eye against the overlay. End-to-end latency needs external equipment (for example a high-speed camera through the lens) and is not measured by the app.

### Known limitations

- **Pen input is experimental.** The tip is a fixed offset in a frame built from three hand landmarks. Holding a pen hides parts of the fingers from the cameras, finger motion within the grip is only partly captured, and re-gripping invalidates the calibration (the app detects large changes and stops writing). Contact and pressure are never measured.
- **Fingertips near a wall** are often occluded or reported with low confidence; the stroke then ends and must be deliberately restarted.
- **Controller tip** is a virtual point until calibrated against the part of the controller you actually touch the surface with.
- **Detected surfaces** come from Space Setup and are only a starting point.
- **Stroke width** is constant. There is no pressure and no speed-based width.
- **Saved boards** keep rendered points only (not raw samples) and board strokes only (not air strokes).
- Budgets: 6000 strokes / 1.5 M vertices per canvas, 4000 points per stroke (longer strokes continue seamlessly in a new stroke), 200 undo steps.

## Architecture

Four assemblies, dependencies pointing one way:

```
WallInk.Core    pure logic, no scene or SDK types; unit-tested outside Unity
   ^
WallInk.App     MonoBehaviours, rendering, UI, flow; talks to XR only via Core interfaces
   ^
WallInk.MetaXR  the only code that touches Meta SDK types (three small adapters)
WallInk.Editor  setup command and build entry points
```

| Component | Responsibility |
|---|---|
| `BoardFrame` | Board coordinates: X right, Y up, Z along the normal (local +Z into the surface; height = −Z, positive toward the writer). Metres. |
| `PlaneFit`, `PivotCalibration` | Least-squares plane fit with degeneracy rejection; tip-offset solve with residual. |
| `SurfaceStrokeCapture`, `ContactDetector` | Per-frame inking decision: hysteresis, bounds, jump / stale / implausible rejection, mandatory re-arm after any interruption. |
| `StrokeBuilder`, `OneEuroFilter` | Raw samples kept separately; distance-based sampling (0.4 mm); light smoothing; taps stay single-point dots. |
| `StrokeHistory` | Undo/redo for draw, erase and clear; a new action invalidates redo. |
| `StrokeMeshBuilder`, `ProceduralStrokeRenderer` | Constant-width ribbons with miter/round joins and round caps; bounded mesh chunks; reused buffers; parallel-transported ribbon normal for air strokes. |
| `IStylusInputProvider` + `Controller/Hand/CalibratedPen…Provider` | Tip pose, validity, confidence, draw state, timestamp, analog value with stated meaning. A tracked stylus would be one more provider. |
| `IXRDevice`, `ISurfaceScanner`, `IBoardAnchor` | Platform seam. `MetaXRDevice`, `MetaSurfaceScanner` (MRUK), `MetaBoardAnchor` (OVRSpatialAnchor) implement it; `SimulatedXRDevice` stands in when none is present. |
| `DrawingManager` | Samples → capture → strokes → history/renderers; cursors; dropout and frame-time counters. |
| `WhiteboardSurface` | The board transform, backing, grid, outline, alignment state. |
| `SurfacePlacementController`, `TipCalibrationController`, `AccuracyDiagnostics`, `BoardPersistence` | Surface calibration; tip calibration; accuracy check; save/load. |
| `DrawingPaletteController`, `UiKit` | World-space palette hit-tested analytically (ray, poke, mouse); no UI framework dependency. |
| `AppController` | Flow, wiring, anchoring, tracking-disruption handling. |

Shaders (`Assets/WallInk/Resources/WallInk`): `Ink` (vertex colour, edge anti-aliasing, painter's order on the board so coplanar strokes never z-fight; depth-writing variant for air), `Board` (translucent backing with metric grid), `Text`. All URP unlit with single-pass-instanced stereo macros. Materials are created at runtime, so there is nothing to wire in the inspector.

Safety behaviour: recenter, tracking-origin change, tracking loss/recovery and headset remount end the stroke and, if the board has no located anchor, mark alignment uncertain and pause writing until you confirm or realign. System menu, focus loss and app pause end the stroke. A large anchor correction ends the stroke before the board moves. The plane is never refitted while writing.

## Validation results

In Unity 6000.5.6f1, headless (batch mode), on Windows 11:

- **Import and compile**: clean. Two manifest mistakes were found and fixed on the way: `com.unity.modules.vr` does not exist in this editor, and the Meta SDK needs built-in modules (AssetBundle and others) that had been trimmed.
- **Setup command** (`SetupBatch`): all six steps OK. A bug that left empty `XR 1/2/3` folders was fixed afterwards; the fixed version compiled but the command was not re-run.
- **Unity Test Runner, EditMode**: 55 / 55 pass. One test failed during development and exposed a real defect: the first smoothing defaults rounded a 4 mm corner by 0.62 mm. The filter was retuned and the test tightened to 0.4 mm.
- **Shaders**: `WallInk/Ink`, `WallInk/Board`, `WallInk/Text` compiled in the Android build with no errors.
- **Android build**: succeeded, 0 errors. APK package `com.wallink.whiteboard`, arm64-v8a, supported devices `quest3|quest3s`, permissions `HAND_TRACKING`, `USE_SCENE`, `USE_ANCHOR_API`, feature `PASSTHROUGH` required.
- **Before Unity was available**: every script was also type-checked with Roslyn against the SDK sources, with a negative control; that predicted the clean compile.
- **Not done**: Play mode, missing-reference scan in a running scene, Meta Project Setup Tool review, install on a device, any device test.

Things to watch: Meta SDK 209 on the non-LTS 6000.5 editor is a combination Meta does not list; the build targets the newest installed Android API (36) rather than 34, which is fine for sideloading but not for store submission.

## On-device checklist (all still to do)

- [ ] Passthrough visible at launch; palette readable in both eyes
- [ ] Vertical wall: all three placement methods; outline sits on the wall after adjustment
- [ ] Horizontal desk: 3+ points placement; "up" is away from you; contact-like writing
- [ ] Tilted surface (drafting board, propped clipboard)
- [ ] Handwriting at 20, 10 and 5 mm (Writing guide); is 5 mm legible with each input?
- [ ] Dots and decimal points, minus signs, fraction bars, equals signs, exponents, subscripts, closely spaced symbols
- [ ] Slow strokes (no wobble) and fast strokes (no dropped segments, no false "tracking jumped")
- [ ] Board edges: stroke ends cleanly; re-entry starts a new stroke; no line across the gap
- [ ] Occlusion: hide the hand or controller mid-stroke; stroke ends; nothing connects on recovery; re-arm works
- [ ] Input switching: controller ↔ fingertip ↔ pen, and left ↔ right hand, mid-session
- [ ] Palette: ray and poke; pressing buttons never inks; Clear needs a hold; collapse and summon
- [ ] Undo/redo across draw, erase, clear
- [ ] Recenter (hold the Meta button): stroke ends; board state is correct with and without an anchor
- [ ] Take the headset off and put it back on; leave and re-enter the room
- [ ] Realign Board with ink present: ink moves with the board
- [ ] Save, quit, relaunch, Load: board appears orange, read-only until confirmed; position correct
- [ ] 20–30 minute session: visible drift? record Accuracy check and Jitter test at start and end
- [ ] Performance: frame time on the Info tab with a full board; profile with OVR Metrics Tool

## Next steps to improve accuracy

1. Install the built APK on a headset and fix whatever the first run reveals.
2. Run the accuracy and jitter checks per input, record the numbers, and replace the provisional targets with measured ones.
3. Tune from data: contact thresholds, smoothing preset, grip tolerance, jump limits.
4. If fingertip or pen results miss the handwriting target, keep them as secondary modes and add a tracked stylus (for example Logitech MX Ink) as a fourth `IStylusInputProvider`; it reports a real tip pose and pressure.
5. Measure end-to-end latency externally; consider drawing the live stroke tail from a late-latched pose if it is visible.
6. Consider a multi-touch tip check against a physical ruler or printed grid to estimate absolute in-plane error, which the current check cannot.
