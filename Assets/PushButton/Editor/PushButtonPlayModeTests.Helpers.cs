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

// Shared checks and helpers for placing fingertips, moving the button and counting actions.
public static partial class PushButtonPlayModeTests
{
    static void CountErrors(string message, string stackTrace, LogType type)
    {
        if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && !message.Contains("[PushButtonTest]")) errorCount++;
    }

    // Whether the manager hasn't put the button out of range (outOfRange is false).
    static bool InRange(UdonBehaviour pushButton)
    {
        return pushButton != null && false.Equals(pushButton.GetProgramVariable("outOfRange"));
    }

    // How many buttons the scene button's manager ticked on its last Update.
    static int TickedButtons()
    {
        UdonBehaviour manager = ManagerOf(button);
        return manager != null ? (int)manager.GetProgramVariable("tickedButtons") : -1;
    }

    // The manager's slots hold exactly the valid players in the instance, each once, and the button keeps four
    // fingertip slots for each of the manager's slots.
    static void CheckTrackedPlayers(string name)
    {
        UdonBehaviour manager = ManagerOf(button);
        if (!Check(name + " manager", manager != null, "no manager")) return;
        VRCPlayerApi[] slots = (VRCPlayerApi[])manager.GetProgramVariable("slotPlayers");
        int[] generations = (int[])manager.GetProgramVariable("slotGenerations");
        bool[] engaged = (bool[])button.GetProgramVariable("fingertipEngaged");
        bool[] wasInFront = (bool[])button.GetProgramVariable("fingertipWasInFront");
        int expected = VRCPlayerApi.GetPlayerCount();
        int count = 0;
        bool allValid = true;
        bool noDuplicates = true;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null) continue;
            count++;
            allValid &= Utilities.IsValid(slots[i]);
            noDuplicates &= System.Array.IndexOf(slots, slots[i]) == i;
        }
        Debug.Log("[PushButtonTest] " + name + ": " + count + " of " + expected + " players in " + slots.Length + " slots");
        Check(name + " count", count == expected, count + " in slots, expected " + expected);
        Check(name + " generations array", generations.Length == slots.Length, generations.Length + " generations for " + slots.Length + " slots");
        Check(name + " state arrays", engaged.Length == slots.Length * 4 && wasInFront.Length == slots.Length * 4,
            "engaged " + engaged.Length + ", wasInFront " + wasInFront.Length + " for " + slots.Length + " slots");
        Check(name + " all valid", allValid, "an invalid player has a slot");
        Check(name + " no duplicates", noDuplicates, "a player has two slots");
        Check(name + " local player tracked", System.Array.IndexOf(slots, Networking.LocalPlayer) >= 0, "missing");
    }

    static void CheckLocalFingertipKept(string name)
    {
        bool[] engaged = (bool[])button.GetProgramVariable("fingertipEngaged");
        int slot = SlotOf(Networking.LocalPlayer, 0);
        Check(name + " fingertip state kept", slot >= 0 && engaged[slot], "local fingertip state lost");
    }

    // The button's manager (the UdonBehaviour behind its manager field), or null.
    static UdonBehaviour ManagerOf(UdonBehaviour pushButton)
    {
        return pushButton != null ? pushButton.GetProgramVariable("manager") as UdonBehaviour : null;
    }

    // The scene's manager, found once (it can be inactive later, when Find no longer sees it).
    static UdonBehaviour sceneManager;
    static UdonBehaviour SceneManagerUdon()
    {
        if (sceneManager == null)
        {
            GameObject go = GameObject.Find(ManagerName);
            sceneManager = go != null ? go.GetComponent<UdonBehaviour>() : null;
        }
        return sceneManager;
    }

    // Plan step 8.8: one of the manager's shared settings (fingertipRadius, useIndexFingers, useMiddleFingers,
    // hapticStrength, clickPressDuration, and allowVRTrigger from 8.9), read from the scene's manager.
    static object Setting(string setting)
    {
        UdonBehaviour manager = SceneManagerUdon();
        return manager != null ? manager.GetProgramVariable(setting) : null;
    }

    // Sets one of the shared settings on the scene's manager and pushes it to every registered button.
    static void SetSetting(string setting, object value)
    {
        UdonBehaviour manager = SceneManagerUdon();
        if (manager == null) return;
        manager.SetProgramVariable(setting, value);
        manager.SendCustomEvent("ApplySettings");
    }

    // Pushes the scene manager's current settings to every registered button.
    static void ApplySettingsNow()
    {
        UdonBehaviour manager = SceneManagerUdon();
        if (manager != null) manager.SendCustomEvent("ApplySettings");
    }

    static VRCPlayerApi FindPlayer(string displayName)
    {
        foreach (VRCPlayerApi player in VRCPlayerApi.AllPlayers)
        {
            if (player != null && player.displayName == displayName) return player;
        }
        return null;
    }

    static void CheckButtonAtPress(string name, float press)
    {
        Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
        Quaternion restRot = (Quaternion)button.GetProgramVariable("restWorldRot");
        Vector3 axis = (Vector3)button.GetProgramVariable("pressAxis");
        Check(name, Near(button.transform.position, restWorld + restRot * (axis * press)), "got " + button.transform.position.ToString("F4"));
    }

    // Creates a ButtonActionExample target for the test (separate from the scene's own), points the button at it with
    // OnButtonPressed, and starts counting its "[ButtonActionExample] pressed" log lines.
    static bool SetUpActionTarget(string name)
    {
        AbstractUdonProgramSource program = AssetDatabase.LoadAssetAtPath<AbstractUdonProgramSource>(ExampleProgramPath);
        if (!Check(name + ": load ButtonActionExample program", program != null && program.SerializedProgramAsset != null, "no program asset")) return false;

        actionTarget = new GameObject("PushButtonTestActionTarget");
        actionTarget.SetActive(false);
        UdonBehaviour target = actionTarget.AddComponent<UdonBehaviour>();
        target.AssignProgramAndVariables(program.SerializedProgramAsset, new UdonVariableTable());
        actionTarget.SetActive(true);
        target.InitializeUdonContent();

        savedTarget = (UdonBehaviour)button.GetProgramVariable("targetBehaviour");
        savedEventName = (string)button.GetProgramVariable("eventName");
        button.SetProgramVariable("targetBehaviour", target);
        button.SetProgramVariable("eventName", "OnButtonPressed");
        actionCount = 0;
        Application.logMessageReceived += CountActionLogs;
        return true;
    }

    // Restores the button's target and event name and destroys the test target.
    static void TearDownActionTarget(string name)
    {
        Application.logMessageReceived -= CountActionLogs;
        if (button != null)
        {
            button.SetProgramVariable("targetBehaviour", savedTarget);
            button.SetProgramVariable("eventName", savedEventName);
        }
        if (actionTarget != null) Object.Destroy(actionTarget);
        Debug.Log("[PushButtonTest] " + name + ": " + actionCount + " action logs");
    }

    // One sweep 0 -> bottom -> hover -> 0: fires once on reaching the bottom, not again while hovering, re-arms at 0.
    static void AddSweepSteps(string name, int expectedCount, float settle)
    {
        AddStep(settle, () =>
        {
            if (button == null) return;
            SetLocalPress(1f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " fires at the bottom", expectedCount);
            Check(name + " disarmed after firing", !(bool)button.GetProgramVariable("armed"), "still armed");
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " hovering does not fire again", expectedCount);
            SetLocalPress(0f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            Check(name + " re-armed after release", (bool)button.GetProgramVariable("armed"), "not armed");
            CheckActionCount(name + " release does not fire", expectedCount);
        });
    }

    // Drives the local press with the debug fingertip: 0 puts it 1 cm in front of the face (disengaging it), anything
    // else pushes it (still centred in the footprint) to that fraction of travelDistance.
    static void SetLocalPress(float fraction)
    {
        if (button == null) return;
        float travel = (float)button.GetProgramVariable("travelDistance");
        PlaceDebugTip(fraction > 0f ? travel * fraction : -0.01f, 0f, 0f);
    }

    // Puts the debug fingertip (created and assigned to the scene manager's debugFingertip on first use) where its
    // press on `button` would be `press` meters (negative: in front of the face), offset u and v meters from the
    // footprint centre, in the rest frame.
    static void PlaceDebugTip(float press, float u, float v)
    {
        if (button == null) return;
        if (debugTip == null)
        {
            debugTip = new GameObject("PushButtonTestFingertip");
            UdonBehaviour manager = SceneManagerUdon();
            if (manager != null) manager.SetProgramVariable("debugFingertip", debugTip.transform);
        }
        debugTip.transform.position = RestFramePoint(press, (float)Setting("fingertipRadius"), u, v);
    }

    // Clears the manager's debugFingertip and destroys the test fingertip, so it can't press the button once the
    // button moves.
    static void RemoveDebugTip()
    {
        UdonBehaviour manager = SceneManagerUdon();
        if (manager != null) manager.SetProgramVariable("debugFingertip", null);
        if (debugTip != null) Object.Destroy(debugTip);
        debugTip = null;
    }

    // The world point where a fingertip of the given radius would press `press` meters, offset u and v from the
    // footprint centre, with the button's rest pose rebuilt from its parent now.
    static Vector3 RestFramePoint(float press, float radius, float u, float v)
    {
        Vector3 restWorld;
        Quaternion restRot;
        RestPoseNow(out restWorld, out restRot);
        Vector3 axis = (Vector3)button.GetProgramVariable("pressAxis");
        Vector3 axisU = (Vector3)button.GetProgramVariable("lateralAxisU");
        Vector3 axisV = (Vector3)button.GetProgramVariable("lateralAxisV");
        float centerU = (float)button.GetProgramVariable("footprintCenterU");
        float centerV = (float)button.GetProgramVariable("footprintCenterV");
        float face = (float)button.GetProgramVariable("frontFaceOffset");
        Vector3 local = axis * (press - face - radius) + axisU * (centerU + u) + axisV * (centerV + v);
        return restWorld + restRot * local;
    }

    static void RestPoseNow(out Vector3 restWorld, out Quaternion restRot)
    {
        Transform parent = button.transform.parent;
        Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
        Quaternion restLocalRot = (Quaternion)button.GetProgramVariable("restLocalRotation");
        restWorld = parent != null ? parent.TransformPoint(restLocal) : restLocal;
        restRot = parent != null ? parent.rotation * restLocalRot : restLocalRot;
    }

    // Moves the button's parent so the player's left index fingertip (estimated from their bones, as Udon does) would
    // press `press` meters, centred in the footprint. Returns false if the avatar lacks the finger bones.
    static bool MoveButtonOntoLeftIndex(VRCPlayerApi player, float press)
    {
        return MoveButtonOntoIndex(player, press, true);
    }

    // The same for either hand's index fingertip.
    static bool MoveButtonOntoIndex(VRCPlayerApi player, float press, bool left)
    {
        Vector3 d = player.GetBonePosition(left ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal);
        Vector3 i = player.GetBonePosition(left ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate);
        if (d == Vector3.zero || i == Vector3.zero) return false;
        Vector3 tip = d + (d - i) * 0.8f;
        float eyeHeight = player.GetAvatarEyeHeightAsMeters();
        float radius = (float)Setting("fingertipRadius") * (eyeHeight > 0f ? eyeHeight / 1.6f : 1f);
        button.transform.parent.position += tip - RestFramePoint(press, radius, 0f, 0f);
        return true;
    }

    // The index of the player's fingertip in the button's per-slot state arrays (from the manager's slot), or -1.
    static int SlotOf(VRCPlayerApi player, int fingertip)
    {
        UdonBehaviour manager = ManagerOf(button);
        if (manager == null || player == null) return -1;
        int index = System.Array.IndexOf((VRCPlayerApi[])manager.GetProgramVariable("slotPlayers"), player);
        return index < 0 ? -1 : index * 4 + fingertip;
    }

    static void CountActionLogs(string message, string stackTrace, LogType type)
    {
        if (!message.Contains("[ButtonActionExample] pressed")) return;
        actionCount++;
        System.Action action = onAction;
        onAction = null;
        if (action != null) action();
    }

    static void CheckActionCount(string name, int expected)
    {
        Check(name, actionCount == expected, "action logged " + actionCount + " times, expected " + expected);
    }

    static void CheckWorldRestPose(string name, Vector3 restLocal)
    {
        Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
        Quaternion restRot = (Quaternion)button.GetProgramVariable("restWorldRot");
        Quaternion restLocalRot = (Quaternion)button.GetProgramVariable("restLocalRotation");
        Vector3 expectedPos = buttonParent.TransformPoint(restLocal);
        Quaternion expectedRot = buttonParent.rotation * restLocalRot;
        Debug.Log("[PushButtonTest] " + name + ": " + restWorld.ToString("F4") + " " + restRot.eulerAngles.ToString("F2"));
        Check(name + " position", Near(restWorld, expectedPos), "got " + restWorld.ToString("F4") + ", expected " + expectedPos.ToString("F4"));
        Check(name + " rotation", Quaternion.Angle(restRot, expectedRot) < 0.01f,
            "got " + restRot.eulerAngles.ToString("F2") + ", expected " + expectedRot.eulerAngles.ToString("F2"));
    }
}
