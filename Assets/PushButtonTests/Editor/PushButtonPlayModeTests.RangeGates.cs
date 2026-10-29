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

// Plan phase 6: the range gate and the per-player hand early-out.
public static partial class PushButtonPlayModeTests
{
    // Plan step 6.1: with the local player teleported more than 5 m away, the button snaps to rest at once (not at
    // the slow returnSpeed), its engagement and click state are cleared, and Update stops evaluating fingertips.
    // Back in range it works normally again: a tip that stayed inside has to come in from the front to press.
    static void AddRangeGateTest(string setup = "")
    {
        string name = "range gate" + setup;
        const float settle = 0.3f;
        const float slowReturnSpeed = 0.01f;
        Vector3 awayOffset = new Vector3(10f, 0f, 0f);
        float travel = 0f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            if (!SetUpActionTarget(name)) { button = null; return; }
            travel = (float)button.GetProgramVariable("travelDistance");
            savedReturnSpeed = (float)button.GetProgramVariable("returnSpeed");
            savedClickDuration = (float)Setting("clickPressDuration");
            button.SetProgramVariable("returnSpeed", slowReturnSpeed);
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            savedPlayerPos = localPlayer.GetPosition();
            savedPlayerRot = localPlayer.GetRotation();
            Check(name + " starts in range", !(bool)button.GetProgramVariable("outOfRange"), "out of range");
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            PlaceDebugTip(0.03f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " pressed before leaving", true, false, travel);
            CheckActionCount(name + " fires before leaving", 1);
            Check(name + " disarmed before leaving", !(bool)button.GetProgramVariable("armed"), "still armed");
            Networking.LocalPlayer.TeleportTo(savedPlayerPos + awayOffset, savedPlayerRot);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Debug.Log("[PushButtonTest] " + name + ": out of range, press " + press.ToString("F4") + ", player at " + Networking.LocalPlayer.GetPosition().ToString("F2"));
            Check(name + " out of range", (bool)button.GetProgramVariable("outOfRange"), "still in range");
            // At 0.01 m/s, springing back would still be most of the way down after 0.3 s.
            Check(name + " snaps to rest", press == 0f, "press " + press.ToString("F5"));
            CheckButtonAtPress(name + " at rest position", 0f);
            CheckTip(name + " engagement cleared", false, false, 0f);
            Check(name + " re-armed by the reset", (bool)button.GetProgramVariable("armed"), "not armed");
            // Pull the tip out in front: while out of range nothing is evaluated, so it doesn't register in front.
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " not evaluated out of range", false, false, 0f);
            PlaceDebugTip(0.03f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " push out of range ignored", false, false, 0f);
            Check(name + " push out of range stays at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "moved");
            CheckActionCount(name + " push out of range does not fire", 1);
            Networking.LocalPlayer.TeleportTo(savedPlayerPos, savedPlayerRot);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            Check(name + " back in range", !(bool)button.GetProgramVariable("outOfRange"), "still out of range");
            // The tip is still inside the button, but it never came in from the front, so it doesn't press.
            CheckTip(name + " tip left inside does not engage", false, false, 0f);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " in front again", false, true, 0f);
            PlaceDebugTip(0.03f, 0f, 0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckTip(name + " pushed again", true, false, travel);
            CheckActionCount(name + " fires again back in range", 2);
            PlaceDebugTip(-0.01f, 0f, 0f);
            button.SetProgramVariable("returnSpeed", savedReturnSpeed);
        });

        // A click still pressing in is cut off when the player leaves, and doesn't fire.
        AddStep(1f, () =>
        {
            if (button == null) return;
            Check(name + " at rest before clicking", (float)button.GetProgramVariable("pressAmount") == 0f && (bool)button.GetProgramVariable("armed"), "not ready");
            button.SetProgramVariable("returnSpeed", slowReturnSpeed);
            SetSetting("clickPressDuration", 1f);
            TapWithHelper(GameObject.Find(ButtonPath));
        });
        AddStep(0.4f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " click part-way", press > 0f && press < travel, "press " + press.ToString("F5"));
            Networking.LocalPlayer.TeleportTo(savedPlayerPos + awayOffset, savedPlayerRot);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " click cleared out of range", !(bool)button.GetProgramVariable("clickActive") && (float)button.GetProgramVariable("clickPress") == 0f,
                "clickActive " + button.GetProgramVariable("clickActive") + ", clickPress " + button.GetProgramVariable("clickPress"));
            Check(name + " click snapped to rest", press == 0f, "press " + press.ToString("F5"));
            CheckActionCount(name + " cut-off click does not fire", 2);
            Networking.LocalPlayer.TeleportTo(savedPlayerPos, savedPlayerRot);
            button.SetProgramVariable("returnSpeed", savedReturnSpeed);
            SetSetting("clickPressDuration", savedClickDuration);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            Check(name + " player back at start", (Networking.LocalPlayer.GetPosition() - savedPlayerPos).magnitude < 0.01f,
                "at " + Networking.LocalPlayer.GetPosition().ToString("F3"));
            Check(name + " in range at the end", !(bool)button.GetProgramVariable("outOfRange"), "out of range");
            Check(name + " at rest at the end", (float)button.GetProgramVariable("pressAmount") == 0f, "pressed");
            Check(name + " armed at the end", (bool)button.GetProgramVariable("armed"), "not armed");
            TearDownActionTarget(name);
        });
    }

    // Plan step 6.2: a VR player whose hands are both more than 0.5 m from the button is skipped and their fingertip
    // state cleared, while a player near the button presses as before. A remote VR player is spawned and the local
    // VR player stands 1.5 m aside, so the button can sit at one player's hand, the other's, or away from both.
    static void AddHandEarlyOutTest()
    {
        const string name = "hand early-out";
        const string remoteName = "PushButtonTest Remote E";
        float travel = 0f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            travel = (float)button.GetProgramVariable("travelDistance");
            savedParentPos = button.transform.parent.position;
            ClientSimMain.SpawnRemotePlayer(remoteName);
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            savedPlayerPos = localPlayer.GetPosition();
            savedPlayerRot = localPlayer.GetRotation();
            localPlayer.TeleportTo(savedPlayerPos + new Vector3(1.5f, 0f, 0f), savedPlayerRot);
            SimulateVR(localPlayer, true);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (!Check(name + ": remote spawned and tracked", remote != null && SlotOf(remote, 0) >= 0, "not tracked")) { button = null; return; }
            SimulateVR(remote, true);
            if (!Check(name + ": remote avatar has index bones", MoveButtonOntoLeftIndex(remote, -0.02f), "no finger bones")) button = null;
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckPlayerTip(name + " remote tip near", FindPlayer(remoteName), false, true);
            MoveButtonOntoLeftIndex(FindPlayer(remoteName), 0.03f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckPlayerTip(name + " remote tip pushed in", FindPlayer(remoteName), true, false);
            float remotePress = (float)button.GetProgramVariable("remotePress");
            Check(name + " remote press unchanged", Mathf.Abs(remotePress - travel) < 1e-6f, "remotePress " + remotePress.ToString("F5"));
            MoveButtonOntoLeftIndex(Networking.LocalPlayer, -0.02f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckPlayerTip(name + " remote tip released when far", FindPlayer(remoteName), false, false);
            CheckPlayerTip(name + " local tip near", Networking.LocalPlayer, false, true);
            Check(name + " remote press cleared", (float)button.GetProgramVariable("remotePress") == 0f, "still pressed");
            MoveButtonOntoLeftIndex(Networking.LocalPlayer, travel * 0.5f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckPlayerTip(name + " local tip pushed in", Networking.LocalPlayer, true, false);
            float press = (float)button.GetProgramVariable("localPress");
            Check(name + " local press unchanged", Mathf.Abs(press - travel * 0.5f) < 0.003f, "localPress " + press.ToString("F5"));
            // 1 m up from the local hand: still within 5 m, so the range gate doesn't stop the frame.
            button.transform.parent.position += new Vector3(0f, 1f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckPlayerTip(name + " local tip released when far", Networking.LocalPlayer, false, false);
            Check(name + " local press cleared", (float)button.GetProgramVariable("localPress") == 0f, "still pressed");
            Check(name + " in range", !(bool)button.GetProgramVariable("outOfRange"), "range gate closed");
            SimulateVR(FindPlayer(remoteName), false);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            button.transform.parent.position = savedParentPos;
            ClientSimMain.RemovePlayer(FindPlayer(remoteName));
            Networking.LocalPlayer.TeleportTo(savedPlayerPos, savedPlayerRot);
            simulatedVRPlayers.Clear();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + " released at the end", (float)button.GetProgramVariable("remotePress") == 0f
                && (float)button.GetProgramVariable("localPress") == 0f, "still pressed");
        });
    }
}
