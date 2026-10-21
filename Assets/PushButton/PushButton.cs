using UdonSharp;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Turns any object into a physically pushable button: VR players push it with their fingertips,
/// desktop and mobile players click it. Only the presser's client runs the configured action.
/// Has no Update of its own: the scene's <see cref="PushButtonManager"/> ticks it while the local player is in range.
/// Can be disabled and enabled at any time (its GameObject, a parent, or its UdonBehaviour): a disabled button is at
/// rest and holds no state that could go stale.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class PushButton : UdonSharpBehaviour
{
    [Header("Setup")]
    [Tooltip("Required. The scene's PushButtonManager. Without one the button does nothing.")]
    public PushButtonManager manager;

    [Header("Action")]
    [Tooltip("Interact tooltip text, shown to desktop and mobile players (and VR players when the manager allows the trigger).")]
    public string interactText = "Press";
    [Tooltip("Behaviour to call when the button is pressed (UdonSharp or Graph program).")]
    public UdonBehaviour targetBehaviour;
    [Tooltip("Custom event sent to the target behaviour. Runs on the presser's client only.")]
    public string eventName;
    [Tooltip("Optional. Custom event sent to the same target when the press is released. Runs on the presser's client only.")]
    public string releaseEventName;

    [Header("Travel")]
    [Tooltip("Press direction in the button's local space.")]
    public Vector3 localPressAxis = new Vector3(0f, 0f, -1f);
    [Tooltip("Meters. The button stops at this depth and fires the action there.")]
    public float travelDistance = 0.019f;
    [Tooltip("The button re-arms once it rises back above this fraction of the travel distance.")]
    [Range(0f, 1f)]
    public float releaseFraction = 0.5f;
    [Tooltip("Spring-back speed in meters per second.")]
    public float returnSpeed = 0.1f;

    // The manager's shared settings (see PushButtonManager), written by the manager whenever it registers this button
    // and by its ApplySettings. The tick uses only these copies, so it never reads another behaviour's fields.
    [System.NonSerialized] public float fingertipRadius = 0.008f;
    [System.NonSerialized] public bool useIndexFingers = true;
    [System.NonSerialized] public bool useMiddleFingers = false;
    [System.NonSerialized] public float hapticStrength = 0.3f;
    [System.NonSerialized] public float clickPressDuration = 0.12f;
    [System.NonSerialized] public bool allowVRTrigger = false;

    // Rest pose relative to the parent, captured once on the first enable. The button is always at rest while
    // disabled, so it's never captured from a pressed position.
    private Vector3 restLocalPosition;
    private Quaternion restLocalRotation;
    // Rest pose in world space, rebuilt every frame so a moving parent carries the button along.
    private Vector3 restWorldPos;
    private Quaternion restWorldRot;

    // Contact box in the button's rest frame (rotation only, in meters), set up once on the first enable.
    // pressAxis is the normalized localPressAxis; lateralAxisU and lateralAxisV complete an orthonormal basis.
    private Vector3 pressAxis;
    private Vector3 lateralAxisU;
    private Vector3 lateralAxisV;
    // Distance from the rest position to the front face (the face a finger approaches), measured against pressAxis.
    private float frontFaceOffset;
    // Footprint centre and half-extents along lateralAxisU and lateralAxisV.
    private float footprintCenterU;
    private float footprintCenterV;
    private float footprintHalfU;
    private float footprintHalfV;
    // The whole box in the rest frame: centre and half-extents in meters, along the button's local axes.
    private Vector3 contactCenter;
    private Vector3 contactHalfExtents;

    // Press sources in meters: the local player's fingertips, remote players' fingertips, and the click animation.
    private float localPress;
    private float remotePress;
    private float clickPress;
    // The press amount the button is shown at, in [0, travelDistance].
    private float pressAmount;
    // The press ApplyPress last wrote to the transform, or -1 to make the next ApplyPress write whatever the press.
    // A button whose press doesn't change leaves its transform alone (the parent carries it along), so a button at
    // rest stays out of physics updates.
    private float appliedPress = -1f;
    // Firing disarms the button; the local press rising back to releaseFraction of the travel re-arms it.
    private bool armed = true;
    // Whether the press action has fired and its release action hasn't yet. The release fires at the first of: the
    // click's input is released, the button re-arms, or the button resets.
    private bool releasePending;
    // Counts ResetToRest calls, so firing can tell whether the press action reset the button.
    private int resetCount;
    // Meters short of travelDistance at which the local press counts as reaching the bottom.
    private const float FireTolerance = 0.0005f;
    // Whether the local player is in VR, set on the first enable and whenever the manager pushes its settings: VR players
    // press with their fingers (and click with the trigger only when allowVRTrigger is on), everyone else clicks.
    private bool localIsVR;
    // The click animation: clickPress runs 0 -> travelDistance over clickPressDuration from clickStartTime, holds the
    // bottom while clickHeld (and for at least one frame), then releases and springs back. Only a local click
    // (clickIsLocal) can fire the action. A local click is held until the use input is released; a remote one until
    // NetReleaseClick arrives from its presser (clickPresserId, -1 if unknown) or the presser leaves.
    private bool clickActive;
    private bool clickIsLocal;
    private bool clickHeld;
    private int clickPresserId = -1;
    private float clickStartTime;

    // Fingertip slots per player, in the manager's order: left index, right index, left middle, right middle.
    private const int FingertipsPerPlayer = PushButtonManager.FingertipsPerPlayer;
    // Per-fingertip state for each of the manager's player slots: fingertip f of slot i is at index
    // i * FingertipsPerPlayer + f. seenGenerations[i] is the slot's generation the state belongs to; when the manager
    // gives the slot a new player, the state is cleared so the new player never inherits a press.
    private bool[] fingertipEngaged = new bool[0];
    private bool[] fingertipWasInFront = new bool[0];
    private int[] seenGenerations = new int[0];

    // What the manager reads to decide whose finger bones to read while this button is in range: the sphere a hand
    // bone has to be in, centred on the contact box (its centre in the button's local space, as on the BoxCollider).
    [System.NonSerialized] public Vector3 fingerCheckLocalCenter;
    [System.NonSerialized] public float fingerCheckRangeSqr;
    // Whether the one-time setup has run, and whether the button is enabled now with a manager.
    private bool initialized;
    private bool enabledNow;
    // Set by the manager: whether the button is in its list of buttons (or will be once the manager's current tick
    // ends). Udon drops calls to a manager it hasn't started yet, which leaves this false.
    [System.NonSerialized] public bool registeredWithManager;
    // Engagement state of the debug fingertip, which isn't a tracked player's slot.
    private bool debugEngaged;
    private bool debugWasInFront;
    // One fingertip's engagement state while EvaluateFingertip updates it (UdonSharp has no ref parameters).
    private bool tipEngaged;
    private bool tipWasInFront;
    // The hand of the local player's deepest pressing fingertip, or None when the debug fingertip (or nothing) leads.
    private VRC_Pickup.PickupHand localPressHand = VRC_Pickup.PickupHand.None;
    // The haptic pulse played in that hand when its press fires the action, at hapticStrength.
    private const float HapticDuration = 0.08f;   // seconds
    private const float HapticFrequency = 320f;   // Hz
    // Whether the manager last found the local player beyond its AnimationRange (set by _LeaveRange, cleared by _Tick).
    private bool outOfRange;

    /// <summary>
    /// Every enable starts the button at rest, armed and with nothing engaged, at the rest pose taken from the parent
    /// now, and registers it with the manager. The first enable also does the one-time setup. Udon runs the first
    /// OnEnable just before Start, so nothing here depends on Start or on the manager having started.
    /// </summary>
    private void OnEnable()
    {
        if (!initialized) Initialize();
        if (manager == null) return;
        enabledNow = true;
        ResetToRest();
        TryRegister();
    }

    /// <summary>
    /// Unregisters from the manager and snaps to rest with all engagement, click and press state cleared, so a
    /// finger still inside when the button comes back has to come in from the front again. Also runs on destroy.
    /// </summary>
    private void OnDisable()
    {
        enabledNow = false;
        if (!initialized || manager == null) return;
        if (Utilities.IsValid(manager)) manager.Unregister(this);
        registeredWithManager = false;
        ResetToRest();
    }

    /// <summary>
    /// The one-time setup on the first enable: rest pose, contact box and input mode. A button with no manager warns
    /// once and can't be clicked.
    /// </summary>
    private void Initialize()
    {
        restLocalPosition = transform.localPosition;
        restLocalRotation = transform.localRotation;
        UpdateRestWorldPose();
        ComputeContactBox(true);
        SetUpLocalInput();
        initialized = true;
        // SetUpLocalInput has already made it unclickable.
        if (manager == null) Debug.LogWarning("[PushButton] " + name + ": no manager is set, so the button does nothing");
    }

    /// <summary>
    /// Registers with the manager, which sets registeredWithManager. Udon drops calls to a behaviour it hasn't started
    /// yet, which happens when this button is enabled before the manager's first frame, so a dropped call is retried
    /// on the next frame.
    /// </summary>
    private void TryRegister()
    {
        manager.Register(this);
        if (!registeredWithManager) SendCustomEventDelayedFrames(nameof(_RetryRegister), 1);
    }

    public void _RetryRegister()
    {
        if (enabledNow && !registeredWithManager && Utilities.IsValid(manager)) TryRegister();
    }

    /// <summary>
    /// Whether the button can be pressed now: enabled, registered and its manager running. Clicks and networked
    /// clicks that arrive otherwise are ignored (Udon delivers events to disabled behaviours too).
    /// </summary>
    private bool IsLive()
    {
        return enabledNow && registeredWithManager && Utilities.IsValid(manager) && manager.running;
    }

    /// <summary>
    /// Makes the collider a trigger so it never blocks hands or players, and sets up Interact (see _UpdateInteractive).
    /// </summary>
    private void SetUpLocalInput()
    {
        Collider ownCollider = GetComponent<Collider>();
        if (ownCollider != null) ownCollider.isTrigger = true;
        else Debug.LogWarning("[PushButton] " + name + ": no collider, so desktop and mobile players can't click it");
        _UpdateInteractive();
    }

    /// <summary>
    /// Desktop and mobile players (or no local player at all) click the button. VR players press with their fingers,
    /// so Interact is disabled for them unless the manager's allowVRTrigger is on. A button with no manager can't be
    /// clicked at all. Called by the manager whenever it pushes its settings, so ApplySettings can toggle it at runtime.
    /// </summary>
    public void _UpdateInteractive()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        localIsVR = localPlayer != null && localPlayer.IsUserInVR();
        if (manager == null || (localIsVR && !allowVRTrigger))
        {
            DisableInteractive = true;
            return;
        }
        DisableInteractive = false;
        InteractionText = interactText;
    }

    /// <summary>
    /// Grows the per-slot state arrays to the manager's slot count, keeping the state already there.
    /// </summary>
    private void MatchSlotCount(int slotCount)
    {
        if (seenGenerations.Length >= slotCount) return;
        bool[] engaged = new bool[slotCount * FingertipsPerPlayer];
        bool[] wasInFront = new bool[slotCount * FingertipsPerPlayer];
        int[] generations = new int[slotCount];
        for (int i = 0; i < fingertipEngaged.Length; i++)
        {
            engaged[i] = fingertipEngaged[i];
            wasInFront[i] = fingertipWasInFront[i];
        }
        for (int i = 0; i < seenGenerations.Length; i++) generations[i] = seenGenerations[i];
        fingertipEngaged = engaged;
        fingertipWasInFront = wasInFront;
        seenGenerations = generations;
    }

    /// <summary>
    /// A desktop or mobile click, or a VR trigger click (allowVRTrigger): the same click animation, network event and
    /// action either way, with no haptics. Interact runs when the use input goes down; the click then holds the
    /// bottom until the input is released (see InputUse). Ignored while Interact is disabled, and while the local
    /// player's own fingertip (or the debug fingertip) is pushing the button in, so a hand pressing it can't click it
    /// as well. Remote fingertips never block it, so other players can't stop someone using the button by holding it in.
    /// </summary>
    public override void Interact()
    {
        if (!IsLive() || DisableInteractive || localPress > 0f) return;
        StartClickAnimation(true, -1);
        SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetPlayClickAnimation));
    }

    /// <summary>
    /// Udon sends every use event to every button, so this returns straight away unless this button is holding a
    /// local click. Any use-up releases it: the click finishes pressing in if it hasn't yet, then springs back, the
    /// other clients are told with NetReleaseClick, and the release action fires if the press has.
    /// </summary>
    public override void InputUse(bool value, UdonInputEventArgs args)
    {
        if (value || !clickHeld || !clickIsLocal) return;
        clickHeld = false;
        SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetReleaseClick));
        if (releasePending) FireRelease();
    }

    /// <summary>
    /// Network event from another player's click: plays the same click animation, flagged non-local so it never
    /// fires the action, held at the bottom until that player's NetReleaseClick. Ignored when the local player is out
    /// of range, and while the button is disabled or its manager isn't running. Deliberately doesn't check whether the
    /// local player is in VR: every viewer sees the click, whatever their platform.
    /// </summary>
    public void NetPlayClickAnimation()
    {
        if (!IsLive()) return;
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (localPlayer != null && (localPlayer.GetPosition() - transform.position).sqrMagnitude > PushButtonManager.AnimationRangeSqr) return;
        // Don't turn a local click that is still pressing in or held into a non-local one, or it would never fire.
        if (clickActive && clickIsLocal) return;
        StartClickAnimation(false, CallerId());
    }

    /// <summary>
    /// Network event from the presser of a remote click releasing its input: the click finishes pressing in if it
    /// hasn't yet, then springs back. A release from anyone else is ignored, so it can't cut another player's hold short.
    /// </summary>
    public void NetReleaseClick()
    {
        if (!clickHeld || clickIsLocal || CallerId() != clickPresserId) return;
        clickHeld = false;
    }

    /// <summary>
    /// Releases a remote click held by a player who leaves, since their NetReleaseClick will never come.
    /// </summary>
    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (clickHeld && !clickIsLocal && player != null && player.playerId == clickPresserId) clickHeld = false;
    }

    /// <summary>
    /// The player ID of the network event's sender, or -1 if there is none.
    /// </summary>
    private int CallerId()
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        return Utilities.IsValid(caller) ? caller.playerId : -1;
    }

    /// <summary>
    /// Starts (or restarts) the click animation, stamped with Time.time and held until released. Only a local click
    /// fires the action. presserId is a remote click's presser (-1 for a local click or an unknown sender).
    /// </summary>
    private void StartClickAnimation(bool isLocal, int presserId)
    {
        clickActive = true;
        clickIsLocal = isLocal;
        clickHeld = true;
        clickPresserId = presserId;
        clickStartTime = Time.time;
    }

    /// <summary>
    /// Drives clickPress from the running click animation: in over clickPressDuration, at the bottom while held (and
    /// for at least one frame), then released to 0 so the button springs back at returnSpeed.
    /// </summary>
    private void UpdateClickPress()
    {
        if (!clickActive)
        {
            clickPress = 0f;
            return;
        }
        float progress = clickPressDuration > 0f ? (Time.time - clickStartTime) / clickPressDuration : 1f;
        if (progress >= 1f)
        {
            clickPress = travelDistance;
            if (!clickHeld) clickActive = false;
            return;
        }
        clickPress = travelDistance * Mathf.Max(progress, 0f);
    }

    /// <summary>
    /// One frame of the button, called by the manager while the local player is in range, after it has built this
    /// frame's fingertip cache: rest pose, fingertip presses, click, trigger, press amount, then the new position.
    /// A button without a manager is never registered, so it never ticks: it never moves and never fires.
    /// </summary>
    public void _Tick()
    {
        outOfRange = false;
        UpdateRestWorldPose();
        UpdateFingertipPresses();
        UpdateClickPress();
        float localClickPress = clickIsLocal ? clickPress : 0f;
        bool acted = UpdateTrigger(Mathf.Clamp(Mathf.Max(localPress, localClickPress), 0f, travelDistance));
        // The press or release action can disable this button or the manager, either of which has already put the
        // button at rest: stop here so it doesn't move again.
        if (acted && (!enabledNow || !manager.running)) return;
        UpdatePressAmount();
        ApplyPress(pressAmount);
    }

    /// <summary>
    /// Called by the manager once when the local player goes beyond its AnimationRange, and when the manager is
    /// disabled: snaps the button to rest (see ResetToRest). The manager doesn't tick the button again until the
    /// player is back in range (or the manager is enabled again).
    /// </summary>
    public void _LeaveRange()
    {
        outOfRange = true;
        ResetToRest();
    }

    /// <summary>
    /// Snaps the button to its rest pose (rebuilt from the parent now) and clears the engagement, click and press
    /// state, so nothing is left half-pressed and a finger still inside the button has to come in from the front again.
    /// A press whose release action hasn't fired yet gets it now, so every press gets its release.
    /// </summary>
    private void ResetToRest()
    {
        resetCount++;
        UpdateRestWorldPose();

        for (int i = 0; i < fingertipEngaged.Length; i++)
        {
            fingertipEngaged[i] = false;
            fingertipWasInFront[i] = false;
        }
        debugEngaged = false;
        debugWasInFront = false;
        clickActive = false;
        clickIsLocal = false;
        clickHeld = false;
        clickPresserId = -1;
        localPress = 0f;
        remotePress = 0f;
        clickPress = 0f;
        localPressHand = VRC_Pickup.PickupHand.None;
        // The local press is now 0, below the re-arm depth.
        armed = true;
        pressAmount = 0f;
        // Always write here, once: the rest pose may have changed (the parent moved while the button was disabled).
        appliedPress = -1f;
        ApplyPress(0f);
        // Last, so the button is already at rest if the release action looks at it or changes it.
        if (releasePending) FireRelease();
    }

    /// <summary>
    /// Sets localPress from the local player's fingertips (and the debug fingertip) and remotePress from everyone
    /// else's: the deepest press of any engaged fingertip. The fingertips come from the manager's per-frame cache,
    /// which only fills them in for players in VR (judged by each player's own IsUserInVR, so a remote VR player moves
    /// the button on every client, VR and desktop alike) with a hand near an active button. Desktop and mobile players
    /// click instead, and their avatars' animated hands never press. Each fingertip's radius is fingertipRadius scaled
    /// to the avatar. The debug fingertip counts as the local player's whatever their platform. localPressHand records
    /// the hand of the deepest local press.
    /// </summary>
    private void UpdateFingertipPresses()
    {
        float local = 0f;
        float remote = 0f;
        VRC_Pickup.PickupHand localHand = VRC_Pickup.PickupHand.None;
        Quaternion toRest = Quaternion.Inverse(restWorldRot);
        // Each of the manager's arrays is read once per frame, not per element.
        VRCPlayerApi[] players = manager.slotPlayers;
        int[] generations = manager.slotGenerations;
        bool[] near = manager.slotNear;
        bool[] isLocal = manager.slotIsLocal;
        float[] scales = manager.slotScale;
        Vector3[] tips = manager.fingertipPositions;
        bool[] tipValid = manager.fingertipValid;
        MatchSlotCount(players.Length);
        // The cache is sized to the slots when it's built; a slot added since then has no cache entry yet.
        int cached = Mathf.Min(players.Length, near.Length);
        for (int i = 0; i < players.Length; i++)
        {
            if (seenGenerations[i] != generations[i])
            {
                // The slot has a new player since this button last looked: drop the old player's fingertip state.
                seenGenerations[i] = generations[i];
                for (int f = 0; f < FingertipsPerPlayer; f++)
                {
                    fingertipEngaged[i * FingertipsPerPlayer + f] = false;
                    fingertipWasInFront[i * FingertipsPerPlayer + f] = false;
                }
            }
            // Not near: not a VR player, or both hands too far from every active button to reach one. Its fingertips
            // are cleared like a non-pressing player's.
            bool pressing = i < cached && near[i];
            float radius = pressing ? fingertipRadius * scales[i] : 0f;
            for (int f = 0; f < FingertipsPerPlayer; f++)
            {
                int slot = i * FingertipsPerPlayer + f;
                bool used = f < 2 ? useIndexFingers : useMiddleFingers;
                if (!pressing || !used || !tipValid[slot])
                {
                    fingertipEngaged[slot] = false;
                    fingertipWasInFront[slot] = false;
                    continue;
                }
                tipEngaged = fingertipEngaged[slot];
                tipWasInFront = fingertipWasInFront[slot];
                float press = EvaluateFingertip(toRest, tips[slot], radius);
                fingertipEngaged[slot] = tipEngaged;
                fingertipWasInFront[slot] = tipWasInFront;
                if (isLocal[i])
                {
                    if (press > local)
                    {
                        local = press;
                        // Even slots are left-hand fingers, odd slots right-hand ones.
                        localHand = f % 2 == 0 ? VRC_Pickup.PickupHand.Left : VRC_Pickup.PickupHand.Right;
                    }
                }
                else remote = Mathf.Max(remote, press);
            }
        }

#if UNITY_EDITOR
        // UdonSharp drops UNITY_EDITOR when compiling for a build, so uploaded worlds never read the debug fingertip.
        Transform debugFingertip = manager.debugFingertip;
        if (debugFingertip != null)
        {
            tipEngaged = debugEngaged;
            tipWasInFront = debugWasInFront;
            float debugPress = EvaluateFingertip(toRest, debugFingertip.position, fingertipRadius);
            if (debugPress > local)
            {
                local = debugPress;
                localHand = VRC_Pickup.PickupHand.None;
            }
            debugEngaged = tipEngaged;
            debugWasInFront = tipWasInFront;
        }
        else
        {
            debugEngaged = false;
            debugWasInFront = false;
        }
#endif

        localPress = local;
        remotePress = remote;
        localPressHand = localHand;
    }

    /// <summary>
    /// Updates one fingertip's engagement (tipEngaged, tipWasInFront) and returns its press in meters. Works in the
    /// rest frame: a tip engages only if it was in front of the face and inside the footprint (grown by its radius)
    /// on the previous frame and is now past the face. It stays engaged until it leaves the footprint sideways or
    /// pulls back out in front, so entering from the side or the back never engages.
    /// </summary>
    private float EvaluateFingertip(Quaternion toRest, Vector3 tip, float radius)
    {
        Vector3 p = toRest * (tip - restWorldPos);
        // How far the sphere's leading point is past the front face; positive means it touches or is past the face.
        float press = Vector3.Dot(p, pressAxis) + frontFaceOffset + radius;
        bool inFootprint = Mathf.Abs(Vector3.Dot(p, lateralAxisU) - footprintCenterU) <= footprintHalfU + radius
            && Mathf.Abs(Vector3.Dot(p, lateralAxisV) - footprintCenterV) <= footprintHalfV + radius;
        bool pastFace = press > 0f;
        tipEngaged = inFootprint && pastFace && (tipEngaged || tipWasInFront);
        tipWasInFront = inFootprint && !pastFace;
        return tipEngaged ? Mathf.Clamp(press, 0f, travelDistance) : 0f;
    }

    /// <summary>
    /// Fires the action when the local press reaches the bottom while armed, and re-arms once it rises back to
    /// releaseFraction of the travel, firing the release action then if it hasn't fired yet (which is how fingertip
    /// presses release). Only local presses (fingertips and the local click) come through here, so remote presses and
    /// networked clicks never fire. Returns whether it ran the press or the release action.
    /// </summary>
    private bool UpdateTrigger(float localAmount)
    {
        float fireDepth = travelDistance - FireTolerance;
        if (armed)
        {
            if (localAmount >= fireDepth)
            {
                armed = false;
                // Haptics before the action, which can disable the button and so clear localPressHand.
                if (localPress >= fireDepth) PlayPressHaptics();
                int resets = resetCount;
                FireAction();
                // An action that reset the button (disabled it, or the manager) has already ended the press.
                if (resetCount != resets) FireRelease();
                else releasePending = true;
                return true;
            }
        }
        else if (localAmount <= travelDistance * releaseFraction && localAmount < fireDepth)
        {
            armed = true;
            if (releasePending)
            {
                FireRelease();
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sends the configured event to the target behaviour, or warns if the target or the event name is unset.
    /// </summary>
    private void FireAction()
    {
        if (targetBehaviour == null)
        {
            Debug.LogWarning("[PushButton] " + name + ": pressed, but no target behaviour is set");
            return;
        }
        if (string.IsNullOrEmpty(eventName))
        {
            Debug.LogWarning("[PushButton] " + name + ": pressed, but no event name is set");
            return;
        }
        targetBehaviour.SendCustomEvent(eventName);
    }

    /// <summary>
    /// Sends the release event to the target behaviour, once per fired press. An unset target or release event name
    /// sends nothing, without a warning: the release action is optional.
    /// </summary>
    private void FireRelease()
    {
        releasePending = false;
        if (targetBehaviour == null || string.IsNullOrEmpty(releaseEventName)) return;
        targetBehaviour.SendCustomEvent(releaseEventName);
    }

    /// <summary>
    /// Pulses the hand whose fingertip pressed the button, at hapticStrength. Skipped when the strength is 0, the local
    /// player isn't in VR, or no hand pressed (a click, or the debug fingertip).
    /// </summary>
    private void PlayPressHaptics()
    {
        if (hapticStrength <= 0f || localPressHand == VRC_Pickup.PickupHand.None) return;
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (!Utilities.IsValid(localPlayer) || !localPlayer.IsUserInVR()) return;
        localPlayer.PlayHapticEventInHand(localPressHand, HapticDuration, hapticStrength, HapticFrequency);
    }

    /// <summary>
    /// Combines the press sources: the button jumps straight to a deeper press and springs back at returnSpeed.
    /// </summary>
    private void UpdatePressAmount()
    {
        float target = Mathf.Clamp(Mathf.Max(localPress, Mathf.Max(remotePress, clickPress)), 0f, travelDistance);
        if (target >= pressAmount) pressAmount = target;
        else pressAmount = Mathf.Clamp(Mathf.MoveTowards(pressAmount, target, returnSpeed * Time.deltaTime), 0f, travelDistance);
    }

    /// <summary>
    /// Moves the button from its rest pose along the press axis by the given amount in meters. Skipped when the
    /// amount is the one last written, since the button is a child of its parent and so already in place.
    /// </summary>
    private void ApplyPress(float amount)
    {
        if (amount == appliedPress) return;
        appliedPress = amount;
        transform.position = restWorldPos + restWorldRot * (pressAxis * amount);
    }

    /// <summary>
    /// Works out the contact box from the button's own BoxCollider (or its mesh bounds), in meters in the rest frame:
    /// the front face offset along the press axis and the footprint on the two perpendicular axes.
    /// </summary>
    private void ComputeContactBox(bool warn)
    {
        pressAxis = localPressAxis;
        if (pressAxis.sqrMagnitude < 1e-12f)
        {
            if (warn) Debug.LogWarning("[PushButton] " + name + ": localPressAxis is zero, using (0, 0, -1)");
            pressAxis = new Vector3(0f, 0f, -1f);
        }
        pressAxis = pressAxis.normalized;

        // Perpendicular axes: cross with the local axis least aligned with the press axis.
        Vector3 reference = Vector3.right;
        float ax = Mathf.Abs(pressAxis.x);
        float ay = Mathf.Abs(pressAxis.y);
        float az = Mathf.Abs(pressAxis.z);
        if (ay <= ax && ay <= az) reference = Vector3.up;
        else if (az <= ax && az <= ay) reference = Vector3.forward;
        lateralAxisU = Vector3.Cross(pressAxis, reference).normalized;
        lateralAxisV = Vector3.Cross(pressAxis, lateralAxisU);

        Vector3 center = Vector3.zero;
        Vector3 size = Vector3.zero;
        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
        {
            center = box.center;
            size = box.size;
        }
        else
        {
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                Bounds bounds = meshFilter.sharedMesh.bounds;
                center = bounds.center;
                size = bounds.size;
            }
            else if (warn)
            {
                Debug.LogWarning("[PushButton] " + name + ": no BoxCollider or mesh to take the contact box from");
            }
        }

        Vector3 scale = transform.lossyScale;
        Vector3 centerMeters = Vector3.Scale(center, scale);
        Vector3 halfMeters = Vector3.Scale(size * 0.5f, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

        contactCenter = centerMeters;
        contactHalfExtents = halfMeters;
        fingerCheckLocalCenter = center;
        frontFaceOffset = BoxSupport(halfMeters, pressAxis) - Vector3.Dot(centerMeters, pressAxis);
        footprintCenterU = Vector3.Dot(centerMeters, lateralAxisU);
        footprintCenterV = Vector3.Dot(centerMeters, lateralAxisV);
        footprintHalfU = BoxSupport(halfMeters, lateralAxisU);
        footprintHalfV = BoxSupport(halfMeters, lateralAxisV);
        float handRange = PushButtonManager.HandEarlyOutDistance + halfMeters.magnitude;
        fingerCheckRangeSqr = handRange * handRange;
    }

    /// <summary>
    /// Half-extent of an axis-aligned box (given by its half-extents) along a unit direction.
    /// </summary>
    private float BoxSupport(Vector3 half, Vector3 direction)
    {
        return Mathf.Abs(direction.x) * half.x + Mathf.Abs(direction.y) * half.y + Mathf.Abs(direction.z) * half.z;
    }

    /// <summary>
    /// Rebuilds the world-space rest pose from the parent's current transform (or world space if there is no parent).
    /// </summary>
    private void UpdateRestWorldPose()
    {
        Transform parent = transform.parent;
        if (parent == null)
        {
            restWorldPos = restLocalPosition;
            restWorldRot = restLocalRotation;
            return;
        }
        restWorldPos = parent.TransformPoint(restLocalPosition);
        restWorldRot = parent.rotation * restLocalRotation;
    }

#if !COMPILER_UDONSHARP && UNITY_EDITOR
    // Editor-only gizmos: the press axis arrow, the contact box at rest, and a wire box at full travel.
    // UdonSharp skips this block, so it runs as plain C# on the editor-side component.

    private void OnDrawGizmosSelected()
    {
        Vector3 restPos;
        Quaternion restRot;
        Vector3 boxCenter;
        Vector3 boxSize;
        Vector3 fullCenter;
        if (!GetGizmoGeometry(out restPos, out restRot, out boxCenter, out boxSize, out fullCenter)) return;

        Matrix4x4 previousMatrix = Gizmos.matrix;
        Color previousColor = Gizmos.color;
        Gizmos.matrix = Matrix4x4.TRS(restPos, restRot, Vector3.one);

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.8f);
        Gizmos.DrawWireCube(boxCenter, boxSize);
        Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.9f);
        Gizmos.DrawWireCube(fullCenter, boxSize);

        // Arrow along the press axis, ending at the front face.
        Vector3 faceCenter = footprintCenterU * lateralAxisU + footprintCenterV * lateralAxisV - frontFaceOffset * pressAxis;
        float length = Mathf.Max(travelDistance * 2f, 0.02f);
        float head = length * 0.25f;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(faceCenter - pressAxis * length, faceCenter);
        Gizmos.DrawLine(faceCenter, faceCenter - pressAxis * head + lateralAxisU * head * 0.5f);
        Gizmos.DrawLine(faceCenter, faceCenter - pressAxis * head - lateralAxisU * head * 0.5f);
        Gizmos.DrawLine(faceCenter, faceCenter - pressAxis * head + lateralAxisV * head * 0.5f);
        Gizmos.DrawLine(faceCenter, faceCenter - pressAxis * head - lateralAxisV * head * 0.5f);

        Gizmos.matrix = previousMatrix;
        DrawFingertipGizmos();
        Gizmos.color = previousColor;
    }

    /// <summary>
    /// Editor only, in Play Mode: a wire sphere of the scaled radius at each fingertip in the manager's per-frame cache
    /// that this button uses (only VR players with a hand near an active button have any). The radius and the finger
    /// choice are the running button's copies of the manager's settings.
    /// </summary>
    private void DrawFingertipGizmos()
    {
        if (!Application.isPlaying) return;
        UdonBehaviour udon = GetComponent<UdonBehaviour>();
        UdonBehaviour managerUdon = udon != null ? udon.GetProgramVariable("manager") as UdonBehaviour : null;
        if (managerUdon == null) return;
        Vector3[] tips = managerUdon.GetProgramVariable("fingertipPositions") as Vector3[];
        bool[] valid = managerUdon.GetProgramVariable("fingertipValid") as bool[];
        float[] scales = managerUdon.GetProgramVariable("slotScale") as float[];
        object radius = udon.GetProgramVariable("fingertipRadius");
        object useIndex = udon.GetProgramVariable("useIndexFingers");
        object useMiddle = udon.GetProgramVariable("useMiddleFingers");
        if (tips == null || valid == null || scales == null || !(radius is float) || !(useIndex is bool) || !(useMiddle is bool)) return;
        Gizmos.color = new Color(0.3f, 1f, 0.3f, 0.9f);
        for (int t = 0; t < valid.Length && t < tips.Length; t++)
        {
            int s = t / FingertipsPerPlayer;
            bool used = t % FingertipsPerPlayer < 2 ? (bool)useIndex : (bool)useMiddle;
            if (valid[t] && used && s < scales.Length) Gizmos.DrawWireSphere(tips[t], (float)radius * scales[s]);
        }
    }

    /// <summary>
    /// Editor only: the gizmo geometry. The rest pose is in world space; the box centres (at rest and at full
    /// travel) and the box size are in meters in the rest frame. In Edit Mode the current transform is the rest
    /// pose; in Play Mode it comes from the running Udon program. Returns false if there is nothing to draw.
    /// </summary>
    public bool GetGizmoGeometry(out Vector3 restPos, out Quaternion restRot, out Vector3 boxCenter, out Vector3 boxSize, out Vector3 fullCenter)
    {
        restPos = transform.position;
        restRot = transform.rotation;
        boxCenter = Vector3.zero;
        boxSize = Vector3.zero;
        fullCenter = Vector3.zero;
        if (localPressAxis.sqrMagnitude < 1e-12f) return false;

        if (Application.isPlaying)
        {
            UdonBehaviour udon = GetComponent<UdonBehaviour>();
            object restLocalPos = udon != null ? udon.GetProgramVariable("restLocalPosition") : null;
            object restLocalRot = udon != null ? udon.GetProgramVariable("restLocalRotation") : null;
            if (restLocalPos is Vector3 && restLocalRot is Quaternion)
            {
                Transform parent = transform.parent;
                restPos = parent != null ? parent.TransformPoint((Vector3)restLocalPos) : (Vector3)restLocalPos;
                restRot = parent != null ? parent.rotation * (Quaternion)restLocalRot : (Quaternion)restLocalRot;
            }
        }

        ComputeContactBox(false);
        if (contactHalfExtents == Vector3.zero) return false;
        boxCenter = contactCenter;
        boxSize = contactHalfExtents * 2f;
        fullCenter = contactCenter + pressAxis * travelDistance;
        return true;
    }
#endif
}
