using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// The scene's shared state for every <see cref="PushButton"/>: one per scene, required by each button.
/// Keeps everyone in the instance in stable player slots, so buttons can keep per-slot state that never shifts, and
/// drives every registered button from its own Update: buttons have none.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class PushButtonManager : UdonSharpBehaviour
{
    // Settings shared by every button. The manager writes them into each button whenever it registers it, and
    // ApplySettings writes them into every registered button after a change at runtime.
    [Header("VR")]
    [Tooltip("Fingertip contact radius in meters, scaled by each avatar's size.")]
    public float fingertipRadius = 0.008f;
    [Tooltip("Index fingertips can press buttons.")]
    public bool useIndexFingers = true;
    [Tooltip("Middle fingertips can press buttons.")]
    public bool useMiddleFingers = false;
    [Tooltip("Haptic pulse strength on the local pressing hand (0 = off).")]
    [Range(0f, 1f)]
    public float hapticStrength = 0.3f;

    [Header("Desktop")]
    [Tooltip("Seconds for the click press-in animation.")]
    public float clickPressDuration = 0.12f;

    [Header("Debug")]
    [Tooltip("Editor only: acts as the local player's fingertip for testing without a headset. Ignored in builds.")]
    public Transform debugFingertip;

    // Player slots: a joining player takes the first free slot and a leaving player frees theirs, so slots never shift.
    // slotGenerations[i] goes up each time slot i gets a new player, so a button can tell a new player from the old one
    // and never carries a press over. Both arrays grow together when every slot is taken; null marks a free slot.
    [System.NonSerialized] public VRCPlayerApi[] slotPlayers = new VRCPlayerApi[0];
    [System.NonSerialized] public int[] slotGenerations = new int[0];

    private const int InitialSlotCount = 8;

    // C#'s equivalent of Java's `static final` for a primitive is `const`.
    // UdonSharp supports `const`; non-const static fields are not supported reliably.
    // A button is only ticked while the local player is within this distance of it.
    public const float AnimationRange = 5f;   // meters
    public const float AnimationRangeSqr = AnimationRange * AnimationRange;

    // Fingertip slots per player: left index, right index, left middle, right middle (even slots are left-hand ones).
    public const int FingertipsPerPlayer = 4;
    // The fingertip lies this fraction of the last finger segment (intermediate -> distal) beyond the distal bone.
    private const float FingertipExtrapolation = 0.8f;
    // Avatar eye height in meters at which fingertipRadius applies unscaled; other avatars scale it in proportion.
    private const float ReferenceEyeHeight = 1.6f;
    // A player's finger bones are only read while one of their hand bones is within this distance of an active
    // button's contact box.
    public const float HandEarlyOutDistance = 0.5f;   // meters

    // The per-frame cache, one entry per player slot, written once per frame by PrepareFrame and read by every button.
    // slotNear marks a valid VR player with a hand near an active button; only then are the fingertips filled in.
    // slotScale is the avatar's eye height / ReferenceEyeHeight (1 when unknown), which buttons multiply their
    // fingertipRadius by. fingertipPositions and fingertipValid hold fingertip f of slot i at i * FingertipsPerPlayer + f;
    // a fingertip is valid only if it was estimated this frame (a finger the settings use, on an avatar with the bones).
    [System.NonSerialized] public bool[] slotValid = new bool[0];
    [System.NonSerialized] public bool[] slotIsLocal = new bool[0];
    [System.NonSerialized] public bool[] slotInVR = new bool[0];
    [System.NonSerialized] public Vector3[] slotLeftHand = new Vector3[0];
    [System.NonSerialized] public Vector3[] slotRightHand = new Vector3[0];
    [System.NonSerialized] public bool[] slotNear = new bool[0];
    [System.NonSerialized] public float[] slotScale = new float[0];
    [System.NonSerialized] public Vector3[] fingertipPositions = new Vector3[0];
    [System.NonSerialized] public bool[] fingertipValid = new bool[0];

    // Registered buttons (the first buttonCount entries), with their transforms and whether the local player was in
    // range of each on the last frame. A button registers whenever it's enabled and unregisters whenever it's
    // disabled; destroyed ones are dropped as they're found.
    private PushButton[] buttons = new PushButton[0];
    private Transform[] buttonTransforms = new Transform[0];
    private bool[] buttonInRange = new bool[0];
    private int buttonCount;
    // Registrations and unregistrations requested while Update runs (an action enabling or disabling buttons), applied
    // in order once it's done, so its loop never sees the list change: one entry per button, holding its latest request.
    private PushButton[] pendingButtons = new PushButton[0];
    private bool[] pendingRegister = new bool[0];
    private int pendingCount;
    private bool ticking;
    // Whether the manager is enabled and ticking its buttons. Buttons ignore clicks while it isn't.
    [System.NonSerialized] public bool running;
    // How many buttons the last Update ticked (the ones in range).
    [System.NonSerialized] public int tickedButtons;
    // Scratch for PrepareFrame: the hand check spheres of the in-range buttons this frame.
    private Vector3[] activeCenters = new Vector3[0];
    private float[] activeRangeSqr = new float[0];

    private void OnEnable()
    {
        RebuildSlots();
        running = true;
    }

    /// <summary>
    /// Stops ticking and puts every registered button at rest. The buttons stay registered, and once the manager is
    /// enabled again the range gate brings the ones in range back.
    /// </summary>
    private void OnDisable()
    {
        running = false;
        for (int i = 0; i < buttonCount; i++)
        {
            buttonInRange[i] = false;
            PushButton button = buttons[i];
            if (Utilities.IsValid(button)) button._LeaveRange();
        }
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        AddPlayer(player);
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        int slot = FindSlot(player);
        if (slot >= 0) slotPlayers[slot] = null;
    }

    /// <summary>
    /// Brings the slots in line with the instance's player list, keeping everyone who is still there in their slot.
    /// Recovers from joins and leaves missed while the manager was disabled.
    /// </summary>
    private void RebuildSlots()
    {
        VRCPlayerApi[] all = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(all);
        for (int s = 0; s < slotPlayers.Length; s++)
        {
            VRCPlayerApi player = slotPlayers[s];
            if (player == null) continue;
            bool present = false;
            if (Utilities.IsValid(player))
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == player)
                    {
                        present = true;
                        break;
                    }
                }
            }
            if (!present) slotPlayers[s] = null;
        }
        for (int i = 0; i < all.Length; i++) AddPlayer(all[i]);
    }

    /// <summary>
    /// Puts the player in the first free slot (growing the arrays if there is none) with a new generation.
    /// Does nothing for an invalid player or one who already has a slot.
    /// </summary>
    private void AddPlayer(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || FindSlot(player) >= 0) return;
        int slot = -1;
        for (int s = 0; s < slotPlayers.Length; s++)
        {
            if (slotPlayers[s] == null || !Utilities.IsValid(slotPlayers[s]))
            {
                slot = s;
                break;
            }
        }
        if (slot < 0)
        {
            slot = slotPlayers.Length;
            int size = Mathf.Max(InitialSlotCount, slotPlayers.Length * 2);
            VRCPlayerApi[] players = new VRCPlayerApi[size];
            int[] generations = new int[size];
            for (int s = 0; s < slotPlayers.Length; s++)
            {
                players[s] = slotPlayers[s];
                generations[s] = slotGenerations[s];
            }
            slotPlayers = players;
            slotGenerations = generations;
        }
        slotPlayers[slot] = player;
        slotGenerations[slot]++;
    }

    /// <summary>
    /// Writes the settings into the button, adds it to the registered buttons and sets its registeredWithManager.
    /// Does nothing more if it is already registered. While Update runs, the change to the list is deferred until
    /// it's done. Buttons register on every enable, so a button disabled during a settings change catches up here.
    /// </summary>
    public void Register(PushButton button)
    {
        if (!Utilities.IsValid(button)) return;
        PushSettings(button);
        button.registeredWithManager = true;
        if (ticking) Defer(button, true);
        else AddButton(button);
    }

    /// <summary>
    /// Writes the current settings into every registered button (and those waiting to register), for changing them at
    /// runtime. Disabled buttons get them when they're next enabled.
    /// </summary>
    public void ApplySettings()
    {
        for (int i = 0; i < buttonCount; i++)
        {
            if (Utilities.IsValid(buttons[i])) PushSettings(buttons[i]);
        }
        for (int i = 0; i < pendingCount; i++)
        {
            if (pendingRegister[i] && Utilities.IsValid(pendingButtons[i])) PushSettings(pendingButtons[i]);
        }
    }

    private void PushSettings(PushButton button)
    {
        button.fingertipRadius = fingertipRadius;
        button.useIndexFingers = useIndexFingers;
        button.useMiddleFingers = useMiddleFingers;
        button.hapticStrength = hapticStrength;
        button.clickPressDuration = clickPressDuration;
    }

    /// <summary>
    /// Removes the button from the registered buttons, keeping the others in order, and clears its
    /// registeredWithManager. Does nothing more if it isn't registered. While Update runs, the change is deferred
    /// until it's done.
    /// </summary>
    public void Unregister(PushButton button)
    {
        if (button == null) return;
        if (Utilities.IsValid(button)) button.registeredWithManager = false;
        if (ticking) Defer(button, false);
        else RemoveButton(button);
    }

    /// <summary>
    /// Records a registration (or unregistration) to apply after the tick, replacing any earlier one for the button.
    /// </summary>
    private void Defer(PushButton button, bool register)
    {
        for (int i = 0; i < pendingCount; i++)
        {
            if (pendingButtons[i] != button) continue;
            pendingRegister[i] = register;
            return;
        }
        if (pendingCount == pendingButtons.Length)
        {
            int size = Mathf.Max(InitialSlotCount, pendingButtons.Length * 2);
            PushButton[] grown = new PushButton[size];
            bool[] grownRegister = new bool[size];
            for (int i = 0; i < pendingCount; i++)
            {
                grown[i] = pendingButtons[i];
                grownRegister[i] = pendingRegister[i];
            }
            pendingButtons = grown;
            pendingRegister = grownRegister;
        }
        pendingButtons[pendingCount] = button;
        pendingRegister[pendingCount] = register;
        pendingCount++;
    }

    /// <summary>
    /// Applies the requests deferred during the tick, in order.
    /// </summary>
    private void ApplyPending()
    {
        for (int i = 0; i < pendingCount; i++)
        {
            PushButton button = pendingButtons[i];
            if (pendingRegister[i])
            {
                if (Utilities.IsValid(button)) AddButton(button);
            }
            else RemoveButton(button);
            pendingButtons[i] = null;
        }
        pendingCount = 0;
    }

    private void AddButton(PushButton button)
    {
        for (int i = 0; i < buttonCount; i++)
        {
            if (buttons[i] == button) return;
        }
        if (buttonCount == buttons.Length)
        {
            int size = Mathf.Max(InitialSlotCount, buttons.Length * 2);
            PushButton[] grown = new PushButton[size];
            Transform[] grownTransforms = new Transform[size];
            bool[] grownInRange = new bool[size];
            for (int i = 0; i < buttonCount; i++)
            {
                grown[i] = buttons[i];
                grownTransforms[i] = buttonTransforms[i];
                grownInRange[i] = buttonInRange[i];
            }
            buttons = grown;
            buttonTransforms = grownTransforms;
            buttonInRange = grownInRange;
        }
        buttons[buttonCount] = button;
        buttonTransforms[buttonCount] = button.transform;
        // Counted as in range, so a button that starts out of range gets the out-of-range reset once.
        buttonInRange[buttonCount] = true;
        buttonCount++;
    }

    private void RemoveButton(PushButton button)
    {
        for (int i = 0; i < buttonCount; i++)
        {
            if (buttons[i] != button) continue;
            RemoveButtonAt(i);
            return;
        }
    }

    private void RemoveButtonAt(int index)
    {
        for (int j = index + 1; j < buttonCount; j++)
        {
            buttons[j - 1] = buttons[j];
            buttonTransforms[j - 1] = buttonTransforms[j];
            buttonInRange[j - 1] = buttonInRange[j];
        }
        buttonCount--;
        buttons[buttonCount] = null;
        buttonTransforms[buttonCount] = null;
    }

    /// <summary>
    /// Drives every registered button: the range gate for each, this frame's per-slot cache, then a tick for each
    /// button in range. Buttons out of range cost one distance check each. Registration changes requested meanwhile
    /// (by buttons' actions) are applied at the end.
    /// </summary>
    private void Update()
    {
        ticking = true;
        int activeCount = UpdateRangeGates();
        PrepareFrame(activeCount);
        int ticked = 0;
        for (int i = 0; i < buttonCount; i++)
        {
            // An earlier button's action can disable the manager, which has put every button at rest.
            if (!running) break;
            if (!buttonInRange[i]) continue;
            PushButton button = buttons[i];
            // An earlier button's action can destroy or disable this one (its unregistration waits until the end).
            if (!Utilities.IsValid(button) || !button.registeredWithManager) continue;
            button._Tick();
            ticked++;
        }
        tickedButtons = ticked;
        ticking = false;
        ApplyPending();
    }

    /// <summary>
    /// The range gate for every registered button, against the local player (a missing local player counts as in
    /// range). A button leaving range gets its out-of-range reset once. Fills the active* scratch arrays with the
    /// in-range buttons (none when the settings use no finger) and returns how many there are. Destroyed buttons are
    /// dropped.
    /// The distance is taken from the button's current position: a button out of range is at rest, and one in range
    /// is at most its travel distance from rest. Its hand check sphere is centred on its contact box at that position
    /// too, which the sphere's margin (HandEarlyOutDistance) covers.
    /// </summary>
    private int UpdateRangeGates()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        bool hasLocal = Utilities.IsValid(localPlayer);
        Vector3 localPosition = hasLocal ? localPlayer.GetPosition() : Vector3.zero;
        if (activeCenters.Length < buttonCount)
        {
            activeCenters = new Vector3[buttons.Length];
            activeRangeSqr = new float[buttons.Length];
        }
        bool anyFinger = useIndexFingers || useMiddleFingers;
        int count = 0;
        int i = 0;
        while (i < buttonCount)
        {
            PushButton button = buttons[i];
            if (!Utilities.IsValid(button))
            {
                RemoveButtonAt(i);
                continue;
            }
            Transform buttonTransform = buttonTransforms[i];
            bool inRange = !hasLocal || (buttonTransform.position - localPosition).sqrMagnitude <= AnimationRangeSqr;
            if (!inRange)
            {
                if (buttonInRange[i])
                {
                    buttonInRange[i] = false;
                    button._LeaveRange();
                }
                i++;
                continue;
            }
            buttonInRange[i] = true;
            i++;
            if (!anyFinger) continue;
            activeCenters[count] = buttonTransform.TransformPoint(button.fingerCheckLocalCenter);
            activeRangeSqr[count] = button.fingerCheckRangeSqr;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Builds this frame's per-slot cache, before any button ticks. Hand bones are read for every VR player; finger
    /// bones only for players with a hand near one of the activeCount in-range buttons UpdateRangeGates collected,
    /// and only for the fingers the settings use. Bone reads therefore scale with players, not players times buttons.
    /// </summary>
    private void PrepareFrame(int activeCount)
    {
        MatchCacheSize();
        for (int s = 0; s < slotPlayers.Length; s++)
        {
            VRCPlayerApi player = slotPlayers[s];
            bool valid = player != null && Utilities.IsValid(player);
            bool inVR = valid && player.IsUserInVR();
            slotValid[s] = valid;
            slotIsLocal[s] = valid && player.isLocal;
            slotInVR[s] = inVR;
            slotNear[s] = false;
            int first = s * FingertipsPerPlayer;
            for (int f = 0; f < FingertipsPerPlayer; f++) fingertipValid[first + f] = false;
            if (!inVR)
            {
                // Desktop and mobile players click instead, so their avatars' animated hands never press.
                slotLeftHand[s] = Vector3.zero;
                slotRightHand[s] = Vector3.zero;
                slotScale[s] = 1f;
                continue;
            }

            Vector3 left = player.GetBonePosition(HumanBodyBones.LeftHand);
            Vector3 right = player.GetBonePosition(HumanBodyBones.RightHand);
            slotLeftHand[s] = left;
            slotRightHand[s] = right;
            float eyeHeight = player.GetAvatarEyeHeightAsMeters();
            slotScale[s] = eyeHeight > 0f ? eyeHeight / ReferenceEyeHeight : 1f;

            bool near = false;
            for (int b = 0; b < activeCount; b++)
            {
                if (!HandNear(left, activeCenters[b], activeRangeSqr[b]) && !HandNear(right, activeCenters[b], activeRangeSqr[b])) continue;
                near = true;
                break;
            }
            if (!near) continue;

            slotNear[s] = true;
            if (useIndexFingers)
            {
                fingertipValid[first] = EstimateFingertip(player, first, HumanBodyBones.LeftIndexDistal, HumanBodyBones.LeftIndexIntermediate, left);
                fingertipValid[first + 1] = EstimateFingertip(player, first + 1, HumanBodyBones.RightIndexDistal, HumanBodyBones.RightIndexIntermediate, right);
            }
            if (useMiddleFingers)
            {
                fingertipValid[first + 2] = EstimateFingertip(player, first + 2, HumanBodyBones.LeftMiddleDistal, HumanBodyBones.LeftMiddleIntermediate, left);
                fingertipValid[first + 3] = EstimateFingertip(player, first + 3, HumanBodyBones.RightMiddleDistal, HumanBodyBones.RightMiddleIntermediate, right);
            }
        }
    }

    /// <summary>
    /// Whether a hand bone is within the given squared distance of a point. A missing hand bone (zero) counts as near,
    /// so the fingertip estimate decides.
    /// </summary>
    private bool HandNear(Vector3 hand, Vector3 center, float rangeSqr)
    {
        return hand == Vector3.zero || (hand - center).sqrMagnitude <= rangeSqr;
    }

    /// <summary>
    /// Puts one fingertip estimate in fingertipPositions[index]: the distal bone extended along the last finger
    /// segment, or the hand bone (already read) if the avatar lacks those finger bones. Returns false if it lacks the
    /// hand bone too. GetBonePosition returns zero for a missing bone.
    /// </summary>
    private bool EstimateFingertip(VRCPlayerApi player, int index, HumanBodyBones distalBone, HumanBodyBones intermediateBone, Vector3 hand)
    {
        Vector3 distal = player.GetBonePosition(distalBone);
        Vector3 intermediate = player.GetBonePosition(intermediateBone);
        if (distal != Vector3.zero && intermediate != Vector3.zero)
        {
            fingertipPositions[index] = distal + (distal - intermediate) * FingertipExtrapolation;
            return true;
        }
        fingertipPositions[index] = hand;
        return hand != Vector3.zero;
    }

    /// <summary>
    /// Sizes the per-frame cache to the slot arrays. Its contents are rebuilt every frame, so nothing is copied.
    /// </summary>
    private void MatchCacheSize()
    {
        int slots = slotPlayers.Length;
        if (slotValid.Length == slots) return;
        slotValid = new bool[slots];
        slotIsLocal = new bool[slots];
        slotInVR = new bool[slots];
        slotLeftHand = new Vector3[slots];
        slotRightHand = new Vector3[slots];
        slotNear = new bool[slots];
        slotScale = new float[slots];
        fingertipPositions = new Vector3[slots * FingertipsPerPlayer];
        fingertipValid = new bool[slots * FingertipsPerPlayer];
    }

    /// <summary>
    /// The player's slot, or -1 if they have none.
    /// </summary>
    public int FindSlot(VRCPlayerApi player)
    {
        if (player == null) return -1;
        for (int s = 0; s < slotPlayers.Length; s++)
        {
            if (slotPlayers[s] == player) return s;
        }
        return -1;
    }
}
