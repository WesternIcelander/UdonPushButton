using System.Collections.Generic;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.ClientSim;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using VRC.SDK3.UdonNetworkCalling;

// Hooks into ClientSim: counting bone reads, recording haptics and network events, and simulating VR players.
public static partial class PushButtonPlayModeTests
{
    // Replaces ClientSim's GetBonePosition with one that counts calls per frame (or restarts the count), from the next
    // frame on.
    static void StartCountingBoneReads()
    {
        if (!boneReadsCounted)
        {
            VRCPlayerApi._GetBonePosition -= ClientSimPlayerManager.GetBonePosition;
            VRCPlayerApi._GetBonePosition += CountedGetBonePosition;
            boneReadsCounted = true;
        }
        boneReadsPerFrame.Clear();
        boneCountFrom = Time.frameCount + 1;
    }

    static Vector3 CountedGetBonePosition(VRCPlayerApi player, HumanBodyBones bone)
    {
        int frame = Time.frameCount;
        int count;
        boneReadsPerFrame.TryGetValue(frame, out count);
        boneReadsPerFrame[frame] = count + 1;
        return ClientSimPlayerManager.GetBonePosition(player, bone);
    }

    // The GetBonePosition calls per frame from the frame after counting started to the frame before this one, which
    // must be the same on every frame (otherwise -1). The test's own steps read no bones in between.
    static int BoneReadsPerFrame(string name)
    {
        int last = Time.frameCount - 1;
        int min = int.MaxValue;
        int max = -1;
        for (int frame = boneCountFrom; frame <= last; frame++)
        {
            int count;
            boneReadsPerFrame.TryGetValue(frame, out count);
            min = Mathf.Min(min, count);
            max = Mathf.Max(max, count);
        }
        int frames = last - boneCountFrom + 1;
        Debug.Log("[PushButtonTest] " + name + ": " + min + " to " + max + " bone reads per frame over " + frames + " frames");
        if (!Check(name + " counted frames", frames > 0, "no frames")) return -1;
        return Check(name + " same count every frame", min == max, min + " to " + max) ? min : -1;
    }

    // Puts ClientSim's own GetBonePosition back, before ClientSim unhooks it as Play Mode ends.
    static void StopCountingBoneReads()
    {
        if (!boneReadsCounted) return;
        VRCPlayerApi._GetBonePosition -= CountedGetBonePosition;
        VRCPlayerApi._GetBonePosition += ClientSimPlayerManager.GetBonePosition;
        boneReadsCounted = false;
    }

    static void RecordHaptic(VRCPlayerApi player, VRC_Pickup.PickupHand hand, float duration, float amplitude, float frequency)
    {
        if (player == null || !player.isLocal) return;
        hapticCount++;
        lastHapticHand = hand;
        lastHapticAmplitude = amplitude;
        Debug.Log("[PushButtonTest] haptic pulse: " + hand + ", " + duration + " s, amplitude " + amplitude + ", " + frequency + " Hz");
    }

    static void CheckHaptics(string name, int count, VRC_Pickup.PickupHand hand, float amplitude)
    {
        if (!Check(name + " pulses", hapticCount == count, hapticCount + " pulses, expected " + count)) return;
        Check(name + " hand", lastHapticHand == hand, "pulsed " + lastHapticHand);
        Check(name + " amplitude", Mathf.Abs(lastHapticAmplitude - amplitude) < 1e-6f, "amplitude " + lastHapticAmplitude);
    }

    // A player's left index fingertip state in the tracked slots.
    static void CheckPlayerTip(string name, VRCPlayerApi player, bool engaged, bool wasInFront)
    {
        int slot = player != null ? SlotOf(player, 0) : -1;
        if (!Check(name + " tracked", slot >= 0, "player not tracked")) return;
        bool isEngaged = ((bool[])button.GetProgramVariable("fingertipEngaged"))[slot];
        bool isInFront = ((bool[])button.GetProgramVariable("fingertipWasInFront"))[slot];
        Debug.Log("[PushButtonTest] " + name + ": in VR " + player.IsUserInVR() + ", engaged " + isEngaged + ", in front " + isInFront);
        Check(name + (engaged ? " engaged" : " not engaged"), isEngaged == engaged, "engaged " + isEngaged);
        Check(name + (wasInFront ? " in front" : " not in front"), isInFront == wasInFront, "in front " + isInFront);
    }

    // Makes IsUserInVR report the player as in VR (or back on desktop), installing the override on first use.
    // ClientSim answers IsUserInVR through VRCPlayerApi._IsUserInVR, and all its players are on desktop.
    static void SimulateVR(VRCPlayerApi player, bool inVR)
    {
        if (!isUserInVROverridden)
        {
            VRCPlayerApi._IsUserInVR -= ClientSimPlayerManager.IsUserInVR;
            VRCPlayerApi._IsUserInVR += SimulatedIsUserInVR;
            isUserInVROverridden = true;
        }
        if (inVR) simulatedVRPlayers.Add(player.playerId);
        else simulatedVRPlayers.Remove(player.playerId);
    }

    static bool SimulatedIsUserInVR(VRCPlayerApi player)
    {
        return simulatedVRPlayers.Contains(player.playerId) || ClientSimPlayerManager.IsUserInVR(player);
    }

    // Puts ClientSim's own IsUserInVR back, before ClientSim unhooks it as Play Mode ends.
    static void RestoreIsUserInVR()
    {
        simulatedVRPlayers.Clear();
        if (!isUserInVROverridden) return;
        VRCPlayerApi._IsUserInVR -= SimulatedIsUserInVR;
        VRCPlayerApi._IsUserInVR += ClientSimPlayerManager.IsUserInVR;
        isUserInVROverridden = false;
    }

    // Records every network event sent from now on (or clears the record), as "<target> <event>", alongside ClientSim's
    // own handler, which drops events sent to Others (ClientSim has no other clients). NetworkCalling's proxy isn't
    // public outside ClientSim, so it's reached by reflection.
    static void StartRecordingNetworkEvents()
    {
        if (!networkEventsRecorded)
        {
            System.Reflection.PropertyInfo proxy = NetworkEventProxy();
            if (!Check("network event hook: NetworkCalling.SendCustomNetworkEventProxy", proxy != null, "not found; static members: " + NetworkCallingMembers())) return;
            networkEventRecorder = System.Delegate.CreateDelegate(proxy.PropertyType, typeof(PushButtonPlayModeTests).GetMethod(nameof(RecordNetworkEvent),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
            proxy.SetValue(null, System.Delegate.Combine((System.Delegate)proxy.GetValue(null), networkEventRecorder));
            networkEventsRecorded = true;
        }
        sentNetworkEvents.Clear();
    }

    static System.Reflection.PropertyInfo NetworkEventProxy()
    {
        return typeof(NetworkCalling).GetProperty("SendCustomNetworkEventProxy",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
    }

    static string NetworkCallingMembers()
    {
        List<string> names = new List<string>();
        foreach (System.Reflection.MemberInfo member in typeof(NetworkCalling).GetMembers(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            names.Add(member.MemberType + " " + member.Name);
        }
        return string.Join(", ", names);
    }

    static void RecordNetworkEvent(IUdonEventReceiver receiver, NetworkEventTarget target, string eventName, System.Memory<object> parameters)
    {
        sentNetworkEvents.Add(target + " " + eventName);
    }

    static void StopRecordingNetworkEvents()
    {
        if (!networkEventsRecorded) return;
        System.Reflection.PropertyInfo proxy = NetworkEventProxy();
        proxy.SetValue(null, System.Delegate.Remove((System.Delegate)proxy.GetValue(null), networkEventRecorder));
        networkEventsRecorded = false;
    }

    // The network events recorded since StartRecordingNetworkEvents are exactly `expected`, in order.
    static void CheckNetworkEvents(string name, params string[] expected)
    {
        string sent = string.Join(", ", sentNetworkEvents);
        Check(name, sent == string.Join(", ", expected), "sent [" + sent + "], expected [" + string.Join(", ", expected) + "]");
    }
}
