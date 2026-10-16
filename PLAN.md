# Push Button – Implementation Plan

Work through the **Steps** section one step at a time, ticking each box when the step and its check are done. The **Design reference** sections at the bottom hold the detail the steps point to.

## Goal

A reusable UdonSharp component that turns any object into a physically pushable button:

- **VR players** push it with their avatar's fingertips. The button moves along its press axis and stops at a set depth. When it reaches that depth, it fires a configurable action.
- **Desktop and mobile players** click it (a trigger collider with `Interact()`). The button plays a short press-in/return animation and fires the same action.
- **Every player sees the button move, whatever their own platform.** There is no position sync. Each client works out the animation itself from the avatar bone data VRChat already sends, and a desktop click sends one small network event so other clients play the click animation.
- **Only the player who pressed it runs the action.** If the action should affect everyone, the target behaviour does its own networking.
- The **travel distance** is one Inspector value: the button stops at that depth and fires there.
- The **action** is a target UdonBehaviour plus a custom event name, both set in the Inspector.
- It works with **any object, at any rotation or scale**, under any parent. **Only the button itself is animated.** It never references or needs a backing plate.

---

## Steps

### Phase 1: Scaffolding

- [x] **1.1 Create the folder layout.**
  Create `Assets/PushButton/`, `Assets/PushButton/Editor/` and `Assets/PushButton/Examples/`.
  *Check:* the folders appear in the Unity Project window.
  Done: folders created, and a trigger `refresh` imported them (Unity generated `Assets/PushButton.meta`, `Editor.meta`, `Examples.meta`).

- [x] **1.2 Create an empty `PushButton` UdonSharpBehaviour.**
  Add `Assets/PushButton/PushButton.cs` and its generated `PushButton.asset` program asset. Set `[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]`, and add the `AnimationRange` / `AnimationRangeSqr` constants (see [Constants](#constants)).
  *Check:* UdonSharp compiles with no console errors.
  Done: `PushButton.cs` and a hand-written `PushButton.asset` (UdonSharpProgramAsset pointing at the script's GUID) were added. A trigger `refresh` logged `refreshed`, UdonSharp compiled 9 scripts with no errors, and it generated `Assets/SerializedUdonPrograms/7b3640e8ef5d4df6b37ca6274fffd821.asset` for the program. Unity hasn't written its compiled fields back into `PushButton.asset` on disk yet; that happens at the next asset save.

- [x] **1.3 Add all Inspector fields.**
  Add every field from [Inspector fields](#inspector-fields), with headers and tooltips. Wrap `debugFingertip` so it's editor-only in effect.
  *Check:* compiles. Adding the component to the child `Button` shows all the fields with their defaults.
  Done: all fields from the table were added under `Action` / `Travel` / `VR` / `Desktop` / `Debug` headers, with tooltips (plus `[Range(0, 1)]` on `releaseFraction` and `hapticStrength`). `debugFingertip` stays a normal serialized field, but `Start` clears it inside `#if !UNITY_EDITOR`. UdonSharp drops `UNITY_EDITOR` when it compiles for a build (`UdonSharpUtils.GetProjectDefines`), so uploaded worlds ignore it. `refresh` logged `refreshed`. UdonSharp only recompiled once `menu VRChat SDK/Utilities/Re-compile All Program Sources` was queued (see `TestingAutomation.md`), and it compiled 9 scripts with no errors. The component isn't in the scene yet (7.2 adds it), so the Inspector part of the check was done by reading the code, not visually.

- [x] **1.4 Create the example action target.**
  Add `Assets/PushButton/Examples/ButtonActionExample.cs` with `public void OnButtonPressed()`, which logs `"[ButtonActionExample] pressed by <local player name>"` and toggles an optional `GameObject`.
  *Check:* compiles.
  Done: added `ButtonActionExample.cs` (`NoVariableSync`, optional `toggleTarget` GameObject, logs `"[ButtonActionExample] pressed by " + displayName`, falling back to `<no local player>` if `Networking.LocalPlayer` is null) with a hand-written `ButtonActionExample.asset` program asset. `refresh` logged `refreshed`, and after `Re-compile All Program Sources` UdonSharp compiled 10 scripts with no errors. It generated `Assets/SerializedUdonPrograms/1d651c3ea55a415d905de79f00e85396.asset`, and this time Unity wrote the compiled fields back into `ButtonActionExample.asset`. (1.3 was already done in the previous run but hadn't been ticked; it is ticked now.)

### Phase 2: Rest pose and geometry

- [x] **2.1 Cache the rest pose.**
  In `Start`, store `restLocalPosition` and `restLocalRotation`. Add a helper that rebuilds `restWorldPos` and `restWorldRot` from the parent (or world space if there is no parent) each frame.
  *Check:* a temporary log prints sensible values. Moving the parent at runtime changes the rebuilt world rest pose.
  Done: `Start` caches the local rest pose and `Update` calls `UpdateRestWorldPose()` (parent `TransformPoint` / `rotation *`, or the local values if there is no parent). A temporary `[PushButton] rest local … rest world …` log in `Start` (marked `TEMP (plan step 2.1)`, to remove by 7.2) printed `(0, 0, 0.03)` local and `(0, 1, 0.03)` world. To test in Play Mode, the `PushButton` component was added to the child `Button/Button` (all Inspector values at their defaults, no target yet) and the scene was saved. Added `Assets/PushButton/Editor/PushButtonPlayModeTests.cs`, an editor-side Play Mode harness that later steps can extend: `menu Tools/PushButton/Run Tests On Next Play` arms it, then `play 120 [PushButtonTest] done` runs it. It reads the Udon variables with `GetProgramVariable`, moves the parent by (1, 0.5, -2) and rotates it to (35, 120, -70), and checks the rebuilt rest pose against the parent's transform. Result: `[PushButtonTest] done: 7 passed, 0 failed`, with 0 errors in Play Mode. It also has `menu Tools/PushButton/Add PushButton To Scene Button`, which does nothing if the component is already there.

- [x] **2.2 Compute the contact box.**
  Take the box from the button's own `BoxCollider` center and size, or from `MeshFilter.sharedMesh.bounds` if there is no box collider. Convert it to meters with `lossyScale`. From it, derive the **front face offset** along `localPressAxis` and the **footprint** half-extents on the two perpendicular axes. Normalize `localPressAxis`, and warn if it is zero.
  *Check:* for the scene button, the front face offset is 0.01 m and the footprint half-extents are 0.025 × 0.025 m.
  Done: `ComputeContactBox()` runs in `Start` after the rest pose. It stores `pressAxis` (normalized `localPressAxis`; a zero axis logs a warning and falls back to (0, 0, -1)), two perpendicular unit axes `lateralAxisU` / `lateralAxisV`, `frontFaceOffset`, and `footprintCenterU/V` plus `footprintHalfU/V`, all in meters in the rest frame (rotation only, scale applied via `lossyScale`). Arbitrary axes use the box's support distance along each axis. **Sign convention:** `frontFaceOffset` is positive, the distance from the rest position to the front face measured *against* `pressAxis` (the front face sits at `depth = -frontFaceOffset` when `depth = Dot(p, pressAxis)`). So in 5.3 the press formula `depth - faceOffset + radius` becomes `depth + frontFaceOffset + radius`. Added `AddContactBoxTest` to the Play Mode harness. Result: `contact box: face 0.0100, footprint 0.0250 x 0.0250, axis (0, 0, -1)`, `[PushButtonTest] done: 12 passed, 0 failed`, 0 errors. (The trigger's `play` saw the done line at once but only left Play Mode about 3 minutes later with "time up", which looks like a trigger quirk, not a test problem.)

- [x] **2.3 Apply a press amount.**
  Add `ApplyPress(float amount)`: `transform.position = restWorldPos + restWorldRot * (localPressAxis * amount)`. Add a temporary Inspector slider to drive it.
  *Check:* in Play Mode, the slider moves the button straight into the plate. It still moves along the correct axis after rotating the parent to (35, 120, -70).
  Done: `ApplyPress` uses the normalized `pressAxis` from 2.2 (not the raw `localPressAxis`), so a non-unit Inspector axis still moves exactly `amount` meters. The temporary slider is `debugPressSlider` (`[Range(0, 0.03)]`, under the Debug header), and `Update` calls `ApplyPress(debugPressSlider)` after rebuilding the rest pose. Both are marked `TEMP (plan step 2.3)`, to be removed in 3.1. Added `AddPressTest` to the Play Mode harness. It sets the slider to 0.02 with `SetProgramVariable`, checks the position against the formula and that the offset in the parent's space is (0, 0, -0.02), then sets it back to 0 and checks the button returns to rest. It runs once unrotated (button at the plate face, (0, 1, 0.01)) and once after the rest-pose test has rotated the parent to (35, 120, -70). Result: `[PushButtonTest] done: 21 passed, 0 failed`, 0 errors. The test drives the slider variable, not the Inspector by hand.

- [x] **2.4 Add gizmos.**
  `OnDrawGizmosSelected` (editor-only) draws the press axis arrow, the contact box at rest, and a wire box at full `travelDistance`.
  *Check:* with travel 0.019, the full-depth box sits about 1 mm in front of the plate face, at any parent rotation.
  Done: `OnDrawGizmosSelected` and a public `GetGizmoGeometry(...)` sit in a `#if !COMPILER_UDONSHARP && UNITY_EDITOR` block, so UdonSharp skips them and they run as plain C# on the editor-side component. It draws the rest box (cyan wire), the full-travel box (orange wire) and a yellow arrow along the press axis ending at the front face. The geometry comes from `ComputeContactBox`, which now takes a `warn` flag (so gizmo repaints don't spam warnings) and also stores `contactCenter` / `contactHalfExtents`. In Edit Mode the rest pose is the current transform; in Play Mode it's read from the Udon program's `restLocalPosition` / `restLocalRotation` plus the parent, so the boxes stay put while the button moves. Added `AddGizmoTest` to the harness (run unrotated and after rotating the parent to (35, 120, -70)): rest pose matches Udon's, rest box is 0.05 × 0.05 × 0.02 centred, and the full-depth front face is 1.00 mm in front of `ButtonPlate`'s face in both cases. Result: `[PushButtonTest] done: 35 passed, 0 failed`, 0 errors. The gizmo drawing itself wasn't looked at (nobody at the PC); only its geometry was checked.

### Phase 3: Spring-back and trigger logic

- [x] **3.1 Combine press sources and spring back.**
  Keep `pressAmount`. Each frame, `target = max(localPress, remotePress, clickPress)`. Jump up to `target` immediately; ease down toward it at `returnSpeed`. Clamp to `[0, travelDistance]`. Remove the temporary slider from 2.3 and drive `localPress` from a temporary debug value instead.
  *Check:* raising and dropping the debug value presses the button instantly and returns it smoothly. The button never goes past `travelDistance`.
  Done: added private `localPress`, `remotePress`, `clickPress` and `pressAmount`. `UpdatePressAmount()` clamps the max of the three to `[0, travelDistance]`, jumps straight to it when deeper, and otherwise `MoveTowards` it at `returnSpeed * Time.deltaTime`; `Update` then calls `ApplyPress(pressAmount)`. `debugPressSlider` is gone. In its place `debugLocalPress` (`[Range(0, 0.03)]`, marked `TEMP (plan step 3.1)`, remove in 5.3) is copied into `localPress` each frame. The harness's `AddPressTest` now sets `returnSpeed` to 0.01 m/s, presses with `debugLocalPress = 0.03`, and checks that `pressAmount` is exactly `travelDistance` 0.5 s later (easing in would only have reached 0.005), with the button 1 mm in front of the plate at (0, 1, 0.011) when unrotated. Then it releases and checks that 0.5 s later the press is part-way back at the expected rate (0.0140 measured, 0.0140 expected) and the position matches. Finally it restores `returnSpeed` and checks the button is back at rest. It runs unrotated and with the parent at (35, 120, -70). UdonSharp compiled 10 scripts with no errors. Result: `[PushButtonTest] done: 43 passed, 0 failed`, 0 errors (the trigger again only left Play Mode at "time up"). The scene file never stored `debugPressSlider`, so it needs no change.

- [x] **3.2 Add local-only triggering with arming.**
  Implement the logic in [Trigger rules](#trigger-rules): fire `targetBehaviour.SendCustomEvent(eventName)` when the **local** press reaches the bottom while armed, and re-arm below `releaseFraction`. Warn instead of throwing if the target or event name is unset.
  *Check:* sweeping the debug value 0 → max → 0 fires `OnButtonPressed` exactly once per sweep. Hovering at the bottom does not fire again. Driving `remotePress` alone moves the button but never fires.
  Done: added a private `armed` flag (starts true) and `FireTolerance = 0.0005f`. Each frame `Update` calls `UpdateTrigger(clamp(localPress, 0, travelDistance))` before `UpdatePressAmount`. It fires when armed and the local press is `>= travelDistance - FireTolerance`, then disarms. It re-arms when the press is `<= travelDistance * releaseFraction` and also below the fire depth, so `releaseFraction = 1` can't re-fire every frame. `FireAction()` logs a `[PushButton] <name>: pressed, but no target behaviour / event name is set` warning instead of throwing. Only `localPress` reaches the trigger. In 4.2 the local click has to feed `UpdateTrigger` too (e.g. pass `max(localPress, local clickPress)`), and networked clicks must not. Haptics are left for 5.5. Added `AddTriggerTest` to the harness. In Play Mode it creates a `PushButtonTestActionTarget` object with an `UdonBehaviour` running the `ButtonActionExample` program (`AssignProgramAndVariables` + `InitializeUdonContent`), points the button at it with `OnButtonPressed`, and counts `[ButtonActionExample] pressed` log lines. It checks two full sweeps (fires once at the bottom, no refire while hovering 0.5 s, re-armed after release), a partial sweep (bottom → 75% → bottom fires only once), and that `remotePress = 0.03` alone moves the button to `travelDistance` without firing or disarming. Afterwards it restores the target and event name and destroys the object. Result: 3 action logs, `[PushButtonTest] done: 65 passed, 0 failed`, with no errors or exceptions in the log. The older press tests now log the expected "no target behaviour is set" warning, because the scene button has no target until 7.2.

### Phase 4: Desktop/mobile click and network event

- [ ] **4.1 Set up local input mode.**
  In `Start`, set the collider's `isTrigger = true`. If `LocalPlayer.IsUserInVR()`, set `DisableInteractive = true`. Otherwise enable interaction with `InteractionText = interactText`. If `LocalPlayer` is null, fall back to desktop mode.
  *Check:* in ClientSim desktop, hovering shows the "Press" tooltip.

- [ ] **4.2 Add the local click animation.**
  `Interact()` starts the click animation (stamped with `Time.time`, flagged local): `clickPress` goes 0 → `travelDistance` over `clickPressDuration`, then releases. The action fires through the trigger rules from 3.2.
  *Check:* in ClientSim, each click animates the button in and back, and logs exactly once.

- [ ] **4.3 Add the networked click animation.**
  After a local click, send `SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetPlayClickAnimation))`. `public void NetPlayClickAnimation()` starts the same animation flagged **non-local**, which never fires the action. The handler ignores the event if the local player is out of range, and **does not check the local player's VR status**.
  *Check:* calling `NetPlayClickAnimation` directly (e.g. from a temporary ClientSim debug call) animates the button with no action log.

### Phase 5: Fingertip pressing

- [ ] **5.1 Track players.**
  Keep a cached `VRCPlayerApi[]`, refreshed via `VRCPlayerApi.GetPlayers` in `OnPlayerJoined` and `OnPlayerLeft`, with parallel per-player, per-fingertip state arrays (`engaged`, `wasInFront`). Never allocate in `Update`.
  *Check:* a log on join and leave shows the correct player count in ClientSim, including with simulated remote players.

- [ ] **5.2 Estimate fingertip positions.**
  Write a helper that returns each enabled fingertip for a player: the distal bone position plus `(distal - intermediate) * 0.8`, falling back to the hand bone if the finger bones are missing. Scale `fingertipRadius` by the player's avatar eye height, relative to a 1.6 m reference.
  *Check:* a gizmo or debug sphere follows the tips of a ClientSim avatar's fingers.

- [ ] **5.3 Add engagement and press depth.**
  Implement [Fingertip engagement](#fingertip-engagement): transform each tip into the rest frame, engage only on a front-side entry within the footprint, and press = `clamp(depth - faceOffset + radius, 0, travelDistance)`. The local player's tips plus `debugFingertip` feed `localPress`; remote players' tips feed `remotePress`. Remove the temporary debug value from 3.1.
  *Check (debug sphere as `debugFingertip`):* pushing from the front presses the button and stops it at `travelDistance`. The action fires once. Entering from the side or the back does nothing. Pulling out and re-entering re-arms and fires again.

- [ ] **5.4 Apply the per-player VR filter.**
  Evaluate fingertips only for players whose own `IsUserInVR()` is true. The local player counts only if they are in VR (or `debugFingertip` is set). Remote VR players are evaluated **on every client, VR and desktop alike**.
  *Check:* code review against [Who sees what](#who-sees-what), since real verification is in Phase 7.

- [ ] **5.5 Add haptics.**
  When the local VR player fires the action, call `PlayHapticEventInHand` on the hand that pressed, using `hapticStrength`. Skip this when the strength is 0 or the player isn't in VR.
  *Check:* compiles. Felt in the Phase 7 VR test.

### Phase 6: Performance gates

- [ ] **6.1 Add the range gate.**
  At the top of `Update`, if the local player is farther than `AnimationRangeSqr` (squared distance) from the button, skip the frame. On the first out-of-range frame, snap the button to rest and clear the engagement and click state once.
  *Check:* in ClientSim, press with the debug sphere and walk more than 5 m away. The button snaps to rest. Walk back and it works normally.

- [ ] **6.2 Add the per-player hand early-out.**
  Skip a player's fingertip work when both hand bones are more than 0.5 m from the button.
  *Check:* behaviour is unchanged. A temporary counter shows players skipped when far away.

### Phase 7: Editor inspector and scene wiring

- [ ] **7.1 Add the custom inspector.**
  `Assets/PushButton/Editor/PushButtonEditor.cs` draws the default inspector, replacing `eventName` with a dropdown of the public methods of `targetBehaviour` when it is an UdonSharpBehaviour. For Graph programs it falls back to free text.
  *Check:* selecting `ButtonActionExample` lists `OnButtonPressed`.

- [ ] **7.2 Wire up the scene.**
  Optionally rename the parent `Button` to `PushButtonAssembly`, and optionally rotate it 180° on Y so the button faces the spawn. On the child `Button`, keep `localPressAxis = (0, 0, -1)` and set `travelDistance = 0.019`. Add `ButtonActionExample` and assign it with `OnButtonPressed`. Remove any leftover temporary debug code.
  *Check:* the scene is saved, and a ClientSim desktop click logs once.

### Phase 8: Verification

- [ ] **8.1 Run the editor regression pass.**
  Repeat the checks from 4.2, 5.3 and 6.1 with the parent rotated to (35, 120, -70), and again with `ButtonPlate` disabled.
  *Check:* all pass in both setups.

- [x] **8.2 Set up testing automation.** Done: the editor trigger is installed and `TestingAutomation.md` exists. Compile checks in every step can use the trigger's `refresh`. Play Mode tests should log `[PushButtonTest] done: N passed, M failed` (see `TestingAutomation.md`). The world ID and test instance URL are recorded there too.

- [ ] **8.3 Run the multi-client test (built world, 2+ clients).**
  Cover every cell of [Who sees what](#who-sees-what):
  - desktop click on A → animates on a desktop B **and** a VR B; action log only on A;
  - VR push on A → animates on a desktop B **and** a VR B; action log only on A;
  - B more than 5 m away → no animation and no errors.

- [ ] **8.4 Run the real VR test (headset).**
  Push with either index finger. Check that haptics fire, the stop is flush with no z-fighting, and there are no false presses when reaching past the button.

---

## Design reference

### Current scene (VRCDefaultWorldScene)

| Object | Local position | Scale | Notes |
|---|---|---|---|
| `Button` (empty parent) | (0, 1, 0) | (1, 1, 1) | Root of the assembly |
| └ `ButtonPlate` | (0, 0, 0) | (0.1, 0.1, 0.04) | Visual reference only. The script never touches it |
| └ `Button` | (0, 0, 0.03) | (0.05, 0.05, 0.02) | Default cube, BoxCollider. Gets the `PushButton` component and presses along local -Z |

- The flush point is 0.02 m of travel. With a 1 mm anti-z-fighting margin, `travelDistance` is **0.019 m**.
- The parent and the child are both named `Button`. Renaming the parent avoids confusion.
- The spawn point (z = -2.558, facing +Z) looks at the **back** of the plate. Rotating the parent 180° on Y makes the button face the spawn.

### Who sees what

How the button is pressed depends on the **presser's** platform. Whether it animates **never** depends on the **viewer's** platform.

| Presser → / Viewer ↓ | VR viewer | Desktop/mobile viewer |
|---|---|---|
| **VR presser** (finger push) | Animates from the presser's synced bones | Animates from the presser's synced bones |
| **Desktop/mobile presser** (click) | Plays the click animation on receiving the network event | Plays the click animation on receiving the network event |
| **Action (`SendCustomEvent`)** | Presser's client only | Presser's client only |

The local player's VR status only decides **how the local player can press**: VR players use fingers with Interact disabled, and desktop/mobile players click.

### Approach: kinematic, not physics

Avatar hands in VRChat have no colliders that Udon can use, and Rigidbody "hand colliders" with joints tend to jitter and tunnel through. Instead, each frame the script works out where fingertips are relative to the button's rest pose, computes the press depth, clamps it to the travel distance (the hard stop), and positions the button. All the math is in the button's own rest frame, so it works for any rotation, scale and mesh.

### Constants

```csharp
// C#'s equivalent of Java's `static final` for a primitive is `const`.
// UdonSharp supports `const`; non-const static fields are not supported reliably.
private const float AnimationRange = 5f;   // meters
private const float AnimationRangeSqr = AnimationRange * AnimationRange;
```

### Inspector fields

| Group | Field | Default | Purpose |
|---|---|---|---|
| Action | `UdonBehaviour targetBehaviour` | – | Behaviour to call (works with both UdonSharp and Graph programs) |
| | `string eventName` | – | Event passed to `SendCustomEvent`, which runs on the presser's client only |
| Travel | `Vector3 localPressAxis` | (0, 0, -1) | Press direction in the button's local space |
| | `float travelDistance` | 0.019 | Meters. The stop depth and the trigger depth |
| | `float releaseFraction` | 0.5 | The button re-arms once it rises back above this fraction of travel |
| | `float returnSpeed` | 0.1 | m/s spring-back speed |
| VR | `float fingertipRadius` | 0.008 | Meters, scaled by each avatar's size |
| | `bool useIndexFingers` / `useMiddleFingers` | true / false | Which fingertips can press |
| | `float hapticStrength` | 0.3 | Haptic pulse on the local pressing hand (0 = off) |
| Desktop | `float clickPressDuration` | 0.12 | Seconds for the click press-in animation |
| | `string interactText` | "Press" | Interact tooltip |
| Debug | `Transform debugFingertip` | – | Editor-only. Acts as a local fingertip for testing without a headset |

### Fingertip engagement

- Rest-frame transform: `p = Quaternion.Inverse(restWorldRot) * (tip - restWorldPos)`, split into `depth` (along the press axis) and lateral components.
- A tip **engages** only if on the previous frame it was **in front of** the face and inside the footprint (plus the radius), and is now past the face.
- It stays engaged while inside the footprint. It disengages when it leaves sideways or pulls back out in front.
- An engaged tip's press = `clamp(depth - faceOffset + radius, 0, travelDistance)`.

### Trigger rules

- Only the local player's fingertips and the local player's own click can fire the action. Remote VR presses and networked click animations move the button but never fire.
- Fire when the local press amount is `>= travelDistance - 0.0005` and the button is armed. Then disarm, and play haptics if in VR.
- Re-arm when the local press amount is `<= travelDistance * releaseFraction`.

### Risks and limitations

- **Remote presses lag slightly:** synced bone data is interpolated and slightly delayed, so remote viewers see the button move a little behind the presser's finger.
- **Remote runs can briefly disagree:** a jittery remote finger near the face can show a small press the presser didn't see. This only affects visuals; the action is never fired remotely.
- **CPU scales with players in range:** cost is roughly (VR players near the button) × (enabled fingertips), only for buttons within 5 m. The 0.5 m hand early-out keeps it to a few distance checks per player.
- **Contact shape is the local bounding box:** for strongly non-box shapes, presses near the corners register slightly outside the visible mesh. `Collider.ClosestPoint` could tighten this.
- **Fingertip estimate varies by avatar:** the extrapolation is approximate, and `fingertipRadius` absorbs most of the variation.
