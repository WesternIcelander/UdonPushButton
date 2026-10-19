using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Turns any object into a physically pushable button: VR players push it with their fingertips,
/// desktop and mobile players click it. Only the presser's client runs the configured action.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class PushButton : UdonSharpBehaviour
{
    // C#'s equivalent of Java's `static final` for a primitive is `const`.
    // UdonSharp supports `const`; non-const static fields are not supported reliably.
    private const float AnimationRange = 5f;   // meters
    private const float AnimationRangeSqr = AnimationRange * AnimationRange;

    [Header("Setup")]
    [Tooltip("Required. The scene's PushButtonManager. Without one the button does nothing.")]
    public PushButtonManager manager;

    [Header("Action")]
    [Tooltip("Behaviour to call when the button is pressed (UdonSharp or Graph program).")]
    public UdonBehaviour targetBehaviour;
    [Tooltip("Custom event sent to the target behaviour. Runs on the presser's client only.")]
    public string eventName;

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

    [Header("VR")]
    [Tooltip("Fingertip contact radius in meters, scaled by each avatar's size.")]
    public float fingertipRadius = 0.008f;
    [Tooltip("Index fingertips can press the button.")]
    public bool useIndexFingers = true;
    [Tooltip("Middle fingertips can press the button.")]
    public bool useMiddleFingers = false;
    [Tooltip("Haptic pulse strength on the local pressing hand (0 = off).")]
    [Range(0f, 1f)]
    public float hapticStrength = 0.3f;

    [Header("Desktop")]
    [Tooltip("Seconds for the click press-in animation.")]
    public float clickPressDuration = 0.12f;
    [Tooltip("Interact tooltip text.")]
    public string interactText = "Press";

    [Header("Debug")]
    [Tooltip("Editor only: acts as a local fingertip for testing without a headset. Ignored in builds.")]
    public Transform debugFingertip;

    // Rest pose relative to the parent, captured once in Start.
    private Vector3 restLocalPosition;
    private Quaternion restLocalRotation;
    // Rest pose in world space, rebuilt every frame so a moving parent carries the button along.
    private Vector3 restWorldPos;
    private Quaternion restWorldRot;

    // Contact box in the button's rest frame (rotation only, in meters), set up once in Start.
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
    // Firing disarms the button; the local press rising back to releaseFraction of the travel re-arms it.
    private bool armed = true;
    // Meters short of travelDistance at which the local press counts as reaching the bottom.
    private const float FireTolerance = 0.0005f;
    // Whether the local player is in VR, set once in Start: VR players press with their fingers, everyone else clicks.
    private bool localIsVR;
    // The click animation: clickPress runs 0 -> travelDistance over clickPressDuration from clickStartTime, holds the
    // bottom for one frame, then releases and springs back. Only a local click (clickIsLocal) can fire the action.
    private bool clickActive;
    private bool clickIsLocal;
    private float clickStartTime;

    // Fingertip slots per player, in the manager's order: left index, right index, left middle, right middle.
    private const int FingertipsPerPlayer = PushButtonManager.FingertipsPerPlayer;
    // Per-fingertip state for each of the manager's player slots: fingertip f of slot i is at index
    // i * FingertipsPerPlayer + f. seenGenerations[i] is the slot's generation the state belongs to; when the manager
    // gives the slot a new player, the state is cleared so the new player never inherits a press.
    private bool[] fingertipEngaged = new bool[0];
    private bool[] fingertipWasInFront = new bool[0];
    private int[] seenGenerations = new int[0];

    // What the manager reads to decide whose finger bones to read: whether this button wants fingertips this frame
    // (registered and in range), and the sphere a hand bone has to be in for that, centred on the contact box.
    [System.NonSerialized] public bool fingerCheckActive;
    [System.NonSerialized] public Vector3 fingerCheckCenter;
    [System.NonSerialized] public float fingerCheckRangeSqr;
    // Whether this button has registered with the manager since it was last enabled.
    private bool registered;
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
    // Whether the local player was beyond AnimationRange on the last frame, so the reset to rest runs only once.
    private bool outOfRange;

    private void Start()
    {
#if !UNITY_EDITOR
        // UdonSharp drops UNITY_EDITOR when compiling for a build, so uploaded worlds never use the debug fingertip.
        debugFingertip = null;
#endif

        restLocalPosition = transform.localPosition;
        restLocalRotation = transform.localRotation;
        UpdateRestWorldPose();
        ComputeContactBox(true);
        SetUpLocalInput();
        if (manager == null)
        {
            Debug.LogWarning("[PushButton] " + name + ": no manager is set, so the button does nothing");
            DisableInteractive = true;
        }
    }

    /// <summary>
    /// Makes the collider a trigger so it never blocks hands or players. VR players press with their fingers, so
    /// Interact is disabled for them; desktop and mobile players (or no local player at all) click it instead.
    /// </summary>
    private void SetUpLocalInput()
    {
        Collider ownCollider = GetComponent<Collider>();
        if (ownCollider != null) ownCollider.isTrigger = true;
        else Debug.LogWarning("[PushButton] " + name + ": no collider, so desktop and mobile players can't click it");

        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        localIsVR = localPlayer != null && localPlayer.IsUserInVR();
        if (localIsVR)
        {
            DisableInteractive = true;
        }
        else
        {
            DisableInteractive = false;
            InteractionText = interactText;
        }
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

    private void OnDisable()
    {
        // The manager stops reading this button's fingers. The rest of the disable rules come with plan step 8.4.
        fingerCheckActive = false;
        if (registered && Utilities.IsValid(manager)) manager.Unregister(this);
        registered = false;
    }

    public override void Interact()
    {
        if (manager == null) return;
        StartClickAnimation(true);
        SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetPlayClickAnimation));
    }

    /// <summary>
    /// Network event from another player's click: plays the same click animation, flagged non-local so it never
    /// fires the action. Ignored when the local player is out of range. Deliberately doesn't check whether the local
    /// player is in VR: every viewer sees the click, whatever their platform.
    /// </summary>
    public void NetPlayClickAnimation()
    {
        if (manager == null) return;
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (localPlayer != null && (localPlayer.GetPosition() - transform.position).sqrMagnitude > AnimationRangeSqr) return;
        // Don't turn a local click that is still pressing in into a non-local one, or it would never fire.
        if (clickActive && clickIsLocal) return;
        StartClickAnimation(false);
    }

    /// <summary>
    /// Starts (or restarts) the click animation, stamped with Time.time. Only a local click fires the action.
    /// </summary>
    private void StartClickAnimation(bool isLocal)
    {
        clickActive = true;
        clickIsLocal = isLocal;
        clickStartTime = Time.time;
    }

    /// <summary>
    /// Drives clickPress from the running click animation: in over clickPressDuration, one frame at the bottom,
    /// then released to 0 so the button springs back at returnSpeed.
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
            clickActive = false;
            return;
        }
        clickPress = travelDistance * Mathf.Max(progress, 0f);
    }

    private void Update()
    {
        // Without a manager the button is inert: it never moves and never fires.
        if (manager == null) return;
        if (!registered)
        {
            manager.Register(this);
            registered = true;
        }
        UpdateRestWorldPose();
        if (!UpdateRangeGate()) return;
        fingerCheckCenter = restWorldPos + restWorldRot * contactCenter;
        fingerCheckActive = true;
        manager.PrepareFrame();
        UpdateFingertipPresses();
        UpdateClickPress();
        float localClickPress = clickIsLocal ? clickPress : 0f;
        UpdateTrigger(Mathf.Clamp(Mathf.Max(localPress, localClickPress), 0f, travelDistance));
        UpdatePressAmount();
        ApplyPress(pressAmount);
    }

    /// <summary>
    /// Returns false while the local player is farther than AnimationRange from the button, so Update skips the frame.
    /// On the first out-of-range frame it snaps the button to rest and clears the engagement and click state, so
    /// nothing is left half-pressed and a finger still inside the button has to come in from the front again.
    /// A missing local player counts as in range.
    /// </summary>
    private bool UpdateRangeGate()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        bool inRange = localPlayer == null || (localPlayer.GetPosition() - restWorldPos).sqrMagnitude <= AnimationRangeSqr;
        if (inRange)
        {
            outOfRange = false;
            return true;
        }
        if (outOfRange) return false;
        outOfRange = true;
        fingerCheckActive = false;

        for (int i = 0; i < fingertipEngaged.Length; i++)
        {
            fingertipEngaged[i] = false;
            fingertipWasInFront[i] = false;
        }
        debugEngaged = false;
        debugWasInFront = false;
        clickActive = false;
        clickIsLocal = false;
        localPress = 0f;
        remotePress = 0f;
        clickPress = 0f;
        localPressHand = VRC_Pickup.PickupHand.None;
        // The local press is now 0, below the re-arm depth.
        armed = true;
        pressAmount = 0f;
        ApplyPress(0f);
        return false;
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
    /// releaseFraction of the travel. Only local presses (fingertips and the local click) come through here, so
    /// remote presses and networked clicks never fire.
    /// </summary>
    private void UpdateTrigger(float localAmount)
    {
        float fireDepth = travelDistance - FireTolerance;
        if (armed)
        {
            if (localAmount >= fireDepth)
            {
                armed = false;
                FireAction();
                if (localPress >= fireDepth) PlayPressHaptics();
            }
        }
        else if (localAmount <= travelDistance * releaseFraction && localAmount < fireDepth)
        {
            armed = true;
        }
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
    /// Moves the button from its rest pose along the press axis by the given amount in meters.
    /// </summary>
    private void ApplyPress(float amount)
    {
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
    /// that this button uses (only VR players with a hand near an active button have any).
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
        if (tips == null || valid == null || scales == null) return;
        Gizmos.color = new Color(0.3f, 1f, 0.3f, 0.9f);
        for (int t = 0; t < valid.Length && t < tips.Length; t++)
        {
            int s = t / FingertipsPerPlayer;
            bool used = t % FingertipsPerPlayer < 2 ? useIndexFingers : useMiddleFingers;
            if (valid[t] && used && s < scales.Length) Gizmos.DrawWireSphere(tips[t], fingertipRadius * scales[s]);
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
