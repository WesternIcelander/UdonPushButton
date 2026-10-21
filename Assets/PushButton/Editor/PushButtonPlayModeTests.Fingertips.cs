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

// Plan phase 5: fingertip estimates, engagement, player tracking, remote presses, the VR filter and haptics.
public static partial class PushButtonPlayModeTests
{
    // Plan step 5.2: the local player's fingertip estimates (in the manager's per-frame cache since plan step 8.2) match
    // the ClientSim avatar's finger bones, honour the finger toggles (the manager only estimates fingers a near button
    // uses), carry the eye-height-scaled radius, and follow the avatar when the player teleports 1 m sideways.
    static void AddFingertipTest()
    {
        const string name = "fingertips";
        Vector3 teleportOffset = new Vector3(1f, 0f, 0f);
        // Only VR players' fingertips are estimated (plan step 5.4), and ClientSim's local player is on desktop. Only
        // hands near the button are estimated (plan step 6.2), so the button sits 5 cm in front of the left index.
        AddStep(0.5f, () =>
        {
            if (Networking.LocalPlayer == null) return;
            SimulateVR(Networking.LocalPlayer, true);
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (button == null) return;
            savedParentPos = button.transform.parent.position;
            MoveButtonOntoLeftIndex(Networking.LocalPlayer, -0.05f);
        });
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            if (!Check(name + ": local player", Networking.LocalPlayer != null, "Networking.LocalPlayer is null")) { button = null; return; }
            CheckFingertips(name + " defaults", true, false);

            float eyeHeight = Networking.LocalPlayer.GetAvatarEyeHeightAsMeters();
            int localSlot = SlotOf(Networking.LocalPlayer, 0);
            localSlot = localSlot >= 0 ? localSlot / 4 : -1;
            float[] scales = (float[])ManagerOf(button).GetProgramVariable("slotScale");
            float radius = localSlot >= 0 && localSlot < scales.Length ? (float)button.GetProgramVariable("fingertipRadius") * scales[localSlot] : -1f;
            float expectedRadius = (float)button.GetProgramVariable("fingertipRadius") * eyeHeight / 1.6f;
            Debug.Log("[PushButtonTest] " + name + ": eye height " + eyeHeight.ToString("F3") + " m, scaled radius " + radius.ToString("F5"));
            Check(name + " radius scaled by eye height", Mathf.Abs(radius - expectedRadius) < 1e-6f,
                "got " + radius.ToString("F6") + ", expected " + expectedRadius.ToString("F6"));

            SetSetting("useIndexFingers", false);
            SetSetting("useMiddleFingers", true);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckFingertips(name + " middle only", false, true);
            SetSetting("useIndexFingers", true);
            SetSetting("useMiddleFingers", false);

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            savedPlayerPos = localPlayer.GetPosition();
            savedPlayerRot = localPlayer.GetRotation();
            tipsBeforeTeleport = LocalCachedTips();
            localPlayer.TeleportTo(savedPlayerPos + teleportOffset, savedPlayerRot);
            button.transform.parent.position += teleportOffset;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckFingertips(name + " after teleport", true, false);
            Vector3[] tips = LocalCachedTips();
            for (int f = 0; f < 2; f++)
            {
                Vector3 moved = tips[f] - tipsBeforeTeleport[f];
                Debug.Log("[PushButtonTest] " + name + " tip " + f + " moved " + moved.ToString("F4"));
                Check(name + " tip " + f + " follows the teleport", (moved - teleportOffset).magnitude < 0.02f, "moved " + moved.ToString("F4"));
            }
            Networking.LocalPlayer.TeleportTo(savedPlayerPos, savedPlayerRot);
            button.transform.parent.position = savedParentPos;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + " player back at start", (Networking.LocalPlayer.GetPosition() - savedPlayerPos).magnitude < 0.01f,
                "at " + Networking.LocalPlayer.GetPosition().ToString("F3"));
        });
    }

    // Compares the Udon fingertip slots with an estimate from the local player's bones taken now. Udon sampled the bones
    // earlier in the frame, so a small idle-animation drift is allowed.
    static void CheckFingertips(string name, bool index, bool middle)
    {
        HumanBodyBones[] distal = { HumanBodyBones.LeftIndexDistal, HumanBodyBones.RightIndexDistal, HumanBodyBones.LeftMiddleDistal, HumanBodyBones.RightMiddleDistal };
        HumanBodyBones[] intermediate = { HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.RightMiddleIntermediate };
        HumanBodyBones[] hand = { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftHand, HumanBodyBones.RightHand };
        VRCPlayerApi player = Networking.LocalPlayer;
        Vector3[] tips = LocalCachedTips();
        bool[] valid = new bool[4];
        int first = SlotOf(player, 0);
        if (first >= 0) System.Array.Copy((bool[])ManagerOf(button).GetProgramVariable("fingertipValid"), first, valid, 0, 4);
        for (int f = 0; f < 4; f++)
        {
            bool enabled = f < 2 ? index : middle;
            Check(name + " slot " + f + (enabled ? " valid" : " off"), valid[f] == enabled, "valid " + valid[f]);
            if (!enabled) continue;
            Vector3 d = player.GetBonePosition(distal[f]);
            Vector3 i = player.GetBonePosition(intermediate[f]);
            bool hasFinger = d != Vector3.zero && i != Vector3.zero;
            Vector3 expected = hasFinger ? d + (d - i) * 0.8f : player.GetBonePosition(hand[f]);
            float error = (tips[f] - expected).magnitude;
            Debug.Log("[PushButtonTest] " + name + " slot " + f + ": " + (hasFinger ? "finger bones" : "hand fallback")
                + ", tip " + tips[f].ToString("F4") + ", " + (error * 1000f).ToString("F2") + " mm from expected");
            Check(name + " slot " + f + " position", error < 0.005f, "off by " + error.ToString("F4") + " m");
            if (hasFinger) Check(name + " slot " + f + " beyond the distal bone", Vector3.Dot(tips[f] - d, d - i) > 0f, "not past the distal bone");
        }
    }

    // The local player's four fingertips in the manager's per-frame cache (all zero if the player has no slot).
    static Vector3[] LocalCachedTips()
    {
        Vector3[] tips = new Vector3[4];
        int first = SlotOf(Networking.LocalPlayer, 0);
        if (first >= 0) System.Array.Copy((Vector3[])ManagerOf(button).GetProgramVariable("fingertipPositions"), first, tips, 0, 4);
        return tips;
    }

    // Plan step 5.3: the debug fingertip pushed in from the front presses the button by its depth past the face (plus
    // its radius), stops it at travelDistance and fires once; pulling out and pushing in again fires again. Entering
    // from the side or the back never engages, leaving sideways disengages, and the footprint is grown by the radius.
    static void AddEngagementTest(string setup = "")
    {
        string name = "engagement" + setup;
        const float settle = 0.3f;
        float halfU = 0f;
        float radius = 0f;
        float travel = 0f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            if (!SetUpActionTarget(name)) { button = null; return; }
            halfU = (float)button.GetProgramVariable("footprintHalfU");
            radius = (float)button.GetProgramVariable("fingertipRadius");
            travel = (float)button.GetProgramVariable("travelDistance");
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " in front", false, true, 0f);
            PlaceDebugTip(travel * 0.5f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " half-way in", true, false, travel * 0.5f);
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " half-way button press", Mathf.Abs(press - travel * 0.5f) < Tolerance, "press " + press.ToString("F5"));
            CheckButtonAtPress(name + " half-way position", press);
            CheckActionCount(name + " half-way does not fire", 0);
            PlaceDebugTip(0.03f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " pushed past the stop", true, false, travel);
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " stops at travelDistance", Mathf.Abs(press - travel) < 1e-6f, "press " + press.ToString("F5"));
            CheckButtonAtPress(name + " stop position", press);
            CheckActionCount(name + " fires at the stop", 1);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " holding does not fire again", 1);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " pulled out", false, true, 0f);
            Check(name + " re-armed after pulling out", (bool)button.GetProgramVariable("armed"), "not armed");
            PlaceDebugTip(0.03f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " pushed again", true, false, travel);
            CheckActionCount(name + " fires again", 2);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });

        // Side entry: past the face but outside the footprint, then slid in.
        AddStep(settle, () =>
        {
            if (button == null) return;
            PlaceDebugTip(0.01f, halfU + radius + 0.01f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " beside the button", false, false, 0f);
            PlaceDebugTip(0.01f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " slid in from the side", false, false, 0f);
            // Back entry: from behind the button, moving towards the face.
            PlaceDebugTip(0.1f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " behind the button", false, false, 0f);
            PlaceDebugTip(0.01f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " came in from the back", false, false, 0f);
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " side and back entries leave it at rest", press == 0f, "press " + press.ToString("F5"));
            CheckActionCount(name + " side and back entries do not fire", 2);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });

        // Leaving sideways disengages, and sliding back in doesn't re-engage.
        AddStep(settle, () =>
        {
            if (button == null) return;
            PlaceDebugTip(travel * 0.5f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " engaged before leaving sideways", true, false, travel * 0.5f);
            PlaceDebugTip(travel * 0.5f, halfU + radius + 0.01f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " left sideways", false, false, 0f);
            PlaceDebugTip(travel * 0.5f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " slid back in", false, false, 0f);
            // The footprint is grown by the radius: a tip just past the edge but within its radius still presses.
            PlaceDebugTip(-0.01f, halfU + radius * 0.5f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " in front, over the edge", false, true, 0f);
            PlaceDebugTip(travel * 0.5f, halfU + radius * 0.5f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " presses within its radius of the edge", true, false, travel * 0.5f);
            CheckActionCount(name + " still two presses", 2);
            Check(name + " no remote press", (float)button.GetProgramVariable("remotePress") == 0f, "remotePress set");
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " back at rest", press == 0f, "press " + press.ToString("F5"));
            Check(name + " armed at the end", (bool)button.GetProgramVariable("armed"), "not armed");
            TearDownActionTarget(name);
        });
    }

    // The debug fingertip's engagement state and the local press it gives.
    static void CheckTip(string name, bool engaged, bool wasInFront, float localPress)
    {
        bool isEngaged = (bool)button.GetProgramVariable("debugEngaged");
        bool isInFront = (bool)button.GetProgramVariable("debugWasInFront");
        float press = (float)button.GetProgramVariable("localPress");
        Debug.Log("[PushButtonTest] " + name + ": engaged " + isEngaged + ", in front " + isInFront + ", local press " + press.ToString("F4"));
        Check(name + (engaged ? " engaged" : " not engaged"), isEngaged == engaged, "engaged " + isEngaged);
        Check(name + (wasInFront ? " in front" : " not in front"), isInFront == wasInFront, "in front " + isInFront);
        Check(name + " local press", Mathf.Abs(press - localPress) < Tolerance, "got " + press.ToString("F5") + ", expected " + localPress.ToString("F5"));
    }

    // Plan steps 5.1 and 5.3 (player tracking moved to the manager in 8.1): everyone in the instance is tracked, and keeps the list current on each join and leave.
    // Moves the button onto the local ClientSim avatar's left index fingertip so it engages for real (from in front,
    // then half the travel in), spawns two ClientSim remote players, removes them one at a time, and checks the press
    // survives every refresh.
    static void AddPlayerTrackingTest()
    {
        const string name = "player tracking";
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            CheckTrackedPlayers(name + " at start");
            savedParentPos = button.transform.parent.position;
            if (!Check(name + ": local avatar has index bones", MoveButtonOntoLeftIndex(Networking.LocalPlayer, -0.02f), "no finger bones")) button = null;
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            int slot = SlotOf(Networking.LocalPlayer, 0);
            Check(name + " local tip in front", slot >= 0 && ((bool[])button.GetProgramVariable("fingertipWasInFront"))[slot], "not in front");
            float travel = (float)button.GetProgramVariable("travelDistance");
            MoveButtonOntoLeftIndex(Networking.LocalPlayer, travel * 0.5f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckLocalFingertipKept(name + " before joins");
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("localPress");
            Debug.Log("[PushButtonTest] " + name + ": local avatar fingertip press " + press.ToString("F4") + ", expected about " + (travel * 0.5f).ToString("F4"));
            // The avatar's idle animation can move the finger a little between the editor's estimate and Udon's.
            Check(name + " local avatar press half the travel", Mathf.Abs(press - travel * 0.5f) < 0.003f, "press " + press.ToString("F5"));

            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote A");
            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote B");
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after two joins");
            CheckLocalFingertipKept(name + " after joins");
            VRCPlayerApi remoteA = FindPlayer("PushButtonTest Remote A");
            if (!Check(name + ": find remote A", remoteA != null, "not spawned")) return;
            ClientSimMain.RemovePlayer(remoteA);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after a leave");
            CheckLocalFingertipKept(name + " after leave");
            VRCPlayerApi remoteB = FindPlayer("PushButtonTest Remote B");
            Check(name + " remote B tracked", SlotOf(remoteB, 0) >= 0, "missing");
            if (remoteB != null) ClientSimMain.RemovePlayer(remoteB);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after both left");
            CheckLocalFingertipKept(name + " after both left");
            Debug.Log("[PushButtonTest] " + name + ": now " + VRCPlayerApi.GetPlayerCount() + " players");
            button.transform.parent.position = savedParentPos;
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            int slot = SlotOf(Networking.LocalPlayer, 0);
            Check(name + " released once the button moves away", slot >= 0 && !((bool[])button.GetProgramVariable("fingertipEngaged"))[slot]
                && (float)button.GetProgramVariable("localPress") == 0f, "still pressed");
        });
    }

    // Plan step 5.3: a remote player's fingertip feeds remotePress, which moves the button but never fires. Spawns a
    // ClientSim remote player, moves the local player 1.5 m aside so only the remote fingertip is near, and moves the
    // button onto the remote avatar's left index fingertip: in front, then 0.03 m in (past the stop).
    static void AddRemotePressTest()
    {
        const string name = "remote press";
        const string remoteName = "PushButtonTest Remote C";
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            if (!SetUpActionTarget(name)) { button = null; return; }
            ClientSimMain.SpawnRemotePlayer(remoteName);
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            savedPlayerPos = localPlayer.GetPosition();
            savedPlayerRot = localPlayer.GetRotation();
            localPlayer.TeleportTo(savedPlayerPos + new Vector3(1.5f, 0f, 0f), savedPlayerRot);
            savedParentPos = button.transform.parent.position;
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (!Check(name + ": remote spawned and tracked", remote != null && SlotOf(remote, 0) >= 0, "not tracked")) return;
            SimulateVR(remote, true);
            Check(name + ": remote avatar has index bones", MoveButtonOntoLeftIndex(remote, -0.02f), "no finger bones");
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote == null) return;
            int slot = SlotOf(remote, 0);
            Check(name + " remote tip in front", slot >= 0 && ((bool[])button.GetProgramVariable("fingertipWasInFront"))[slot], "not in front");
            MoveButtonOntoLeftIndex(remote, 0.03f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float remotePress = (float)button.GetProgramVariable("remotePress");
            float localPress = (float)button.GetProgramVariable("localPress");
            float press = (float)button.GetProgramVariable("pressAmount");
            Debug.Log("[PushButtonTest] " + name + ": remote press " + remotePress.ToString("F4") + ", local press " + localPress.ToString("F4") + ", button " + press.ToString("F4"));
            int slot = SlotOf(remote, 0);
            Check(name + " remote tip engaged", slot >= 0 && ((bool[])button.GetProgramVariable("fingertipEngaged"))[slot], "not engaged");
            Check(name + " remote press stops at travel", Mathf.Abs(remotePress - travel) < 1e-6f, "remotePress " + remotePress.ToString("F5"));
            Check(name + " no local press", localPress == 0f, "localPress " + localPress.ToString("F5"));
            Check(name + " moves the button to the stop", Mathf.Abs(press - travel) < 1e-6f, "press " + press.ToString("F5"));
            CheckActionCount(name + " never fires", 0);
            Check(name + " stays armed", (bool)button.GetProgramVariable("armed"), "disarmed");
            button.transform.parent.position = savedParentPos;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + " released once the button moves away", (float)button.GetProgramVariable("remotePress") == 0f, "still pressed");
            CheckActionCount(name + " no fire after release", 0);
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) ClientSimMain.RemovePlayer(remote);
            Networking.LocalPlayer.TeleportTo(savedPlayerPos, savedPlayerRot);
            TearDownActionTarget(name);
        });
    }

    // Plan step 5.4: only players in VR press with their fingertips, judged by each player's own IsUserInVR. A local
    // VR fingertip pressing half-way releases once the local player counts as desktop, and a desktop local fingertip
    // never engages while the debug fingertip still presses. A desktop remote fingertip pushed past the stop does
    // nothing until that remote player counts as VR.
    static void AddVRFilterTest()
    {
        const string name = "vr filter";
        const string remoteName = "PushButtonTest Remote D";
        float travel = 0f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            travel = (float)button.GetProgramVariable("travelDistance");
            savedParentPos = button.transform.parent.position;
            SimulateVR(Networking.LocalPlayer, true);
            if (!Check(name + ": local avatar has index bones", MoveButtonOntoLeftIndex(Networking.LocalPlayer, -0.02f), "no finger bones")) button = null;
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            MoveButtonOntoLeftIndex(Networking.LocalPlayer, travel * 0.5f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckPlayerTip(name + " local VR tip", Networking.LocalPlayer, true, false);
            float press = (float)button.GetProgramVariable("localPress");
            Check(name + " local VR tip presses", Mathf.Abs(press - travel * 0.5f) < 0.003f, "localPress " + press.ToString("F5"));
            SimulateVR(Networking.LocalPlayer, false);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckPlayerTip(name + " local desktop tip released", Networking.LocalPlayer, false, false);
            Check(name + " no local press on desktop", (float)button.GetProgramVariable("localPress") == 0f, "localPress set");
            MoveButtonOntoLeftIndex(Networking.LocalPlayer, -0.02f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckPlayerTip(name + " local desktop tip in front ignored", Networking.LocalPlayer, false, false);
            MoveButtonOntoLeftIndex(Networking.LocalPlayer, travel * 0.5f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckPlayerTip(name + " local desktop tip pushed in", Networking.LocalPlayer, false, false);
            Check(name + " local desktop push does nothing", (float)button.GetProgramVariable("localPress") == 0f, "localPress set");
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            PlaceDebugTip(travel * 0.5f, 0f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckTip(name + " debug fingertip presses on desktop", true, false, travel * 0.5f);
            RemoveDebugTip();
            button.transform.parent.position = savedParentPos;
            ClientSimMain.SpawnRemotePlayer(remoteName);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (!Check(name + ": remote spawned and tracked", remote != null && SlotOf(remote, 0) >= 0, "not tracked")) { button = null; return; }
            Check(name + ": remote is on desktop", !remote.IsUserInVR(), "remote in VR");
            Check(name + ": remote avatar has index bones", MoveButtonOntoLeftIndex(remote, -0.02f), "no finger bones");
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            MoveButtonOntoLeftIndex(FindPlayer(remoteName), 0.03f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            CheckPlayerTip(name + " remote desktop tip", remote, false, false);
            float remotePress = (float)button.GetProgramVariable("remotePress");
            float press = (float)button.GetProgramVariable("pressAmount");
            Debug.Log("[PushButtonTest] " + name + ": desktop remote pushed in, remote press " + remotePress.ToString("F4") + ", button " + press.ToString("F4"));
            Check(name + " remote desktop push does nothing", remotePress == 0f && press == 0f,
                "remotePress " + remotePress.ToString("F5") + ", button " + press.ToString("F5"));
            SimulateVR(remote, true);
            MoveButtonOntoLeftIndex(remote, -0.02f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            CheckPlayerTip(name + " remote VR tip in front", remote, false, true);
            MoveButtonOntoLeftIndex(remote, 0.03f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            CheckPlayerTip(name + " remote VR tip pushed in", remote, true, false);
            float remotePress = (float)button.GetProgramVariable("remotePress");
            Check(name + " remote VR push reaches the stop", Mathf.Abs(remotePress - travel) < 1e-6f, "remotePress " + remotePress.ToString("F5"));
            button.transform.parent.position = savedParentPos;
            ClientSimMain.RemovePlayer(remote);
            simulatedVRPlayers.Clear();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + " released at the end", (float)button.GetProgramVariable("remotePress") == 0f
                && (float)button.GetProgramVariable("localPress") == 0f, "still pressed");
        });
    }

    // Plan step 5.5: a local VR fingertip press that fires pulses the hand that pressed, at hapticStrength. A strength of
    // 0 or the debug fingertip (no hand) fires without a pulse. ClientSim's own haptic handler only logs, and only with
    // its logs turned on, so the test records the calls through VRCPlayerApi._PlayHapticEventInHand.
    static void AddHapticsTest()
    {
        const string name = "haptics";
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            savedParentPos = button.transform.parent.position;
            savedHapticStrength = (float)Setting("hapticStrength");
            if (!SetUpActionTarget(name)) { button = null; return; }
            hapticCount = 0;
            VRCPlayerApi._PlayHapticEventInHand += RecordHaptic;
            SimulateVR(Networking.LocalPlayer, true);
            if (!Check(name + ": local avatar has index bones", MoveButtonOntoIndex(Networking.LocalPlayer, -0.02f, true), "no finger bones")) button = null;
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            MoveButtonOntoIndex(Networking.LocalPlayer, 0.03f, true);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " left press fires", 1);
            CheckHaptics(name + " left press", 1, VRC_Pickup.PickupHand.Left, savedHapticStrength);
            MoveButtonOntoIndex(Networking.LocalPlayer, -0.02f, false);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            MoveButtonOntoIndex(Networking.LocalPlayer, 0.03f, false);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " right press fires", 2);
            CheckHaptics(name + " right press", 2, VRC_Pickup.PickupHand.Right, savedHapticStrength);
            SetSetting("hapticStrength", 0f);
            MoveButtonOntoIndex(Networking.LocalPlayer, -0.02f, false);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            MoveButtonOntoIndex(Networking.LocalPlayer, 0.03f, false);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " strength 0 still fires", 3);
            Check(name + " strength 0 plays no pulse", hapticCount == 2, hapticCount + " pulses");
            SetSetting("hapticStrength", savedHapticStrength);
            button.transform.parent.position = savedParentPos;
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            PlaceDebugTip(0.03f, 0f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " debug fingertip fires", 4);
            Check(name + " debug fingertip plays no pulse", hapticCount == 2, hapticCount + " pulses");
            Debug.Log("[PushButtonTest] " + name + ": " + hapticCount + " pulses");
            RemoveDebugTip();
            VRCPlayerApi._PlayHapticEventInHand -= RecordHaptic;
            simulatedVRPlayers.Clear();
            TearDownActionTarget(name);
        });
    }
}
