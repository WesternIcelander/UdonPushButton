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
- **One `PushButtonManager` per scene drives every button.** It is required: a button without one does nothing. It reads each player's bones once per frame for all buttons, so the cost stays flat as buttons are added (see [Manager](#manager)).
- **Enabling and disabling buttons at any time is safe** (see [Enable and disable](#enable-and-disable)).
- **A build-time check** offers to add the manager and wire the buttons when they are missing or wrong, and never blocks a build.

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

- [x] **4.1 Set up local input mode.**
  In `Start`, set the collider's `isTrigger = true`. If `LocalPlayer.IsUserInVR()`, set `DisableInteractive = true`. Otherwise enable interaction with `InteractionText = interactText`. If `LocalPlayer` is null, fall back to desktop mode.
  *Check:* in ClientSim desktop, hovering shows the "Press" tooltip.
  Done: `Start` calls `SetUpLocalInput()` after the contact box. It makes the button's own `Collider` a trigger (warning if there is none) and stores a private `localIsVR` (false when `LocalPlayer` is null). VR sets `DisableInteractive = true`; otherwise `DisableInteractive = false` and `InteractionText = interactText`. Udon only treats a behaviour as interactive if it has an `Interact` event, so an empty `public override void Interact()` was added, marked `TEMP (plan step 4.1)`, for 4.2 to fill in. Added `AddInputModeTest` to the harness. It checks the collider is a trigger, that the ClientSim local player is on desktop and matches `localIsVR`, and that `IsInteractive` is true. It also reads what ClientSim's tooltip would show from the button's `ClientSimUdonHelper` (`CanInteract()` and `GetInteractText()`), which logged `ClientSim can interact True, tooltip "Press"`. Result: `[PushButtonTest] done: 74 passed, 0 failed`, 0 errors. Nobody hovered by hand: the tooltip was checked through ClientSim's helper (the same calls `ClientSimTooltip` uses), and the VR branch was only checked by reading the code.

- [x] **4.2 Add the local click animation.**
  `Interact()` starts the click animation (stamped with `Time.time`, flagged local): `clickPress` goes 0 → `travelDistance` over `clickPressDuration`, then releases. The action fires through the trigger rules from 3.2.
  *Check:* in ClientSim, each click animates the button in and back, and logs exactly once.
  Done: `Interact()` calls `StartClickAnimation(true)`, which sets private `clickActive`, `clickIsLocal` and `clickStartTime = Time.time` (a new click restarts the animation). Each frame `UpdateClickPress()` sets `clickPress = travelDistance * elapsed / clickPressDuration`. Once the time is up it holds `travelDistance` for exactly one frame (so the trigger sees the bottom), then clears `clickActive` and drops `clickPress` to 0, and `UpdatePressAmount` springs the button back at `returnSpeed`. A `clickPressDuration` of 0 or less goes straight to the bottom. `Update` now passes `max(localPress, clickIsLocal ? clickPress : 0)` to `UpdateTrigger`. For 4.3, `NetPlayClickAnimation` only needs to call `StartClickAnimation(false)`. **Behaviour note:** re-arming follows the *local* press, so the button re-arms as soon as the click releases, while it is still visibly springing back. A quick second click fires again, which seems right for a click. Added `AddClickTest` to the harness, and moved the trigger test's target setup and teardown into `SetUpActionTarget` / `TearDownActionTarget`. The test clicks through the button's `ClientSimUdonHelper.Interact()` (the same call ClientSim's own interact makes), with `clickPressDuration` set to 1 s and `returnSpeed` to 0.01 m/s. It checks the press 0.4 s in (0.0076 measured, 0.0076 expected, no fire yet), then 0.4 s after the bottom: fired once, click released, re-armed, springing back at the expected rate (0.0150). It restores the speeds and checks the button is back at rest, then does a second click at the default speeds, which fires once more and returns to rest. Result: 2 action logs, `[PushButtonTest] done: 94 passed, 0 failed`, 0 errors. Nobody clicked by hand, and the animation wasn't watched; it was checked from the Udon variables and the transform.

- [x] **4.3 Add the networked click animation.**
  After a local click, send `SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetPlayClickAnimation))`. `public void NetPlayClickAnimation()` starts the same animation flagged **non-local**, which never fires the action. The handler ignores the event if the local player is out of range, and **does not check the local player's VR status**.
  *Check:* calling `NetPlayClickAnimation` directly (e.g. from a temporary ClientSim debug call) animates the button with no action log.
  Done: `Interact()` now sends the network event to `Others` after starting the local click. `NetPlayClickAnimation()` returns early when the local player is farther than `AnimationRangeSqr` from `transform.position` (a null local player counts as in range), and otherwise calls `StartClickAnimation(false)`. It doesn't look at VR status. **Extra guard:** if a *local* click is still pressing in (`clickActive && clickIsLocal`), the event is ignored, so another player's click arriving at the same moment can't turn the local click non-local and swallow its action. 6.1's range gate will also cover the out-of-range case each frame; this check stops a far-away event from starting a click in the first place. Added `AddNetClickTest` to the harness (instead of a temporary debug call inside `PushButton`). It calls `SendCustomEvent("NetPlayClickAnimation")` on the button's UdonBehaviour with `clickPressDuration` 1 s and `returnSpeed` 0.01 m/s, and checks: a non-local click starts; the press 0.4 s in matches the animation (0.0076 measured and expected); afterwards it reached the bottom and is springing back (0.0150), the click has ended, the button is still armed, and nothing fired. It then checks the button returns to rest. Finally it moves the parent 20 m away, sends the event again, and checks that no click starts, the button stays at rest, and nothing fires. Result: 0 action logs, `[PushButtonTest] done: 109 passed, 0 failed`, 0 errors. ClientSim has no remote clients, so the actual network delivery to other players is left for 9.3 (formerly 8.3).

### Phase 5: Fingertip pressing

- [x] **5.1 Track players.**
  Keep a cached `VRCPlayerApi[]`, refreshed via `VRCPlayerApi.GetPlayers` in `OnPlayerJoined` and `OnPlayerLeft`, with parallel per-player, per-fingertip state arrays (`engaged`, `wasInFront`). Never allocate in `Update`.
  *Check:* a log on join and leave shows the correct player count in ClientSim, including with simulated remote players.
  Done: `OnPlayerJoined` and `OnPlayerLeft` call `RefreshPlayers(leaving)`, which fills private `trackedPlayers` from `GetPlayers`, skipping invalid players and the leaving one. During `OnPlayerLeft` the leaving player is still in VRChat's (and ClientSim's) player list, so it has to be left out explicitly. The state arrays `fingertipEngaged` and `fingertipWasInFront` have `FingertipsPerPlayer = 4` slots per player (left index, right index, left middle, right middle), at index `player * 4 + fingertip`. On each refresh, the state of players who were already tracked is copied across, so a join or leave doesn't drop someone's press partway through. Arrays are only allocated in the refresh, never in `Update`. A temporary log, `[PushButton] Button: tracking N players` (marked `TEMP (plan step 5.1)`), runs on each refresh. Added `AddPlayerTrackingTest` to the harness. It runs last, uses `ClientSimMain.SpawnRemotePlayer` to add two remote players, then removes them one at a time with `ClientSimMain.RemovePlayer`. Each time it checks that the tracked list matches `GetPlayerCount()` and that the logged count is right (logs read 1 → 2 → 3 → 2 → 1). It also checks the state arrays are 4× the player count, every tracked player is valid, the local player is tracked, and the remaining remote player is still tracked after the other leaves. A marked fingertip state on the local player carries over through every refresh. Result: `[PushButtonTest] done: 136 passed, 0 failed`, 0 errors.

- [x] **5.2 Estimate fingertip positions.**
  Write a helper that returns each enabled fingertip for a player: the distal bone position plus `(distal - intermediate) * 0.8`, falling back to the hand bone if the finger bones are missing. Scale `fingertipRadius` by the player's avatar eye height, relative to a 1.6 m reference.
  *Check:* a gizmo or debug sphere follows the tips of a ClientSim avatar's fingers.
  Done: UdonSharp has no `out` parameters, so `GetFingertips(player)` fills preallocated private buffers: `fingertipPositions` (`Vector3[4]`), `fingertipValid` (`bool[4]`, in the same slot order as the state arrays: left index, right index, left middle, right middle) and `fingertipScaledRadius`. `EstimateFingertip` does one slot: distal + `(distal - intermediate) * FingertipExtrapolation` (0.8). If either finger bone is missing (`GetBonePosition` returns zero) it uses the hand bone instead, and the slot is invalid only if the hand is missing too. Disabled fingers (`useIndexFingers` / `useMiddleFingers`) are invalid. The radius is `fingertipRadius * eyeHeight / ReferenceEyeHeight` (1.6), or unscaled if the eye height isn't positive. A temporary hook in `Update` (marked `TEMP (plan step 5.2)`) runs `GetFingertips` on the local player each frame, so the tests can read the buffers. Step 5.3 should replace it with the per-player loop. **Gizmo:** in Play Mode, `OnDrawGizmosSelected` now also draws a green wire sphere of the scaled radius at each valid fingertip of every tracked player. It reads `trackedPlayers` from Udon and runs `GetFingertips` on the editor proxy (using the proxy's own finger toggles). Added `AddFingertipTest` to the harness. It compares Udon's buffers with an estimate from the local ClientSim avatar's bones: index slots valid and middle off by default (0.00 mm from expected, finger bones present, so no hand fallback), and the tip lies past the distal bone. The radius is scaled by the eye height (ClientSim 1.9 m → 0.0095). Turning index off and middle on flips the slots. A 1 m teleport moves both tips by exactly (1, 0, 0), and the player is teleported back afterwards. Result: `[PushButtonTest] done: 166 passed, 0 failed`, 0 errors. The hand-bone fallback isn't exercised, because the ClientSim avatar has all its finger bones. Nobody watched the gizmo; it was checked from the Udon buffers. **Trigger note (not from this step):** `play` logs the done line about 25 s in but still waits until "time up" (about 3 min) before leaving Play Mode. The 4.3 and 5.1 runs did the same.

- [x] **5.3 Add engagement and press depth.**
  Implement [Fingertip engagement](#fingertip-engagement): transform each tip into the rest frame, engage only on a front-side entry within the footprint, and press = `clamp(depth - faceOffset + radius, 0, travelDistance)`. The local player's tips plus `debugFingertip` feed `localPress`; remote players' tips feed `remotePress`. Remove the temporary debug value from 3.1.
  *Check (debug sphere as `debugFingertip`):* pushing from the front presses the button and stops it at `travelDistance`. The action fires once. Entering from the side or the back does nothing. Pulling out and re-entering re-arms and fires again.
  Done: `Update` now calls `UpdateFingertipPresses()` right after rebuilding the rest pose. It loops over `trackedPlayers`, runs `GetFingertips` for each one, and evaluates every valid slot. The local player's slots (`player.isLocal`) feed `localPress` and everyone else's feed `remotePress`, taking the deepest engaged press. Slots that are invalid (finger turned off, bones missing, player invalid) have their state cleared. `debugFingertip` is evaluated with the unscaled `fingertipRadius` and its own state (`debugEngaged` / `debugWasInFront`), and adds to `localPress`. `EvaluateFingertip(toRest, tip, radius)` does the [Fingertip engagement](#fingertip-engagement) rules. UdonSharp has no `ref`, so it passes state through the `tipEngaged` / `tipWasInFront` fields. It computes `press = Dot(p, pressAxis) + frontFaceOffset + radius` (2.2's sign convention) and an `inFootprint` test that grows the footprint by the radius. "Past the face" means `press > 0`. A tip engages if it was in front last frame (in the footprint, `press <= 0`) and is now in the footprint and past the face. It stays engaged while in the footprint and past the face. It returns `clamp(press, 0, travelDistance)` when engaged, otherwise 0. `debugLocalPress` and the 5.2 hook in `Update` are gone. `GetFingertips` still leaves the last evaluated player's tips in the buffers, which the 5.2 test reads while the local player is alone. **Harness:** `SetLocalPress` now drives a test fingertip object (`PlaceDebugTip(press, u, v)` places it in the rest frame; fraction 0 puts it 1 cm in front of the face). The press tests start the tip in front before pushing. The trigger test's direct `remotePress` poke was dropped, because `Update` now overwrites it each frame. New `AddEngagementTest` (debug fingertip, unrotated): in front → half-way (press 0.0095, no fire) → 0.03 m in (stops at 0.0190, fires once, no refire while holding) → pulled out (re-armed) → pushed again (fires, 2 total). Side entry, back entry (from 0.1 m behind) and sliding back in after leaving sideways all stay disengaged at press 0. A tip half its radius past the footprint edge still engages. `AddPlayerTrackingTest` now uses a real press, not a hand-set flag: it moves the button so the **local ClientSim avatar's left index fingertip** comes in from the front to half the travel (press 0.0095), and checks that it stays engaged through both joins and both leaves, then releases when the button moves away. New `AddRemotePressTest` spawns a ClientSim remote player, moves the local player 1.5 m aside, and moves the button onto the remote avatar's left index fingertip, first in front and then 0.03 m in. Result: `remotePress` 0.0190, `localPress` 0, button at the stop, no action log, still armed, released once moved away. UdonSharp compiled 10 scripts with no errors. Result: `[PushButtonTest] done: 240 passed, 0 failed`, 0 errors, 0 exceptions (the trigger again only left Play Mode at "time up"). **For 5.4:** with the VR filter, the desktop ClientSim local player won't be evaluated any more. `AddPlayerTrackingTest`'s carry-over check engages the local avatar's real fingertip, so it will need to change, e.g. to engage the remote player's fingertip instead. ClientSim remote players are desktop too (`IsUserInVR` false), so `AddRemotePressTest` will also need a way to count them as VR, or a different check.

- [x] **5.4 Apply the per-player VR filter.**
  Evaluate fingertips only for players whose own `IsUserInVR()` is true. The local player counts only if they are in VR (or `debugFingertip` is set). Remote VR players are evaluated **on every client, VR and desktop alike**.
  *Check:* code review against [Who sees what](#who-sees-what), since real verification is in Phase 7.
  Done: in `UpdateFingertipPresses`, a tracked player's fingertips are estimated and evaluated only if `Utilities.IsValid(player) && player.IsUserInVR()`. Otherwise all their slots are cleared, the same way as for an invalid player. The check uses each player's own `IsUserInVR()` every frame, for the local player too, and never the viewer's `localIsVR`, so a remote VR player's push moves the button on desktop and VR viewers alike. `debugFingertip` is still evaluated separately, whatever the local platform. **Code review against [Who sees what](#who-sees-what):** the VR presser row holds, because the presser's `IsUserInVR` is true on every viewer and the viewer's platform isn't checked. The desktop presser row is unchanged: their avatar's hands never press, and their click comes through `NetPlayClickAnimation`, which has no VR check. The action row is unchanged: only `localPress` and the local click reach `UpdateTrigger`. **Harness:** ClientSim players are all desktop, so the tests swap `VRCPlayerApi._IsUserInVR` from ClientSim's handler to `SimulatedIsUserInVR`, which reports chosen player IDs as in VR. `SimulateVR(player, bool)` controls it, and the swap is undone on `ExitingPlayMode`. `localIsVR` (input mode) is set in `Start` before the swap, so the input-mode test still sees desktop. `AddFingertipTest` marks the local player as VR first, and the later tests run with it on, as they did in 5.3. `AddRemotePressTest` marks its remote player as VR. New `AddVRFilterTest`: a local VR fingertip pressing half-way releases (engaged and in-front cleared, `localPress` 0) once the local player is switched to desktop. A desktop local fingertip neither registers in front nor engages when pushed in, while `debugFingertip` still presses (0.0095) on desktop. A desktop remote fingertip pushed 0.03 m in gives `remotePress` 0 and leaves the button at rest. After switching that remote player to VR, the same push engages from the front and reaches the stop (0.0190). UdonSharp compiled 10 scripts with no errors. Result: `[PushButtonTest] done: 275 passed, 0 failed`, 0 errors (the trigger again only left Play Mode at "time up").

- [x] **5.5 Add haptics.**
  When the local VR player fires the action, call `PlayHapticEventInHand` on the hand that pressed, using `hapticStrength`. Skip this when the strength is 0 or the player isn't in VR.
  *Check:* compiles. Felt in the Phase 7 VR test.
  Done: `UpdateFingertipPresses` now also records `localPressHand` (`VRC_Pickup.PickupHand`). This is the hand of the local player's deepest pressing fingertip: even slots are Left and odd slots are Right. It's `None` when the debug fingertip leads or nothing presses. When `UpdateTrigger` fires and `localPress` (not the click) reached the bottom, `PlayPressHaptics()` calls `PlayHapticEventInHand(localPressHand, 0.08 s, hapticStrength, 320 Hz)` on the local player. The duration and frequency are new constants, `HapticDuration` and `HapticFrequency`. It's skipped when `hapticStrength <= 0`, the hand is `None` (a click, or the debug fingertip), or the local player isn't valid or isn't in VR. The pulse plays even if the target or event name is unset, because the button still bottomed out. **Harness:** `MoveButtonOntoIndex(player, press, left)` generalizes `MoveButtonOntoLeftIndex` to either hand. New `AddHapticsTest` hooks `VRCPlayerApi._PlayHapticEventInHand` to record calls. ClientSim's own handler only logs, and only when its logs are on. The test unhooks on exit. With the local player simulated as VR, a left index press fires and pulses Left at 0.3. A right index press fires and pulses Right. With `hapticStrength` 0 it fires without a pulse. The debug fingertip fires without a pulse. UdonSharp compiled 10 scripts with no errors. Result: `[PushButtonTest] done: 290 passed, 0 failed`, 0 errors (the trigger again only left Play Mode at "time up"). Nobody has felt the pulse yet; that's for the Phase 7/8 VR test.

### Phase 6: Performance gates

- [x] **6.1 Add the range gate.**
  At the top of `Update`, if the local player is farther than `AnimationRangeSqr` (squared distance) from the button, skip the frame. On the first out-of-range frame, snap the button to rest and clear the engagement and click state once.
  *Check:* in ClientSim, press with the debug sphere and walk more than 5 m away. The button snaps to rest. Walk back and it works normally.

  Done: `Update` rebuilds the rest pose, then calls `UpdateRangeGate()`. This measures the squared distance from the local player's position to `restWorldPos` (a null local player counts as in range) and returns early when it's beyond `AnimationRangeSqr`. A new private `outOfRange` flag makes the reset run only on the first out-of-range frame. The reset clears every fingertip's engaged and in-front state, the debug fingertip's state and the click (`clickActive`, `clickIsLocal`, `clickPress`). It also zeroes `localPress`, `remotePress` and `localPressHand`, re-arms (the local press is now 0, below the re-arm depth), and snaps `pressAmount` and the transform to rest. Because in-front state is cleared, a fingertip still inside the button when the player comes back has to come in from the front again. `NetPlayClickAnimation`'s own range check (against `transform.position`) is unchanged. **Harness:** new `AddRangeGateTest` (unrotated, after the net-click test, `returnSpeed` 0.01 m/s so a snap can't be mistaken for spring-back). The debug fingertip presses to the stop (fires, disarmed), then the local player is teleported 10 m away. The button snaps to rest at once (press 0, at the rest position), engagement is cleared, it's re-armed and `outOfRange` is set. While away, pulling the tip out and pushing it back in registers nothing and doesn't fire. Back in range, the tip left inside doesn't engage; from the front it presses and fires again (2 action logs). A 1 s click teleported away part-way is cut off (click cleared, press 0, no fire). Teleporting back leaves the player in range, at rest and armed. "Walking" away is a ClientSim teleport. UdonSharp compiled 10 scripts with no errors. Result: `[PushButtonTest] done: 333 passed, 0 failed`, 0 errors (the trigger again only left Play Mode at "time up").

- [x] **6.2 Add the per-player hand early-out.**
  Skip a player's fingertip work when both hand bones are more than 0.5 m from the button.
  *Check:* behaviour is unchanged. A temporary counter shows players skipped when far away.
  Done: in `UpdateFingertipPresses`, a VR player is skipped (treated like a non-pressing player: no `GetFingertips`, all their slots cleared) when `HandNear` is false for both `LeftHand` and `RightHand`. `HandNear` compares the squared distance from the hand bone to the contact box's world centre (`restWorldPos + restWorldRot * contactCenter`) with `handEarlyOutRangeSqr`, which `ComputeContactBox` sets to `(HandEarlyOutDistance + |contactHalfExtents|)²`. So "0.5 m from the button" is measured from the box's bounding sphere, and large buttons still work. `HandEarlyOutDistance = 0.5f` is a new constant. A missing hand bone counts as near, so the fingertip estimate (which may use finger bones alone) still decides. The check runs after the VR filter, so desktop players are never counted. A temporary private counter `handEarlyOutSkips` (marked `TEMP (plan step 6.2)`) holds the number of players skipped on the last evaluated frame; step 7.2's clean-up should remove it with the other temporary code (and `CheckSkips` / `AddHandEarlyOutTest`'s skip checks with it). **Harness:** `AddFingertipTest` read the local player's tips from the buffers while the button sat far from the spawn, which the early-out now skips. It now moves the button 5 cm in front of the local left index first, moves it along with the 1 m teleport, and puts it back at the end. New `AddHandEarlyOutTest` (last): spawns a remote player, marks both as VR and moves the local player 1.5 m aside. With the button at the remote's left index, 1 player is skipped (local) and the remote tip registers in front; pushed 0.03 m in, `remotePress` reaches the stop as before. Moving the button to the local left index clears the remote press and slots (1 skipped, the remote), and the local tip presses half-way as before. Moving the button 1 m up (still within 5 m, range gate open) skips 2 and clears the local press. Switching the remote to desktop leaves 1 counted. With everyone back on desktop, 0. UdonSharp compiled 10 scripts with no errors. Result: `[PushButtonTest] done: 366 passed, 0 failed`, 0 errors (the trigger again only left Play Mode at "time up").

### Phase 7: Editor inspector and scene wiring

- [x] **7.1 Add the custom inspector.**
  `Assets/PushButton/Editor/PushButtonEditor.cs` draws the default inspector, replacing `eventName` with a dropdown of the public methods of `targetBehaviour` when it is an UdonSharpBehaviour. For Graph programs it falls back to free text.
  *Check:* selecting `ButtonActionExample` lists `OnButtonPressed`.
  Done: `PushButtonEditor` (`[CustomEditor(typeof(PushButton))]`) draws UdonSharp's default header, then every visible serialized property (headers and tooltips included) with `eventName` swapped for a popup. UdonSharp hosts it inside its own `UdonSharpBehaviourOverrideEditor` (as `_userEditor`), which is how custom U# inspectors work. `GetEventNames(UdonBehaviour)` gets the class with `UdonSharpEditorUtility.GetUdonSharpBehaviourType` and lists its public, parameterless, non-generic instance methods (inherited ones down to `UdonSharpBehaviour`), leaving out property accessors and overrides of built-in events (`Interact`, `OnPlayerJoined`, …), sorted. It returns null for no target or a non-UdonSharp (Graph) program, and the field is then a plain text field; a target with no callable methods shows the text field plus a warning. A current event name that the target doesn't have stays selectable as "name (not found)" (or "(none)" when empty), so it's never silently replaced. Multi-object editing with differing values falls back to the text field. **Harness:** new `AddInspectorTest` (last). `Editor.CreateEditor` on the scene button's `PushButton` hosts a `PushButtonEditor`. An `UdonBehaviour` with `ButtonActionExample.asset` as its program source lists exactly `OnButtonPressed`. No target and an `UdonBehaviour` without a program both give null (text fallback). The scene button's own list includes `NetPlayClickAnimation` and leaves out `Interact`, `OnPlayerJoined` and `GetType`. The popup's drawing itself wasn't checked by eye (nobody at the PC). UdonSharp compiled 10 scripts with no errors. Result: `[PushButtonTest] done: 374 passed, 0 failed`, 0 errors.

- [x] **7.2 Wire up the scene.**
  Optionally rename the parent `Button` to `PushButtonAssembly`, and optionally rotate it 180° on Y so the button faces the spawn. On the child `Button`, keep `localPressAxis = (0, 0, -1)` and set `travelDistance = 0.019`. Add `ButtonActionExample` and assign it with `OnButtonPressed`. Remove any leftover temporary debug code.
  *Check:* the scene is saved, and a ClientSim desktop click logs once.
  Done: both optional changes were made. The parent is now `PushButtonAssembly`, rotated (0, 180, 0), so the button is at world (0, 1, -0.03) and faces the spawn. The scene was edited by a new idempotent menu item, `Tools/PushButton/Wire Up Scene` (in `PushButtonPlayModeTests.cs`, replacing `Add PushButton To Scene Button`). It renames and rotates the parent, makes sure the child has a `PushButton`, adds a `ButtonActionExample` (no `toggleTarget`) to `PushButtonAssembly`, sets `localPressAxis` (0, 0, -1), `travelDistance` 0.019, `targetBehaviour` (the example's backing UdonBehaviour) and `eventName` `OnButtonPressed` through a `SerializedObject` on the proxy plus `CopyProxyToUdon`, and saves the scene. UdonSharp also set the button's backing `_syncMethod` in the scene (Unity-generated). Temporary code removed from `PushButton.cs`: the `rest local` log (2.1), the `tracking N players` log (5.1), and `handEarlyOutSkips` (6.2). **Harness:** `ButtonPath` is now `PushButtonAssembly/Button`. The tracking-log counts and `CheckSkips` were removed; the tracked-player array checks and the early-out's tip and press checks still cover the same behaviour. The `press unrotated` "1 mm in front of plate" check now uses `buttonParent.TransformPoint(0, 0, 0.011)` instead of a fixed world point. New `AddSceneWiringTest` (first) checks the saved wiring: parent at (0, 180, 0), travel 0.019, axis (0, 0, -1), and the target is the parent's UdonBehaviour running `ButtonActionExample.asset` with `OnButtonPressed`. It then clicks through `ClientSimUdonHelper.Interact()`; one second later there is exactly one `[ButtonActionExample] pressed` log and the button is back at rest. UdonSharp compiled 10 scripts with no errors. Result: `[PushButtonTest] done: 371 passed, 0 failed`, 0 errors. The test log has `scene wiring: desktop click, 1 action logs` and no `[PushButton]` debug lines.

### Phase 8: Shared manager

Moves the per-frame work out of each button into one `PushButtonManager`, makes enabling and disabling buttons safe, and adds the build-time wiring check. Steps 5.1, 5.2, 5.4, 6.1 and 6.2 move into the manager; 5.3's engagement, Phases 2–4 and 5.5 stay on the button. Every step keeps the whole Play Mode suite passing, updating older tests where the move changes how they drive the button.

- [ ] **8.1 Add the manager and make it required.**
  Add `Assets/PushButton/PushButtonManager.cs` and its `PushButtonManager.asset` program asset (`NoVariableSync`). Give `PushButton` a `manager` field (new `Setup` Inspector group, see [Inspector fields](#inspector-fields)). Move player tracking (5.1) from the button into the manager, using stable player slots (see [Manager](#manager)). Remove `OnPlayerJoined` / `OnPlayerLeft` / `trackedPlayers` from the button. A button with no manager is inert: it logs one warning, sets `DisableInteractive = true`, never moves and never fires.
  *Check:* compiles. Tests: the manager's slots match `GetPlayerCount()` through ClientSim joins and leaves, a reused slot gets a new generation, and a button with no manager logs exactly one warning, can't be clicked, ignores the debug fingertip and stays at rest.

- [ ] **8.2 Cache fingertips once per player per frame.**
  Move fingertip estimation (5.2), the VR filter (5.4) and the hand early-out (6.2) into the manager. Each frame, for each VR player, it reads the two hand bones. Only if a hand is within the early-out range of an active in-range button does it read the finger bones, and only for the fingers some such button uses. It publishes per-slot arrays (see [Manager](#manager)). Buttons read those arrays and keep their own engagement state (5.3) and their own radius (`fingertipRadius` × the slot's avatar scale).
  *Check:* the existing fingertip, VR filter, remote press, haptics and early-out tests pass. New test: hook `VRCPlayerApi._GetBonePosition` (like the 5.4 `IsUserInVR` hook) and count calls per frame with 1 and with 10 in-range buttons around the same player. The counts are equal.

- [ ] **8.3 Let the manager drive the buttons.**
  Remove `Update` from `PushButton`. The manager's `Update` loops over the registered buttons. It does the range gate (6.1) per button against the local player, calls the button's out-of-range reset once on leaving range, and calls a per-frame tick on in-range buttons (rest pose, fingertip presses from the manager's arrays, click, trigger, press amount, apply). Buttons that are out of range or disabled cost nothing beyond the range check.
  *Check:* the whole existing suite passes with the scene button driven by the manager. New test: 10 buttons in range and 40 beyond 5 m. Only the 10 tick, and pressing one never moves another.

- [ ] **8.4 Make enabling and disabling safe.**
  Implement every rule in [Enable and disable](#enable-and-disable).
  *Check:* new `AddEnableDisableTest`, each case run by turning off the button's GameObject, a parent GameObject and the button's UdonBehaviour:
  - disabled mid-press (local finger, remote finger, local click, networked click): snaps to rest, armed, nothing engaged; re-enabled with the finger still inside: doesn't engage until it comes in from the front;
  - a button that starts disabled, then is enabled with players already present: local and remote VR fingers press it;
  - a player joins and another leaves while it is disabled: after enabling, both are handled correctly and no stale state shows up;
  - the parent moved while disabled: re-enabled at the new rest pose;
  - the button's own action disables it, enables another button, or disables another button: no error, no move after the disable, the registration list has no duplicates or holes, and the other buttons keep working;
  - 20 disable/enable toggles in one frame: registered exactly once;
  - a networked click arriving while disabled: ignored;
  - manager disabled mid-press: every button snaps to rest; manager re-enabled after a join: the new player presses.

- [ ] **8.5 Skip transform writes when nothing changed.**
  The button remembers the press it last applied and only writes `transform.position` when the press changes, and once after an enable or reset. A button at rest stops touching its transform, so its collider stays out of physics updates.
  *Check:* with `transform.hasChanged` cleared, a button at rest for 1 s hasn't changed; pressed, released and back at rest, it has. The full suite still passes.

- [ ] **8.6 Update the inspector and scene wiring.**
  The inspector shows the `manager` field and a warning box when it isn't the scene's first manager (see [Build-time check](#build-time-check)), with a **Fix** button that runs the same wiring code as the build check. `Tools/PushButton/Wire Up Scene` adds a `PushButtonManager` object at the scene root and wires the scene button to it.
  *Check:* the scene is saved with the manager and the wiring. `AddSceneWiringTest` checks it, and a ClientSim desktop click still logs once. The inspector test checks the warning shows for a null and a wrong manager and not for the right one.

- [ ] **8.7 Add the build-time check.**
  Add `Assets/PushButton/Editor/PushButtonBuildCheck.cs`, implementing the VRChat SDK's `IVRCSDKBuildRequestedCallback` (it runs on Build & Test and Build & Upload). Follow [Build-time check](#build-time-check). Keep the decision logic separate from the dialog so tests can answer for it, and add two menu items: `Tools/PushButton/Check Manager Wiring` (the same check, with dialogs) and `Tools/PushButton/Fix Manager Wiring` (adds the manager if needed and wires every button, without asking; choosing the menu item is the consent). Automated runs use the existing trigger's generic `menu` command to call `Fix Manager Wiring` before a build. Nothing in the `vrchat-world-unity-automation` skill or its trigger changes for PushButton.
  *Check:* Edit Mode tests (answering through the test hook, then restoring the scene): no manager + yes adds one and wires every button, including inactive ones; no manager + no changes nothing; a correctly wired scene asks nothing; a null manager, a second manager, and proxy/Udon data that disagree each count as unwired and are fixed on yes and untouched on no; two managers log the warning and use the first in hierarchy order; a scene with no buttons asks nothing. Every case returns true, so the build continues.

### Phase 9: Verification

- [ ] **9.1 Run the editor regression pass.**
  Repeat the checks from 4.2, 5.3 and 6.1 with the parent rotated to (35, 120, -70), and again with `ButtonPlate` disabled.
  *Check:* all pass in both setups.
  This was done before Phase 8 (as step 8.1). It has to be re-run once Phase 8 is done. Previous run: `AddClickTest` (4.2), `AddEngagementTest` (5.3) and `AddRangeGateTest` (6.1) now take an optional `setup` suffix for their check names. A new `AddRegressionPass(setup, plateActive)`, run twice after the inspector test, moves the parent to its scene position + (1, 0.5, -2) (1.8 m from the local player, well inside the 5 m gate), rotates it to (35, 120, -70), sets `ButtonPlate` active or disabled, checks the rest pose follows the parent and the button is at rest and armed, then runs those three tests again: first as ` (rotated parent)` with the plate on, then as ` (no plate)` with the parent still rotated and the plate disabled. `AddRegressionCleanup` then puts the parent and plate back. The scene pose is recorded in `AddSceneWiringTest`'s first step (`sceneParentPos`/`sceneParentRot`). No changes to `PushButton.cs`. UdonSharp compiled 10 scripts with no errors. Result: `[PushButtonTest] done: 627 passed, 0 failed` (up from 371), 0 errors. Both setups logged 2 action logs for the engagement and range gate passes, as in the unrotated run. The `play` timeout went from 120 to 180 s because the run now takes about 90 s.

- [x] **9.2 Set up testing automation.** (Formerly 8.2.) Done: the editor trigger is installed and `TestingAutomation.md` exists. Compile checks in every step can use the trigger's `refresh`. Play Mode tests should log `[PushButtonTest] done: N passed, M failed` (see `TestingAutomation.md`). The world ID and test instance URL are recorded there too.

- [ ] **9.3 Run the multi-client test (built world, 2+ clients).**
  Cover every cell of [Who sees what](#who-sees-what):
  - desktop click on A → animates on a desktop B **and** a VR B; action log only on A;
  - VR push on A → animates on a desktop B **and** a VR B; action log only on A;
  - B more than 5 m away → no animation and no errors;
  - a button that starts disabled and is enabled after B joins → B's VR push animates it on A.

- [ ] **9.4 Run the real VR test (headset).**
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
- The parent and the child were both named `Button`. Step 7.2 renamed the parent to `PushButtonAssembly` and turned it 180° on Y to face the spawn.
- The spawn point (z = -2.558, facing +Z) looks at the **back** of the plate. Rotating the parent 180° on Y makes the button face the spawn.
- Step 8.6 adds a `PushButtonManager` object at the scene root and wires the button to it.

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

From Phase 8 the range constants, `HandEarlyOutDistance`, `FingertipExtrapolation` and `ReferenceEyeHeight` live on `PushButtonManager`. `FireTolerance` and the haptic constants stay on `PushButton`.

### Manager

- **Required.** Every `PushButton` has a `manager` field. With no manager, the button does nothing at all, clicks included (see 8.1).
- **One per scene.** If there are several, buttons are wired to the first in hierarchy order: root objects in scene order, then depth first by sibling index. The others drive nothing.
- **Players** live in stable slots. A joining player takes a free slot and a leaving player frees theirs, so slots never shift. Each slot has a generation number that goes up whenever the slot gets a new player. A button stores the generation it last saw per slot and clears that slot's engagement state when it changes, so a new player never inherits a press. The slot arrays grow when needed; there's no fixed player cap. The manager also rebuilds its slots from `GetPlayers` in `OnEnable`, so it recovers if it was ever disabled.
- **Per-frame cache** (per slot, written once per frame by the manager, read by every in-range button): valid, is local, in VR, hand positions, near-a-button flag, fingertip positions and validity (left index, right index, left middle, right middle), and avatar scale (eye height / 1.6 m). Buttons read the arrays once per tick, not per element. Bone reads then scale with players, not with players × buttons.
- **Ticking.** Only the manager has an `Update`. Per registered button it does the range check. It calls the button's reset once on leaving range, and its tick while in range.
- **Registration** is an array of buttons. Register and unregister are idempotent. Changes requested while the manager is ticking (an action enabling or disabling buttons) are deferred and applied after the loop, so the loop never sees a changing list.

### Enable and disable

A disabled button holds no state that can go stale, so turning it off and on at any time, in any way, has no effect beyond the button being off.

- **What counts as disabled:** its GameObject or any parent turned off, or its UdonBehaviour turned off. All three run `OnDisable`, and destroying a button runs it too.
- **`OnEnable`:** registers with the manager. On the first enable it also does what `Start` did (rest pose, contact box, input mode), so it doesn't rely on `Start` order or on the manager having run `Start`. Every enable re-reads the rest pose from the parent and starts at rest, armed, with nothing engaged.
- **`OnDisable`:** unregisters, snaps to rest, clears all engagement, click and press state, and re-arms. A finger still inside when it comes back has to come in from the front again.
- **No player state on the button** beyond engagement per manager slot, which is cleared on disable and checked against the slot generation. Joins and leaves while a button is disabled therefore can't leave it with a stale or missing player.
- **The rest pose is never captured from a pressed position**, because a button is always at rest when disabled.
- **Actions that change buttons:** if a button's own action disables it, its tick stops straight after the action and doesn't move it again. Enabling or disabling other buttons from an action is deferred by the manager (see [Manager](#manager)).
- **Networked clicks** that reach a disabled or unregistered button are ignored.
- **Manager disabled:** it resets every registered button to rest and stops ticking. Buttons stay registered. When it is re-enabled it rebuilds its player slots and carries on.

### Build-time check

`PushButtonBuildCheck` runs when a VRChat build is requested and **always lets the build continue**. It looks at every `PushButton` in the loaded scenes, including inactive ones.

- **No buttons:** nothing happens.
- **No manager:** a dialog asks to add one and wire the buttons. **Yes** creates a `PushButtonManager` GameObject at the scene root, wires every button and saves the scene. **No** changes nothing.
- **Several managers:** a warning names the one used (the first in hierarchy order) and the ones ignored. This is logged on every check.
- **Unwired buttons:** a button is wired only if its `manager` is exactly the first manager, in both the UdonSharp proxy and the UdonBehaviour's data. Null, missing, another manager, or the two disagreeing all count as unwired. If any are unwired, a dialog gives the count and asks to fix them. **Yes** wires them and saves the scene. **No** changes nothing.
- Fixes go through `SerializedObject` plus `CopyProxyToUdon`, with Undo, and the scene is saved so the build includes them.
- The dialog is only shown when something is wrong, so an unattended build of a correctly wired scene never stops at a dialog. Before an automated build, run `menu Tools/PushButton/Fix Manager Wiring` so the check finds nothing to ask about.

### Inspector fields

| Group | Field | Default | Purpose |
|---|---|---|---|
| Setup | `PushButtonManager manager` | – | Required. The scene's manager (see [Manager](#manager)) |
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
- **CPU scales with players and nearby buttons:** before Phase 8, every button within 5 m read every VR player's bones each frame, so the cost was buttons × players. With the manager, bones are read once per player per frame, and each in-range button (expected 5–10 at a time) only does rest-frame math on cached positions. Buttons beyond 5 m cost one distance check each.
- **Cross-behaviour calls cost more in Udon:** the manager calls each in-range button's tick, and buttons read the manager's arrays. These calls are kept to one per button per frame and one read per array per tick.
- **Contact shape is the local bounding box:** for strongly non-box shapes, presses near the corners register slightly outside the visible mesh. `Collider.ClosestPoint` could tighten this.
- **Fingertip estimate varies by avatar:** the extrapolation is approximate, and `fingertipRadius` absorbs most of the variation.
