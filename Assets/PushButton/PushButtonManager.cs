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
    // Avatar eye height in meters at which a button's fingertipRadius applies unscaled; other avatars scale it in proportion.
    private const float ReferenceEyeHeight = 1.6f;
    // A player's finger bones are only read while one of their hand bones is within this distance of an active
    // button's contact box.
    public const float HandEarlyOutDistance = 0.5f;   // meters

    // The per-frame cache, one entry per player slot, written once per frame by PrepareFrame and read by every button.
    // slotNear marks a valid VR player with a hand near an active button; only then are the fingertips filled in.
    // slotScale is the avatar's eye height / ReferenceEyeHeight (1 when unknown), which buttons multiply their
    // fingertipRadius by. fingertipPositions and fingertipValid hold fingertip f of slot i at i * FingertipsPerPlayer + f;
    // a fingertip is valid only if it was estimated this frame (a finger some near button uses, on an avatar with the
    // bones).
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
    // range of each on the last frame. A button registers when it starts (and again when re-enabled) and unregisters
    // when disabled; destroyed ones are dropped as they're found.
    private PushButton[] buttons = new PushButton[0];
    private Transform[] buttonTransforms = new Transform[0];
    private bool[] buttonInRange = new bool[0];
    private int buttonCount;
    // How many buttons the last Update ticked (the ones in range).
    [System.NonSerialized] public int tickedButtons;
    // Scratch for PrepareFrame: the buttons that want fingertips this frame, with their hand check spheres and fingers.
    private Vector3[] activeCenters = new Vector3[0];
    private float[] activeRangeSqr = new float[0];
    private bool[] activeUseIndex = new bool[0];
    private bool[] activeUseMiddle = new bool[0];

    private void OnEnable()
    {
        RebuildSlots();
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
    /// Adds the button to the registered buttons and returns true. Does nothing if it is already registered.
    /// A caller that gets false reached the manager before Udon started it, so the call was dropped.
    /// </summary>
    public bool Register(PushButton button)
    {
        if (button == null) return false;
        for (int i = 0; i < buttonCount; i++)
        {
            if (buttons[i] == button) return true;
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
        return true;
    }

    /// <summary>
    /// Removes the button from the registered buttons, keeping the others in order. Does nothing if it isn't registered.
    /// </summary>
    public void Unregister(PushButton button)
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
    /// button in range. Buttons out of range cost one distance check each.
    /// </summary>
    private void Update()
    {
        int activeCount = UpdateRangeGates();
        PrepareFrame(activeCount);
        int ticked = 0;
        for (int i = 0; i < buttonCount; i++)
        {
            if (!buttonInRange[i]) continue;
            PushButton button = buttons[i];
            // An earlier button's action can destroy this one.
            if (!Utilities.IsValid(button)) continue;
            button._Tick();
            ticked++;
        }
        tickedButtons = ticked;
    }

    /// <summary>
    /// The range gate for every registered button, against the local player (a missing local player counts as in
    /// range). A button leaving range gets its out-of-range reset once. Fills the active* scratch arrays with the
    /// in-range buttons that use a finger and returns how many there are. Destroyed buttons are dropped.
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
            activeUseIndex = new bool[buttons.Length];
            activeUseMiddle = new bool[buttons.Length];
        }
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
            bool useIndex = button.useIndexFingers;
            bool useMiddle = button.useMiddleFingers;
            if (!useIndex && !useMiddle) continue;
            activeCenters[count] = buttonTransform.TransformPoint(button.fingerCheckLocalCenter);
            activeRangeSqr[count] = button.fingerCheckRangeSqr;
            activeUseIndex[count] = useIndex;
            activeUseMiddle[count] = useMiddle;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Builds this frame's per-slot cache, before any button ticks. Hand bones are read for every VR player; finger
    /// bones only for players with a hand near one of the activeCount in-range buttons UpdateRangeGates collected,
    /// and only for the fingers those buttons use. Bone reads therefore scale with players, not players times buttons.
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

            bool needIndex = false;
            bool needMiddle = false;
            for (int b = 0; b < activeCount; b++)
            {
                if (!HandNear(left, activeCenters[b], activeRangeSqr[b]) && !HandNear(right, activeCenters[b], activeRangeSqr[b])) continue;
                needIndex = needIndex || activeUseIndex[b];
                needMiddle = needMiddle || activeUseMiddle[b];
                if (needIndex && needMiddle) break;
            }
            if (!needIndex && !needMiddle) continue;

            slotNear[s] = true;
            if (needIndex)
            {
                fingertipValid[first] = EstimateFingertip(player, first, HumanBodyBones.LeftIndexDistal, HumanBodyBones.LeftIndexIntermediate, left);
                fingertipValid[first + 1] = EstimateFingertip(player, first + 1, HumanBodyBones.RightIndexDistal, HumanBodyBones.RightIndexIntermediate, right);
            }
            if (needMiddle)
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
