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
