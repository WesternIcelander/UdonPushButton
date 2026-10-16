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

    // Fingertip slots per player: left index, right index, left middle, right middle.
    private const int FingertipsPerPlayer = 4;
    // Everyone in the instance, refreshed on join and leave (never in Update), with per-player, per-fingertip state
    // in parallel arrays: fingertip f of trackedPlayers[i] is at index i * FingertipsPerPlayer + f.
    private VRCPlayerApi[] trackedPlayers = new VRCPlayerApi[0];
    private bool[] fingertipEngaged = new bool[0];
    private bool[] fingertipWasInFront = new bool[0];

    // The fingertip lies this fraction of the last finger segment (intermediate -> distal) beyond the distal bone.
    private const float FingertipExtrapolation = 0.8f;
    // Avatar eye height in meters at which fingertipRadius applies unscaled; other avatars scale it in proportion.
    private const float ReferenceEyeHeight = 1.6f;
    // Filled by GetFingertips for one player, in the same slot order as the state arrays: world positions, which
    // slots hold a usable tip (an enabled finger on an avatar with the bones), and the radius scaled to the avatar.
    private Vector3[] fingertipPositions = new Vector3[FingertipsPerPlayer];
    private bool[] fingertipValid = new bool[FingertipsPerPlayer];
    private float fingertipScaledRadius;
    // Engagement state of the debug fingertip, which isn't a tracked player's slot.
    private bool debugEngaged;
    private bool debugWasInFront;
    // One fingertip's engagement state while EvaluateFingertip updates it (UdonSharp has no ref parameters).
    private bool tipEngaged;
    private bool tipWasInFront;

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

        // TEMP (plan step 2.1): remove once later steps use the rest pose.
        Debug.Log("[PushButton] rest local " + restLocalPosition.ToString("F4") + " " + restLocalRotation.eulerAngles.ToString("F2")
            + ", rest world " + restWorldPos.ToString("F4") + " " + restWorldRot.eulerAngles.ToString("F2"));
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

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        RefreshPlayers(null);
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        // The leaving player is still in the player list during this event, so leave them out explicitly.
        RefreshPlayers(player);
    }

    /// <summary>
    /// Rebuilds the cached player list (without the leaving player, if any) and the per-fingertip state arrays,
    /// carrying over the state of players who were already tracked so a join or leave doesn't drop a press.
    /// </summary>
    private void RefreshPlayers(VRCPlayerApi leaving)
    {
        VRCPlayerApi[] all = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(all);
        int count = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (Utilities.IsValid(all[i]) && all[i] != leaving) count++;
        }

        VRCPlayerApi[] players = new VRCPlayerApi[count];
        bool[] engaged = new bool[count * FingertipsPerPlayer];
        bool[] wasInFront = new bool[count * FingertipsPerPlayer];
        int next = 0;
        for (int i = 0; i < all.Length; i++)
        {
            VRCPlayerApi player = all[i];
            if (!Utilities.IsValid(player) || player == leaving) continue;
            players[next] = player;
            for (int old = 0; old < trackedPlayers.Length; old++)
            {
                if (trackedPlayers[old] != player) continue;
                for (int f = 0; f < FingertipsPerPlayer; f++)
                {
                    engaged[next * FingertipsPerPlayer + f] = fingertipEngaged[old * FingertipsPerPlayer + f];
                    wasInFront[next * FingertipsPerPlayer + f] = fingertipWasInFront[old * FingertipsPerPlayer + f];
                }
                break;
            }
            next++;
        }
        trackedPlayers = players;
        fingertipEngaged = engaged;
        fingertipWasInFront = wasInFront;

        // TEMP (plan step 5.1): remove once the fingertip steps use the tracked players.
        Debug.Log("[PushButton] " + name + ": tracking " + count + " players");
    }

    /// <summary>
    /// Estimates the player's enabled fingertips into fingertipPositions and fingertipValid (slots: left index, right
    /// index, left middle, right middle), and sets fingertipScaledRadius from their avatar's eye height.
    /// </summary>
    private void GetFingertips(VRCPlayerApi player)
    {
        fingertipValid[0] = useIndexFingers && EstimateFingertip(player, 0, HumanBodyBones.LeftIndexDistal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftHand);
        fingertipValid[1] = useIndexFingers && EstimateFingertip(player, 1, HumanBodyBones.RightIndexDistal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightHand);
        fingertipValid[2] = useMiddleFingers && EstimateFingertip(player, 2, HumanBodyBones.LeftMiddleDistal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftHand);
        fingertipValid[3] = useMiddleFingers && EstimateFingertip(player, 3, HumanBodyBones.RightMiddleDistal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightHand);

        float eyeHeight = player.GetAvatarEyeHeightAsMeters();
        fingertipScaledRadius = eyeHeight > 0f ? fingertipRadius * eyeHeight / ReferenceEyeHeight : fingertipRadius;
    }

    /// <summary>
    /// Puts one fingertip estimate in fingertipPositions[slot]: the distal bone extended along the last finger segment,
    /// or the hand bone if the avatar lacks those finger bones. Returns false if it lacks the hand bone too.
    /// GetBonePosition returns zero for a missing bone.
    /// </summary>
    private bool EstimateFingertip(VRCPlayerApi player, int slot, HumanBodyBones distalBone, HumanBodyBones intermediateBone, HumanBodyBones handBone)
    {
        Vector3 distal = player.GetBonePosition(distalBone);
        Vector3 intermediate = player.GetBonePosition(intermediateBone);
        if (distal != Vector3.zero && intermediate != Vector3.zero)
        {
            fingertipPositions[slot] = distal + (distal - intermediate) * FingertipExtrapolation;
            return true;
        }
        Vector3 hand = player.GetBonePosition(handBone);
        fingertipPositions[slot] = hand;
        return hand != Vector3.zero;
    }

    public override void Interact()
    {
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
        UpdateRestWorldPose();
        UpdateFingertipPresses();
        UpdateClickPress();
        float localClickPress = clickIsLocal ? clickPress : 0f;
        UpdateTrigger(Mathf.Clamp(Mathf.Max(localPress, localClickPress), 0f, travelDistance));
        UpdatePressAmount();
        ApplyPress(pressAmount);
    }

    /// <summary>
    /// Sets localPress from the local player's fingertips (and the debug fingertip) and remotePress from everyone
    /// else's: the deepest press of any engaged fingertip. Only players in VR press with their fingers, judged by
    /// each player's own IsUserInVR, so a remote VR player moves the button on every client, VR and desktop alike.
    /// Desktop and mobile players click instead, and their avatars' animated hands never press. The debug fingertip
    /// counts as the local player's whatever their platform.
    /// </summary>
    private void UpdateFingertipPresses()
    {
        float local = 0f;
        float remote = 0f;
        Quaternion toRest = Quaternion.Inverse(restWorldRot);
        for (int i = 0; i < trackedPlayers.Length; i++)
        {
            VRCPlayerApi player = trackedPlayers[i];
            bool pressing = Utilities.IsValid(player) && player.IsUserInVR();
            if (pressing) GetFingertips(player);
            for (int f = 0; f < FingertipsPerPlayer; f++)
            {
                int slot = i * FingertipsPerPlayer + f;
                if (!pressing || !fingertipValid[f])
                {
                    fingertipEngaged[slot] = false;
                    fingertipWasInFront[slot] = false;
                    continue;
                }
                tipEngaged = fingertipEngaged[slot];
                tipWasInFront = fingertipWasInFront[slot];
                float press = EvaluateFingertip(toRest, fingertipPositions[f], fingertipScaledRadius);
                fingertipEngaged[slot] = tipEngaged;
                fingertipWasInFront[slot] = tipWasInFront;
                if (player.isLocal) local = Mathf.Max(local, press);
                else remote = Mathf.Max(remote, press);
            }
        }

        if (debugFingertip != null)
        {
            tipEngaged = debugEngaged;
            tipWasInFront = debugWasInFront;
            local = Mathf.Max(local, EvaluateFingertip(toRest, debugFingertip.position, fingertipRadius));
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
    /// Editor only, in Play Mode: a wire sphere of the scaled radius at each estimated fingertip of every tracked player.
    /// </summary>
    private void DrawFingertipGizmos()
    {
        if (!Application.isPlaying) return;
        UdonBehaviour udon = GetComponent<UdonBehaviour>();
        VRCPlayerApi[] players = udon != null ? udon.GetProgramVariable("trackedPlayers") as VRCPlayerApi[] : null;
        if (players == null) return;
        Gizmos.color = new Color(0.3f, 1f, 0.3f, 0.9f);
        foreach (VRCPlayerApi player in players)
        {
            if (!Utilities.IsValid(player)) continue;
            GetFingertips(player);
            for (int f = 0; f < FingertipsPerPlayer; f++)
            {
                if (fingertipValid[f]) Gizmos.DrawWireSphere(fingertipPositions[f], fingertipScaledRadius);
            }
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
