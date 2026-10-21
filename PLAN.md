# Push Button – Implementation Plan

Work through the **Steps** section one step at a time, ticking each box when the step and its check are done. The **Design reference** sections at the bottom hold the detail the steps point to.

## Goal

A reusable UdonSharp component that turns any object into a physically pushable button:

- **VR players** push it with their avatar's fingertips. The button moves along its press axis and stops at a set depth. When it reaches that depth, it fires a configurable action.
- **Desktop and mobile players** click it (a trigger collider with `Interact()`). The button plays a short press-in/return animation and fires the same action.
- **Every player sees the button move, whatever their own platform.** There is no position sync. Each client works out the animation itself from the avatar bone data VRChat already sends, and a desktop click sends one small network event so other clients play the click animation.
- **Only the player who pressed it runs the action.** If the action should affect everyone, the target behaviour does its own networking. The optional "anyone pressed" and "anyone released" events (8.11) are the exception: they run on every client, for things like sound effects.
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
  Done: [notes](plan-done/1.1-create-the-folder-layout.md).

- [x] **1.2 Create an empty `PushButton` UdonSharpBehaviour.**
  Add `Assets/PushButton/PushButton.cs` and its generated `PushButton.asset` program asset. Set `[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]`, and add the `AnimationRange` / `AnimationRangeSqr` constants (see [Constants](#constants)).
  *Check:* UdonSharp compiles with no console errors.
  Done: [notes](plan-done/1.2-create-an-empty-pushbutton-udonsharpbehaviour.md).

- [x] **1.3 Add all Inspector fields.**
  Add every field from [Inspector fields](#inspector-fields), with headers and tooltips. Wrap `debugFingertip` so it's editor-only in effect.
  *Check:* compiles. Adding the component to the child `Button` shows all the fields with their defaults.
  Done: [notes](plan-done/1.3-add-all-inspector-fields.md).

- [x] **1.4 Create the example action target.**
  Add `Assets/PushButton/Examples/ButtonActionExample.cs` with `public void OnButtonPressed()`, which logs `"[ButtonActionExample] pressed by <local player name>"` and toggles an optional `GameObject`.
  *Check:* compiles.
  Done: [notes](plan-done/1.4-create-the-example-action-target.md).

### Phase 2: Rest pose and geometry

- [x] **2.1 Cache the rest pose.**
  In `Start`, store `restLocalPosition` and `restLocalRotation`. Add a helper that rebuilds `restWorldPos` and `restWorldRot` from the parent (or world space if there is no parent) each frame.
  *Check:* a temporary log prints sensible values. Moving the parent at runtime changes the rebuilt world rest pose.
  Done: [notes](plan-done/2.1-cache-the-rest-pose.md).

- [x] **2.2 Compute the contact box.**
  Take the box from the button's own `BoxCollider` center and size, or from `MeshFilter.sharedMesh.bounds` if there is no box collider. Convert it to meters with `lossyScale`. From it, derive the **front face offset** along `localPressAxis` and the **footprint** half-extents on the two perpendicular axes. Normalize `localPressAxis`, and warn if it is zero.
  *Check:* for the scene button, the front face offset is 0.01 m and the footprint half-extents are 0.025 × 0.025 m.
  Done: [notes](plan-done/2.2-compute-the-contact-box.md).

- [x] **2.3 Apply a press amount.**
  Add `ApplyPress(float amount)`: `transform.position = restWorldPos + restWorldRot * (localPressAxis * amount)`. Add a temporary Inspector slider to drive it.
  *Check:* in Play Mode, the slider moves the button straight into the plate. It still moves along the correct axis after rotating the parent to (35, 120, -70).
  Done: [notes](plan-done/2.3-apply-a-press-amount.md).

- [x] **2.4 Add gizmos.**
  `OnDrawGizmosSelected` (editor-only) draws the press axis arrow, the contact box at rest, and a wire box at full `travelDistance`.
  *Check:* with travel 0.019, the full-depth box sits about 1 mm in front of the plate face, at any parent rotation.
  Done: [notes](plan-done/2.4-add-gizmos.md).

### Phase 3: Spring-back and trigger logic

- [x] **3.1 Combine press sources and spring back.**
  Keep `pressAmount`. Each frame, `target = max(localPress, remotePress, clickPress)`. Jump up to `target` immediately; ease down toward it at `returnSpeed`. Clamp to `[0, travelDistance]`. Remove the temporary slider from 2.3 and drive `localPress` from a temporary debug value instead.
  *Check:* raising and dropping the debug value presses the button instantly and returns it smoothly. The button never goes past `travelDistance`.
  Done: [notes](plan-done/3.1-combine-press-sources-and-spring-back.md).

- [x] **3.2 Add local-only triggering with arming.**
  Implement the logic in [Trigger rules](#trigger-rules): fire `targetBehaviour.SendCustomEvent(eventName)` when the **local** press reaches the bottom while armed, and re-arm below `releaseFraction`. Warn instead of throwing if the target or event name is unset.
  *Check:* sweeping the debug value 0 → max → 0 fires `OnButtonPressed` exactly once per sweep. Hovering at the bottom does not fire again. Driving `remotePress` alone moves the button but never fires.
  Done: [notes](plan-done/3.2-add-local-only-triggering-with-arming.md).

### Phase 4: Desktop/mobile click and network event

- [x] **4.1 Set up local input mode.**
  In `Start`, set the collider's `isTrigger = true`. If `LocalPlayer.IsUserInVR()`, set `DisableInteractive = true`. Otherwise enable interaction with `InteractionText = interactText`. If `LocalPlayer` is null, fall back to desktop mode.
  *Check:* in ClientSim desktop, hovering shows the "Press" tooltip.
  Done: [notes](plan-done/4.1-set-up-local-input-mode.md).

- [x] **4.2 Add the local click animation.**
  `Interact()` starts the click animation (stamped with `Time.time`, flagged local): `clickPress` goes 0 → `travelDistance` over `clickPressDuration`, then releases. The action fires through the trigger rules from 3.2.
  *Check:* in ClientSim, each click animates the button in and back, and logs exactly once.
  Done: [notes](plan-done/4.2-add-the-local-click-animation.md).

- [x] **4.3 Add the networked click animation.**
  After a local click, send `SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetPlayClickAnimation))`. `public void NetPlayClickAnimation()` starts the same animation flagged **non-local**, which never fires the action. The handler ignores the event if the local player is out of range, and **does not check the local player's VR status**.
  *Check:* calling `NetPlayClickAnimation` directly (e.g. from a temporary ClientSim debug call) animates the button with no action log.
  Done: [notes](plan-done/4.3-add-the-networked-click-animation.md).

### Phase 5: Fingertip pressing

- [x] **5.1 Track players.**
  Keep a cached `VRCPlayerApi[]`, refreshed via `VRCPlayerApi.GetPlayers` in `OnPlayerJoined` and `OnPlayerLeft`, with parallel per-player, per-fingertip state arrays (`engaged`, `wasInFront`). Never allocate in `Update`.
  *Check:* a log on join and leave shows the correct player count in ClientSim, including with simulated remote players.
  Done: [notes](plan-done/5.1-track-players.md).

- [x] **5.2 Estimate fingertip positions.**
  Write a helper that returns each enabled fingertip for a player: the distal bone position plus `(distal - intermediate) * 0.8`, falling back to the hand bone if the finger bones are missing. Scale `fingertipRadius` by the player's avatar eye height, relative to a 1.6 m reference.
  *Check:* a gizmo or debug sphere follows the tips of a ClientSim avatar's fingers.
  Done: [notes](plan-done/5.2-estimate-fingertip-positions.md).

- [x] **5.3 Add engagement and press depth.**
  Implement [Fingertip engagement](#fingertip-engagement): transform each tip into the rest frame, engage only on a front-side entry within the footprint, and press = `clamp(depth - faceOffset + radius, 0, travelDistance)`. The local player's tips plus `debugFingertip` feed `localPress`; remote players' tips feed `remotePress`. Remove the temporary debug value from 3.1.
  *Check (debug sphere as `debugFingertip`):* pushing from the front presses the button and stops it at `travelDistance`. The action fires once. Entering from the side or the back does nothing. Pulling out and re-entering re-arms and fires again.
  Done: [notes](plan-done/5.3-add-engagement-and-press-depth.md).

- [x] **5.4 Apply the per-player VR filter.**
  Evaluate fingertips only for players whose own `IsUserInVR()` is true. The local player counts only if they are in VR (or `debugFingertip` is set). Remote VR players are evaluated **on every client, VR and desktop alike**.
  *Check:* code review against [Who sees what](#who-sees-what), since real verification is in Phase 7.
  Done: [notes](plan-done/5.4-apply-the-per-player-vr-filter.md).

- [x] **5.5 Add haptics.**
  When the local VR player fires the action, call `PlayHapticEventInHand` on the hand that pressed, using `hapticStrength`. Skip this when the strength is 0 or the player isn't in VR.
  *Check:* compiles. Felt in the Phase 7 VR test.
  Done: [notes](plan-done/5.5-add-haptics.md).

### Phase 6: Performance gates

- [x] **6.1 Add the range gate.**
  At the top of `Update`, if the local player is farther than `AnimationRangeSqr` (squared distance) from the button, skip the frame. On the first out-of-range frame, snap the button to rest and clear the engagement and click state once.
  *Check:* in ClientSim, press with the debug sphere and walk more than 5 m away. The button snaps to rest. Walk back and it works normally.

  Done: [notes](plan-done/6.1-add-the-range-gate.md).

- [x] **6.2 Add the per-player hand early-out.**
  Skip a player's fingertip work when both hand bones are more than 0.5 m from the button.
  *Check:* behaviour is unchanged. A temporary counter shows players skipped when far away.
  Done: [notes](plan-done/6.2-add-the-per-player-hand-early-out.md).

### Phase 7: Editor inspector and scene wiring

- [x] **7.1 Add the custom inspector.**
  `Assets/PushButton/Editor/PushButtonEditor.cs` draws the default inspector, replacing `eventName` with a dropdown of the public methods of `targetBehaviour` when it is an UdonSharpBehaviour. For Graph programs it falls back to free text.
  *Check:* selecting `ButtonActionExample` lists `OnButtonPressed`.
  Done: [notes](plan-done/7.1-add-the-custom-inspector.md).

- [x] **7.2 Wire up the scene.**
  Optionally rename the parent `Button` to `PushButtonAssembly`, and optionally rotate it 180° on Y so the button faces the spawn. On the child `Button`, keep `localPressAxis = (0, 0, -1)` and set `travelDistance = 0.019`. Add `ButtonActionExample` and assign it with `OnButtonPressed`. Remove any leftover temporary debug code.
  *Check:* the scene is saved, and a ClientSim desktop click logs once.
  Done: [notes](plan-done/7.2-wire-up-the-scene.md).

### Phase 8: Shared manager

Moves the per-frame work out of each button into one `PushButtonManager`, makes enabling and disabling buttons safe, and adds the build-time wiring check. Steps 5.1, 5.2, 5.4, 6.1 and 6.2 move into the manager; 5.3's engagement, Phases 2–4 and 5.5 stay on the button. Every step keeps the whole Play Mode suite passing, updating older tests where the move changes how they drive the button. Steps 8.8–8.11 then move the shared settings to the manager and add the VR trigger option, held presses, the release action and the "anyone" events.

- [x] **8.1 Add the manager and make it required.**
  Add `Assets/PushButton/PushButtonManager.cs` and its `PushButtonManager.asset` program asset (`NoVariableSync`). Give `PushButton` a `manager` field (new `Setup` Inspector group, see [Inspector fields](#inspector-fields)). Move player tracking (5.1) from the button into the manager, using stable player slots (see [Manager](#manager)). Remove `OnPlayerJoined` / `OnPlayerLeft` / `trackedPlayers` from the button. A button with no manager is inert: it logs one warning, sets `DisableInteractive = true`, never moves and never fires.
  *Check:* compiles. Tests: the manager's slots match `GetPlayerCount()` through ClientSim joins and leaves, a reused slot gets a new generation, and a button with no manager logs exactly one warning, can't be clicked, ignores the debug fingertip and stays at rest.
  Done: [notes](plan-done/8.1-add-the-manager-and-make-it-required.md).

- [x] **8.2 Cache fingertips once per player per frame.**
  Move fingertip estimation (5.2), the VR filter (5.4) and the hand early-out (6.2) into the manager. Each frame, for each VR player, it reads the two hand bones. Only if a hand is within the early-out range of an active in-range button does it read the finger bones, and only for the fingers some such button uses. It publishes per-slot arrays (see [Manager](#manager)). Buttons read those arrays and keep their own engagement state (5.3) and their own radius (`fingertipRadius` × the slot's avatar scale).
  *Check:* the existing fingertip, VR filter, remote press, haptics and early-out tests pass. New test: hook `VRCPlayerApi._GetBonePosition` (like the 5.4 `IsUserInVR` hook) and count calls per frame with 1 and with 10 in-range buttons around the same player. The counts are equal.
  Done: [notes](plan-done/8.2-cache-fingertips-once-per-player-per-frame.md).

- [x] **8.3 Let the manager drive the buttons.**
  Remove `Update` from `PushButton`. The manager's `Update` loops over the registered buttons. It does the range gate (6.1) per button against the local player, calls the button's out-of-range reset once on leaving range, and calls a per-frame tick on in-range buttons (rest pose, fingertip presses from the manager's arrays, click, trigger, press amount, apply). Buttons that are out of range or disabled cost nothing beyond the range check.
  *Check:* the whole existing suite passes with the scene button driven by the manager. New test: 10 buttons in range and 40 beyond 5 m. Only the 10 tick, and pressing one never moves another.
  Done: [notes](plan-done/8.3-let-the-manager-drive-the-buttons.md).

- [x] **8.4 Make enabling and disabling safe.**
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

  Done: [notes](plan-done/8.4-make-enabling-and-disabling-safe.md).

- [x] **8.5 Skip transform writes when nothing changed.**
  The button remembers the press it last applied and only writes `transform.position` when the press changes, and once after an enable or reset. A button at rest stops touching its transform, so its collider stays out of physics updates.
  *Check:* with `transform.hasChanged` cleared, a button at rest for 1 s hasn't changed; pressed, released and back at rest, it has. The full suite still passes.
  Done: [notes](plan-done/8.5-skip-transform-writes-when-nothing-changed.md).

- [x] **8.6 Update the inspector and scene wiring.**
  The inspector shows the `manager` field and a warning box when it isn't the scene's first manager (see [Build-time check](#build-time-check)), with a **Fix** button that runs the same wiring code as the build check. `Tools/PushButton/Wire Up Scene` adds a `PushButtonManager` object at the scene root and wires the scene button to it. (8.1 already did the Wire Up Scene part, and the scene is saved with it. What's left here is the inspector warning and Fix button, and the inspector test. Consider switching Wire Up Scene to the shared wiring code once 8.7 adds it.)
  *Check:* the scene is saved with the manager and the wiring. `AddSceneWiringTest` checks it, and a ClientSim desktop click still logs once. The inspector test checks the warning shows for a null and a wrong manager and not for the right one.
  Done: [notes](plan-done/8.6-update-the-inspector-and-scene-wiring.md).

- [x] **8.7 Add the build-time check.**
  Build on `PushButtonWiring` (8.6 added it: `FirstManager`, `AllButtons`, `Problem`, `Fix`). Add `Assets/PushButton/Editor/PushButtonBuildCheck.cs`, implementing the VRChat SDK's `IVRCSDKBuildRequestedCallback` (it runs on Build & Test and Build & Upload). Follow [Build-time check](#build-time-check). Keep the decision logic separate from the dialog so tests can answer for it, and add two menu items: `Tools/PushButton/Check Manager Wiring` (the same check, with dialogs) and `Tools/PushButton/Fix Manager Wiring` (adds the manager if needed and wires every button, without asking; choosing the menu item is the consent). Automated runs use the existing trigger's generic `menu` command to call `Fix Manager Wiring` before a build. Nothing in the `vrchat-world-unity-automation` skill or its trigger changes for PushButton.
  *Check:* Edit Mode tests (answering through the test hook, then restoring the scene): no manager + yes adds one and wires every button, including inactive ones; no manager + no changes nothing; a correctly wired scene asks nothing; a null manager, a second manager, and proxy/Udon data that disagree each count as unwired and are fixed on yes and untouched on no; two managers log the warning and use the first in hierarchy order; a scene with no buttons asks nothing. Every case returns true, so the build continues.
  Done: [notes](plan-done/8.7-add-the-build-time-check.md).

- [x] **8.8 Move the shared settings to the manager.**
  Move `fingertipRadius`, `useIndexFingers`, `useMiddleFingers`, `hapticStrength`, `clickPressDuration` and `debugFingertip` from `PushButton` to `PushButtonManager`, keeping their defaults, tooltips and `VR` / `Desktop` / `Debug` headers (see [Inspector fields](#inspector-fields)). `interactText` stays on the button. To avoid cross-behaviour reads in the tick, each button keeps its own copies of `fingertipRadius`, `useIndexFingers`, `useMiddleFingers`, `hapticStrength` and `clickPressDuration` and uses only those in the tick. The manager writes them into the button whenever it registers it (every enable, through the existing registration path), rather than the button reading them in `OnEnable`: at scene load Udon delays a button's first `OnEnable` and the manager may not be initialized yet, while registration already only completes once the manager has run (8.4's `TryRegister` retry). A manager method `ApplySettings()` pushes the manager's current values to every registered button for runtime changes; disabled buttons pick them up on their next enable. The manager reads finger bones for its own finger choice instead of the union over in-range buttons. The debug fingertip stays editor-only: the button reads it from the manager inside `#if UNITY_EDITOR`, so builds have no read and no debug path at all. Each button keeps its own debug engagement state, so the tip presses only the button it's pushed into. Remove the moved fields from the button's custom inspector. Copy the scene button's current values to the scene's manager and save the scene. Update the tests that set these fields to set them on the manager.
  *Check:* compiles, and the full Play Mode and Edit Mode suites pass. New tests: changing `fingertipRadius`, the finger choice, `hapticStrength` or `clickPressDuration` on the manager and calling `ApplySettings()` changes how two buttons behave at once; a change without `ApplySettings()` reaches a button when it's next enabled; with non-default values saved on the manager, a button that's active when the scene loads uses them from its first tick; the debug fingertip on the manager presses only the button it's in front of; the button inspector no longer shows the moved fields.
  Done: [notes](plan-done/8.8-move-the-shared-settings-to-the-manager.md).

- [x] **8.9 Let VR players click with the trigger (optional).**
  Add `bool allowVRTrigger` (default false) to the manager. The manager's `Desktop` header becomes `Click`, holding `clickPressDuration` and `allowVRTrigger`. The manager pushes it to the buttons with the other settings (8.8). With it on, `SetUpLocalInput` leaves Interact enabled for VR players too, with `InteractionText = interactText`. With it off, VR players can't click, as now. A VR trigger click is exactly a desktop click: same `Interact()`, click animation, network event and action, and no haptics. `Interact()` ignores it while the local player's own fingertip (or the debug fingertip, which stands in for it) is pushing the button in, so a hand pressing the button can't click it as well. Remote fingertips never block it, so other players can't stop someone using a button by holding it in. The interactive state is re-applied whenever the manager pushes settings, so `ApplySettings()` can toggle it at runtime. On the button, move `interactText` to the top of the `Action` group, above `targetBehaviour`, and drop the `Desktop` header. Update the custom inspector to match.
  *Check:* tests with the local player faked as VR (the 5.4 `IsUserInVR` hook): with the setting off, the button isn't interactive and `Interact()` does nothing; with it on, it's interactive with `interactText`, and `Interact()` animates, fires once and sends the network event; with it on and the local (debug) fingertip holding the button partly in, `Interact()` is ignored; with it on and only a remote VR finger holding it in, `Interact()` still clicks and fires; toggling it with `ApplySettings()` flips `DisableInteractive`; a desktop player is unaffected either way. The inspector shows `interactText` first under `Action` and has no `Desktop` header.
  Done: [notes](plan-done/8.9-let-vr-players-click-with-the-trigger.md).

- [x] **8.10 Hold to keep the button pressed, with a release action.**
  - **Holding a click.** `Interact()` starts the click as now. The button presses in over `clickPressDuration`, then stays at the bottom while the use input (left mouse, or the VR trigger from 8.9) is held. When the input is released, it springs back at `returnSpeed`. A tap shorter than `clickPressDuration` finishes pressing in, then springs straight back, as today. The input release comes from `InputUse(false)`, which the button handles itself (the manager stays out of input): Udon sends every use event to every button, so `InputUse` returns straight away unless that button is holding a local click, and any use-up releases it. The cost is per click, not per frame. First check in ClientSim that `Interact` fires on input down. If it fires on release, holding can't work this way: stop and ask.
  - **Remote viewers.** The release sends a new `NetReleaseClick` network event to the others. A remote copy stays at the bottom until that arrives, then springs back. If it arrives before the press-in finishes, the press-in finishes first. The button records the presser with `NetworkCalling.CallingPlayer` and also releases a remote hold if that player leaves.
  - **Release action.** Add `string releaseEventName` (empty by default) to the button's `Action` group, under `eventName`. It's sent to the same `targetBehaviour` as `eventName`, on the presser's client only. When it's null or empty, no release event is sent at all. It fires exactly once after each fired press action, at the first of: the click's input is released; the local press rises back to the re-arm point (`travelDistance * releaseFraction`), which is how fingertip presses release; or the button resets mid-press (disabled, manager disabled, out of range), so every press gets its release.
  *Check:* tests (driving the input release through ClientSim or the button's `InputUse` handler): a click held for 1 s stays at the bottom, fires the press once, and fires the release once when the input is released, then springs back; a quick tap still animates fully and fires press then release once each; a held networked click holds on the remote copy until `NetReleaseClick`, and a release that arrives early lets the press-in finish; the remote hold ends when the presser leaves; a fingertip fires the release once when it rises past the re-arm point, and not while it wobbles below it; a null and an empty `releaseEventName` each send nothing and log no error, while the press event still fires; disabling the button or the manager mid-hold fires the release once and leaves the button at rest; with 8.9 on, a VR trigger hold behaves like a mouse hold; release actions are never fired by remote presses.
  Done: [notes](plan-done/8.10-hold-to-keep-the-button-pressed-with-a-release-action.md).

- [ ] **8.11 Add "anyone pressed" and "anyone released" events.**
  Add `string anyonePressedEventName` and `string anyoneReleasedEventName` (both empty by default) to the button's `Action` group, under `releaseEventName`. They go to the same `targetBehaviour`, but on **every** client, for things every player should notice, like a sound effect. When the presser's own press event fires (3.2), the button sends `anyonePressedEventName` locally straight away, then `SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetAnyonePressed))`. `NetAnyonePressed()` sends the same event on each other client. `anyoneReleasedEventName` does the same through `NetAnyoneReleased()` whenever the presser's release fires (8.10's rules, including a reset mid-press), so every "anyone pressed" is followed by one "anyone released". These events fire whether or not `eventName` and `releaseEventName` are set. On the presser, the order is: press event, then anyone pressed; release event, then anyone released. When a name is null or empty, nothing is sent for it, locally or over the network, so unused events cost no network traffic. Receivers don't check range or VR status, so a listener can decide for itself what to do with a far-away press. They ignore the event while their copy of the button is disabled or has no running manager, like networked clicks (8.4). Remote presses seen through synced bones or click animations never send these events; only the presser's network event does, so each press is announced exactly once per client.
  *Check:* tests: a local fingertip press and a click each send anyone pressed once locally, after the press event, and one `NetAnyonePressed` network event; the release sends anyone released once locally, after the release event, and one `NetAnyoneReleased`; calling `NetAnyonePressed` / `NetAnyoneReleased` directly sends the event once and never the presser-only events; a remote finger press or `NetPlayClickAnimation` sends no anyone events; null and empty names send nothing and no network event; they're ignored while the button is disabled; disabling mid-hold sends anyone released once.

### Phase 9: Verification

- [x] **9.1 Run the editor regression pass.**
  Repeat the checks from 4.2, 5.3 and 6.1 with the parent rotated to (35, 120, -70), and again with `ButtonPlate` disabled.
  *Check:* all pass in both setups.
  Done: [notes](plan-done/9.1-run-the-editor-regression-pass.md).

- [x] **9.2 Set up testing automation.** (Formerly 8.2.) Done: [notes](plan-done/9.2-set-up-testing-automation.md).

- [ ] **9.3 Run the multi-client test (built world, 2+ clients).**
  Cover every cell of [Who sees what](#who-sees-what):
  - desktop click on A → animates on a desktop B **and** a VR B; action log only on A;
  - VR push on A → animates on a desktop B **and** a VR B; action log only on A;
  - B more than 5 m away → no animation and no errors;
  - a button that starts disabled and is enabled after B joins → B's VR push animates it on A;
  - with `allowVRTrigger` on, a VR trigger click on A → animates on B; action log only on A;
  - a held click on A (mouse and VR trigger) → stays pressed on B until released on A; press and release logs only on A;
  - with 8.11's events set, every press and release on A (finger, click, VR trigger) logs anyone pressed / anyone released once on A and once on B;
  - on mobile, a held tap releases when the finger lifts. If the platform never sends the release, note it and fall back to a plain tap there.

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
| Action | `string interactText` | "Press" | Interact tooltip (moved here from `Desktop` in 8.9) |
| | `UdonBehaviour targetBehaviour` | – | Behaviour to call (works with both UdonSharp and Graph programs) |
| | `string eventName` | – | Event passed to `SendCustomEvent`, which runs on the presser's client only |
| | `string releaseEventName` | – | Optional (8.10). Event sent to the same target when the press is released, on the presser's client only. Null or empty sends nothing |
| | `string anyonePressedEventName` | – | Optional (8.11). Sent to the same target on every client when anyone's press fires. Null or empty sends nothing |
| | `string anyoneReleasedEventName` | – | Optional (8.11). Sent to the same target on every client when anyone's press is released. Null or empty sends nothing |
| Travel | `Vector3 localPressAxis` | (0, 0, -1) | Press direction in the button's local space |
| | `float travelDistance` | 0.019 | Meters. The stop depth and the trigger depth |
| | `float releaseFraction` | 0.5 | The button re-arms once it rises back above this fraction of travel |
| | `float returnSpeed` | 0.1 | m/s spring-back speed |

From 8.8 these live on `PushButtonManager` and apply to every button:

| Group | Field | Default | Purpose |
|---|---|---|---|
| VR | `float fingertipRadius` | 0.008 | Meters, scaled by each avatar's size |
| | `bool useIndexFingers` / `useMiddleFingers` | true / false | Which fingertips can press |
| | `float hapticStrength` | 0.3 | Haptic pulse on the local pressing hand (0 = off) |
| Click | `float clickPressDuration` | 0.12 | Seconds for the click press-in animation |
| | `bool allowVRTrigger` | false | From 8.9. VR players can also click with the controller trigger |
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
- From 8.10, after a fire, the release action (if set) fires once at the first of: the click's input is released, the button re-arms, or the button resets mid-press.
- From 8.11, the presser's press and release also send "anyone pressed" / "anyone released" (if set) locally and, through a network event, on every other client. These are the only events that run on clients other than the presser's.

### Risks and limitations

- **Remote presses lag slightly:** synced bone data is interpolated and slightly delayed, so remote viewers see the button move a little behind the presser's finger.
- **Remote runs can briefly disagree:** a jittery remote finger near the face can show a small press the presser didn't see. This only affects visuals; the action is never fired remotely.
- **CPU scales with players and nearby buttons:** before Phase 8, every button within 5 m read every VR player's bones each frame, so the cost was buttons × players. With the manager, bones are read once per player per frame, and each in-range button (expected 5–10 at a time) only does rest-frame math on cached positions. Buttons beyond 5 m cost one distance check each.
- **Cross-behaviour calls cost more in Udon:** the manager calls each in-range button's tick, and buttons read the manager's arrays. These calls are kept to one per button per frame and one read per array per tick.
- **Contact shape is the local bounding box:** for strongly non-box shapes, presses near the corners register slightly outside the visible mesh. `Collider.ClosestPoint` could tighten this.
- **Fingertip estimate varies by avatar:** the extrapolation is approximate, and `fingertipRadius` absorbs most of the variation.
