using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// The scene's shared state for every <see cref="PushButton"/>: one per scene, required by each button.
/// Keeps everyone in the instance in stable player slots, so buttons can keep per-slot state that never shifts.
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

    // Registered buttons (the first buttonCount entries). A button registers on its first frame and unregisters when
    // disabled; destroyed ones are dropped as they're found.
    private PushButton[] buttons = new PushButton[0];
    private int buttonCount;
    // The Time.frameCount PrepareFrame last ran on, so the cache is built once per frame whichever button asks first.
    private int preparedFrame = -1;
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
    /// Adds the button to the registered buttons. Does nothing if it is already registered.
    /// </summary>
    public void Register(PushButton button)
    {
        if (button == null) return;
        for (int i = 0; i < buttonCount; i++)
        {
            if (buttons[i] == button) return;
        }
        if (buttonCount == buttons.Length)
        {
            PushButton[] grown = new PushButton[Mathf.Max(InitialSlotCount, buttons.Length * 2)];
            for (int i = 0; i < buttonCount; i++) grown[i] = buttons[i];
            buttons = grown;
        }
        buttons[buttonCount] = button;
        buttonCount++;
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
        for (int j = index + 1; j < buttonCount; j++) buttons[j - 1] = buttons[j];
        buttonCount--;
        buttons[buttonCount] = null;
    }

    /// <summary>
    /// Builds this frame's per-slot cache. Only the first call in a frame does the work, so every button can call it.
    /// Hand bones are read for every VR player; finger bones only for players with a hand near an active button, and
    /// only for the fingers those buttons use. Bone reads therefore scale with players, not players times buttons.
    /// A button's hand check sphere is the one it published on its own last frame.
    /// </summary>
    public void PrepareFrame()
    {
        int frame = Time.frameCount;
        if (frame == preparedFrame) return;
        preparedFrame = frame;
        MatchCacheSize();

        int activeCount = CollectActiveButtons();
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
    /// Fills the active* scratch arrays with the registered buttons that want fingertips this frame (in range, with a
    /// finger in use) and returns how many there are. Destroyed buttons are dropped from the registration.
    /// </summary>
    private int CollectActiveButtons()
    {
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
            i++;
            if (!button.fingerCheckActive) continue;
            bool useIndex = button.useIndexFingers;
            bool useMiddle = button.useMiddleFingers;
            if (!useIndex && !useMiddle) continue;
            activeCenters[count] = button.fingerCheckCenter;
            activeRangeSqr[count] = button.fingerCheckRangeSqr;
            activeUseIndex[count] = useIndex;
            activeUseMiddle[count] = useMiddle;
            count++;
        }
        return count;
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
