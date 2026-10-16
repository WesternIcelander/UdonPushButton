using System.Collections.Generic;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

/// <summary>
/// Play Mode checks for <see cref="PushButton"/>, run unattended through the editor trigger:
/// queue `menu Tools/PushButton/Run Tests On Next Play`, then `play 120 [PushButtonTest] done`.
/// Logs `[PushButtonTest] FAIL name: reason` as errors and one `[PushButtonTest] done: N passed, M failed` line.
/// </summary>
[InitializeOnLoad]
public static class PushButtonPlayModeTests
{
    const string ArmedKey = "PushButtonTest.armed";
    const string ButtonPath = "Button/Button";
    const float Tolerance = 0.0001f;

    // A test is a list of steps, each run once after its delay (seconds since the previous step).
    delegate void Step();
    static readonly List<float> stepDelays = new List<float>();
    static readonly List<Step> steps = new List<Step>();
    static int stepIndex;
    static double nextStepTime;
    static int passed;
    static int failed;

    static UdonBehaviour button;
    static Transform buttonParent;
    static Vector3 firstRestWorldPos;
    static float savedReturnSpeed;
    static float releaseTime;
    static GameObject actionTarget;
    static UdonBehaviour savedTarget;
    static string savedEventName;
    static int actionCount;
    static float savedClickDuration;
    static float clickTime;
    static int lastTrackingLogCount;
    static int trackingLogs;
    static Vector3 savedPlayerPos;
    static Quaternion savedPlayerRot;
    static Vector3[] tipsBeforeTeleport;
    static GameObject debugTip;
    static Vector3 savedParentPos;
    // Player IDs that IsUserInVR reports as in VR (ClientSim players are all desktop), while the override is installed.
    static readonly HashSet<int> simulatedVRPlayers = new HashSet<int>();
    static bool isUserInVROverridden;
    static int hapticCount;
    static VRC_Pickup.PickupHand lastHapticHand;
    static float lastHapticAmplitude;
    static float savedHapticStrength;

    static PushButtonPlayModeTests()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    [MenuItem("Tools/PushButton/Run Tests On Next Play")]
    static void Arm()
    {
        SessionState.SetBool(ArmedKey, true);
        Debug.Log("[PushButtonTest] armed for the next Play Mode");
    }

    [MenuItem("Tools/PushButton/Add PushButton To Scene Button")]
    static void AddToSceneButton()
    {
        GameObject go = GameObject.Find(ButtonPath);
        if (go == null)
        {
            Debug.LogError("[PushButtonTest] no object at " + ButtonPath);
            return;
        }
        if (go.GetComponent<PushButton>() != null)
        {
            Debug.Log("[PushButtonTest] " + ButtonPath + " already has a PushButton");
            return;
        }
        UdonSharpUndo.AddComponent<PushButton>(go);
        EditorSceneManager.MarkSceneDirty(go.scene);
        Debug.Log("[PushButtonTest] added PushButton to " + ButtonPath);
    }

    static void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingPlayMode)
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= CountActionLogs;
            Application.logMessageReceived -= CountTrackingLogs;
            VRCPlayerApi._PlayHapticEventInHand -= RecordHaptic;
            RestoreIsUserInVR();
        }
        if (change != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(ArmedKey, false)) return;
        SessionState.EraseBool(ArmedKey);

        steps.Clear();
        stepDelays.Clear();
        stepIndex = 0;
        passed = 0;
        failed = 0;
        AddContactBoxTest();
        AddInputModeTest();
        AddFingertipTest();
        AddGizmoTest("gizmo unrotated");
        AddPressTest("press unrotated", true);
        AddTriggerTest();
        AddEngagementTest();
        AddClickTest();
        AddNetClickTest();
        AddRestPoseTest();
        AddPressTest("press rotated parent", false);
        AddGizmoTest("gizmo rotated parent");
        AddPlayerTrackingTest();
        AddRemotePressTest();
        AddVRFilterTest();
        AddHapticsTest();

        nextStepTime = EditorApplication.timeSinceStartup + stepDelays[0];
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < nextStepTime) return;
        steps[stepIndex]();
        stepIndex++;
        if (stepIndex < steps.Count)
        {
            nextStepTime = EditorApplication.timeSinceStartup + stepDelays[stepIndex];
            return;
        }
        EditorApplication.update -= Tick;
        Debug.Log("[PushButtonTest] done: " + passed + " passed, " + failed + " failed");
    }

    static void AddStep(float delay, Step step)
    {
        stepDelays.Add(delay);
        steps.Add(step);
    }

    // Plan step 2.2: the contact box comes from the BoxCollider, converted to meters.
    static void AddContactBoxTest()
    {
        AddStep(2f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            UdonBehaviour ub = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check("find button for contact box", ub != null, "no UdonBehaviour on " + ButtonPath)) return;
            float face = (float)ub.GetProgramVariable("frontFaceOffset");
            float halfU = (float)ub.GetProgramVariable("footprintHalfU");
            float halfV = (float)ub.GetProgramVariable("footprintHalfV");
            Vector3 axis = (Vector3)ub.GetProgramVariable("pressAxis");
            Debug.Log("[PushButtonTest] contact box: face " + face.ToString("F4") + ", footprint " + halfU.ToString("F4")
                + " x " + halfV.ToString("F4") + ", axis " + axis.ToString("F3"));
            Check("front face offset", Mathf.Abs(face - 0.01f) < Tolerance, "got " + face.ToString("F5"));
            Check("footprint half U", Mathf.Abs(halfU - 0.025f) < Tolerance, "got " + halfU.ToString("F5"));
            Check("footprint half V", Mathf.Abs(halfV - 0.025f) < Tolerance, "got " + halfV.ToString("F5"));
            Check("press axis normalized", Near(axis, new Vector3(0f, 0f, -1f)), "got " + axis.ToString("F4"));
        });
    }

    // Plan step 4.1: the collider becomes a trigger, and for the (desktop) ClientSim player the button is interactive
    // with the "Press" tooltip. Reads what ClientSim's tooltip shows through its helper on the button.
    static void AddInputModeTest()
    {
        const string name = "input mode";
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            UdonBehaviour ub = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", ub != null, "no UdonBehaviour on " + ButtonPath)) return;

            Collider collider = go.GetComponent<Collider>();
            Check(name + " collider is trigger", collider != null && collider.isTrigger, collider == null ? "no collider" : "isTrigger false");

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (!Check(name + ": ClientSim local player", localPlayer != null, "Networking.LocalPlayer is null")) return;
            bool inVR = localPlayer.IsUserInVR();
            Check(name + " local player on desktop", !inVR, "IsUserInVR true");
            Check(name + " localIsVR matches", (bool)ub.GetProgramVariable("localIsVR") == inVR, "localIsVR differs");
            Check(name + " interact enabled", !ub.DisableInteractive && ub.IsInteractive,
                "DisableInteractive " + ub.DisableInteractive + ", IsInteractive " + ub.IsInteractive);

            ClientSimUdonHelper helper = go.GetComponent<ClientSimUdonHelper>();
            if (!Check(name + ": ClientSim helper", helper != null, "no ClientSimUdonHelper on " + ButtonPath)) return;
            string tooltip = helper.GetInteractText();
            Debug.Log("[PushButtonTest] " + name + ": ClientSim can interact " + helper.CanInteract() + ", tooltip \"" + tooltip + "\"");
            Check(name + " ClientSim can interact", helper.CanInteract(), "CanInteract false");
            Check(name + " tooltip text", tooltip == "Press", "got \"" + tooltip + "\"");
        });
    }

    // Plan step 5.2: the local player's fingertip estimates (left in Udon's buffers by the per-player loop in Update, as
    // the local player is the only one in the instance at this point) match the
    // ClientSim avatar's finger bones, honour the finger toggles, carry the eye-height-scaled radius, and follow the
    // avatar when the player teleports 1 m sideways.
    static void AddFingertipTest()
    {
        const string name = "fingertips";
        Vector3 teleportOffset = new Vector3(1f, 0f, 0f);
        // Only VR players' fingertips are estimated (plan step 5.4), and ClientSim's local player is on desktop.
        AddStep(0.5f, () =>
        {
            if (Networking.LocalPlayer != null) SimulateVR(Networking.LocalPlayer, true);
        });
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            if (!Check(name + ": local player", Networking.LocalPlayer != null, "Networking.LocalPlayer is null")) { button = null; return; }
            CheckFingertips(name + " defaults", true, false);

            float eyeHeight = Networking.LocalPlayer.GetAvatarEyeHeightAsMeters();
            float radius = (float)button.GetProgramVariable("fingertipScaledRadius");
            float expectedRadius = (float)button.GetProgramVariable("fingertipRadius") * eyeHeight / 1.6f;
            Debug.Log("[PushButtonTest] " + name + ": eye height " + eyeHeight.ToString("F3") + " m, scaled radius " + radius.ToString("F5"));
            Check(name + " radius scaled by eye height", Mathf.Abs(radius - expectedRadius) < 1e-6f,
                "got " + radius.ToString("F6") + ", expected " + expectedRadius.ToString("F6"));

            button.SetProgramVariable("useIndexFingers", false);
            button.SetProgramVariable("useMiddleFingers", true);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckFingertips(name + " middle only", false, true);
            button.SetProgramVariable("useIndexFingers", true);
            button.SetProgramVariable("useMiddleFingers", false);

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            savedPlayerPos = localPlayer.GetPosition();
            savedPlayerRot = localPlayer.GetRotation();
            tipsBeforeTeleport = (Vector3[])((Vector3[])button.GetProgramVariable("fingertipPositions")).Clone();
            localPlayer.TeleportTo(savedPlayerPos + teleportOffset, savedPlayerRot);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckFingertips(name + " after teleport", true, false);
            Vector3[] tips = (Vector3[])button.GetProgramVariable("fingertipPositions");
            for (int f = 0; f < 2; f++)
            {
                Vector3 moved = tips[f] - tipsBeforeTeleport[f];
                Debug.Log("[PushButtonTest] " + name + " tip " + f + " moved " + moved.ToString("F4"));
                Check(name + " tip " + f + " follows the teleport", (moved - teleportOffset).magnitude < 0.02f, "moved " + moved.ToString("F4"));
            }
            Networking.LocalPlayer.TeleportTo(savedPlayerPos, savedPlayerRot);
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
        Vector3[] tips = (Vector3[])button.GetProgramVariable("fingertipPositions");
        bool[] valid = (bool[])button.GetProgramVariable("fingertipValid");
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

    // Plan step 2.4: the gizmo's rest box matches the button, and with travel 0.019 the full-depth box's
    // front face sits 1 mm in front of the plate face, at any parent rotation.
    static void AddGizmoTest(string name)
    {
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            PushButton proxy = go != null ? go.GetComponent<PushButton>() : null;
            if (!Check(name + ": find PushButton component", proxy != null, "no PushButton on " + ButtonPath)) return;
            Transform plate = go.transform.parent != null ? go.transform.parent.Find("ButtonPlate") : null;
            if (!Check(name + ": find plate", plate != null, "no ButtonPlate next to " + ButtonPath)) return;

            Vector3 restPos;
            Quaternion restRot;
            Vector3 boxCenter;
            Vector3 boxSize;
            Vector3 fullCenter;
            bool ok = proxy.GetGizmoGeometry(out restPos, out restRot, out boxCenter, out boxSize, out fullCenter);
            if (!Check(name + ": has geometry", ok, "GetGizmoGeometry returned false")) return;

            UdonBehaviour ub = go.GetComponent<UdonBehaviour>();
            Vector3 udonRestPos = (Vector3)ub.GetProgramVariable("restWorldPos");
            Quaternion udonRestRot = (Quaternion)ub.GetProgramVariable("restWorldRot");
            Check(name + " rest pose from Udon", Near(restPos, udonRestPos) && Quaternion.Angle(restRot, udonRestRot) < 0.01f,
                "got " + restPos.ToString("F4") + ", Udon has " + udonRestPos.ToString("F4"));
            Check(name + " rest box size", Near(boxSize, new Vector3(0.05f, 0.05f, 0.02f)), "got " + boxSize.ToString("F4"));
            Check(name + " rest box centre", Near(boxCenter, Vector3.zero), "got " + boxCenter.ToString("F4"));

            // The press axis is local -Z, so the front face of each box is its +Z face.
            Vector3 fullFront = restPos + restRot * (fullCenter + new Vector3(0f, 0f, boxSize.z * 0.5f));
            Vector3 plateFace = plate.TransformPoint(new Vector3(0f, 0f, 0.5f));
            float gap = Vector3.Dot(fullFront - plateFace, plate.forward);
            Debug.Log("[PushButtonTest] " + name + ": full-depth front face " + (gap * 1000f).ToString("F2") + " mm in front of the plate");
            Check(name + " full depth 1 mm in front of plate", Mathf.Abs(gap - 0.001f) < Tolerance, "gap " + gap.ToString("F5"));
        });
    }

    // Plan step 2.1: the rest pose is cached in Start and the world rest pose follows a moving parent.
    static void AddRestPoseTest()
    {
        AddStep(2f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check("find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            buttonParent = go.transform.parent;

            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            Check("rest local position", Near(restLocal, new Vector3(0f, 0f, 0.03f)), "got " + restLocal.ToString("F4"));
            CheckWorldRestPose("rest world pose at start", restLocal);
            firstRestWorldPos = (Vector3)button.GetProgramVariable("restWorldPos");

            buttonParent.position += new Vector3(1f, 0.5f, -2f);
            buttonParent.rotation = Quaternion.Euler(35f, 120f, -70f);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            CheckWorldRestPose("rest world pose after moving parent", restLocal);
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Check("rest world pose changed", !Near(restWorld, firstRestWorldPos), "still " + restWorld.ToString("F4"));
        });
    }

    // Plan steps 2.3 and 3.1: the button moves along the press axis from the world rest pose, jumps straight to a
    // deeper press, never goes past travelDistance, and springs back at returnSpeed. returnSpeed is lowered to
    // 0.01 m/s during the test so the spring-back is slow enough to catch half-way.
    // With the parent unrotated and travel 0.019, full travel puts the button 1 mm in front of the plate, (0, 1, 0.011).
    // The press comes from the debug fingertip, pushed from in front of the face to 0.03 m past it in one frame.
    static void AddPressTest(string name, bool checkAbsolute)
    {
        const float overPress = 0.03f;
        const float slowReturnSpeed = 0.01f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            buttonParent = go.transform.parent;
            savedReturnSpeed = (float)button.GetProgramVariable("returnSpeed");
            button.SetProgramVariable("returnSpeed", slowReturnSpeed);
            SetLocalPress(0f);
        });
        AddStep(0.2f, () =>
        {
            if (button == null) return;
            PlaceDebugTip(overPress, 0f, 0f);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Quaternion restRot = (Quaternion)button.GetProgramVariable("restWorldRot");
            Vector3 axis = (Vector3)button.GetProgramVariable("pressAxis");
            Vector3 expected = restWorld + restRot * (axis * travel);
            Vector3 actual = button.transform.position;
            Vector3 offsetInParent = buttonParent.InverseTransformVector(actual - restWorld);
            Debug.Log("[PushButtonTest] " + name + ": press " + press.ToString("F4") + " at " + actual.ToString("F4")
                + ", offset in parent " + offsetInParent.ToString("F4"));
            // At 0.01 m/s, easing in would have reached only 0.005 m by now.
            Check(name + " jumps in and stops at travel", Mathf.Abs(press - travel) < 1e-6f, "press " + press.ToString("F5") + ", travel " + travel.ToString("F5"));
            Check(name + " position", Near(actual, expected), "got " + actual.ToString("F4") + ", expected " + expected.ToString("F4"));
            Check(name + " along parent -Z", Near(offsetInParent, new Vector3(0f, 0f, -travel)), "offset " + offsetInParent.ToString("F4"));
            if (checkAbsolute)
                Check(name + " 1 mm in front of plate", Near(actual, new Vector3(0f, 1f, 0.011f)), "got " + actual.ToString("F4"));
            SetLocalPress(0f);
            releaseTime = Time.time;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            float expectedPress = travel - slowReturnSpeed * (Time.time - releaseTime);
            Debug.Log("[PushButtonTest] " + name + ": spring-back press " + press.ToString("F4") + ", expected about " + expectedPress.ToString("F4"));
            Check(name + " springs back gradually", press > 0f && press < travel, "press " + press.ToString("F5"));
            // Allow a couple of frames of slack between the editor's and Udon's view of the release.
            Check(name + " springs back at returnSpeed", Mathf.Abs(press - expectedPress) < 0.002f,
                "press " + press.ToString("F5") + ", expected " + expectedPress.ToString("F5"));
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Quaternion restRot = (Quaternion)button.GetProgramVariable("restWorldRot");
            Vector3 axis = (Vector3)button.GetProgramVariable("pressAxis");
            Check(name + " spring-back position", Near(button.transform.position, restWorld + restRot * (axis * press)),
                "got " + button.transform.position.ToString("F4"));
            button.SetProgramVariable("returnSpeed", savedReturnSpeed);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Check(name + " back at rest", press == 0f && Near(button.transform.position, restWorld),
                "press " + press.ToString("F5") + ", at " + button.transform.position.ToString("F4"));
        });
    }

    // Plan step 3.2: the local press (the debug fingertip) fires the action once per sweep to the bottom, and hovering
    // at the bottom or rising only part-way (above releaseFraction) doesn't fire again.
    // The target is a ButtonActionExample created for the test (the scene gets its own in step 7.2); presses are
    // counted from its "[ButtonActionExample] pressed" log line.
    static void AddTriggerTest()
    {
        const string name = "trigger";
        const float settle = 0.3f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            if (!SetUpActionTarget(name)) { button = null; return; }
            Check(name + " armed at start", (bool)button.GetProgramVariable("armed"), "not armed");
            SetLocalPress(0f);
        });
        AddSweepSteps(name + " sweep 1", 1, settle);
        AddSweepSteps(name + " sweep 2", 2, settle);

        // Down, part-way up (still above releaseFraction), down again: no second fire.
        AddStep(settle, () => SetLocalPress(1f));
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " partial: first bottom fires", 3);
            SetLocalPress(0.75f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            Check(name + " partial: still disarmed above releaseFraction", !(bool)button.GetProgramVariable("armed"), "re-armed");
            SetLocalPress(1f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " partial: no fire without re-arming", 3);
            SetLocalPress(0f);
        });

        // A remote player's press moving the button without firing is checked in AddRemotePressTest.
        AddStep(0.5f, () =>
        {
            if (button != null)
            {
                Check(name + " re-armed at the end", (bool)button.GetProgramVariable("armed"), "not armed");
                CheckActionCount(name + " total presses", 3);
                float press = (float)button.GetProgramVariable("pressAmount");
                Check(name + " back at rest", press == 0f, "press " + press.ToString("F5"));
            }
            TearDownActionTarget(name);
        });
    }

    // Plan step 5.3: the debug fingertip pushed in from the front presses the button by its depth past the face (plus
    // its radius), stops it at travelDistance and fires once; pulling out and pushing in again fires again. Entering
    // from the side or the back never engages, leaving sideways disengages, and the footprint is grown by the radius.
    static void AddEngagementTest()
    {
        const string name = "engagement";
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

    // Plan step 4.2: a ClientSim click (Interact through the button's ClientSimUdonHelper, as ClientSim's own
    // interact does) animates the button in over clickPressDuration and back at returnSpeed, and fires exactly once.
    // The first click uses a 1 s press-in and a 0.01 m/s spring-back so the animation can be caught part-way.
    static void AddClickTest()
    {
        const string name = "click";
        const float slowClickDuration = 1f;
        const float slowReturnSpeed = 0.01f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            ClientSimUdonHelper helper = go.GetComponent<ClientSimUdonHelper>();
            if (!Check(name + ": ClientSim helper", helper != null, "no ClientSimUdonHelper on " + ButtonPath)) { button = null; return; }
            if (!SetUpActionTarget(name)) { button = null; return; }
            savedReturnSpeed = (float)button.GetProgramVariable("returnSpeed");
            savedClickDuration = (float)button.GetProgramVariable("clickPressDuration");
            button.SetProgramVariable("returnSpeed", slowReturnSpeed);
            button.SetProgramVariable("clickPressDuration", slowClickDuration);
            Check(name + " armed and at rest before clicking", (bool)button.GetProgramVariable("armed") && (float)button.GetProgramVariable("pressAmount") == 0f, "not ready");
            helper.Interact();
            clickTime = Time.time;
        });
        AddStep(0.4f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            float expected = travel * (Time.time - clickTime) / slowClickDuration;
            Debug.Log("[PushButtonTest] " + name + ": press-in " + press.ToString("F4") + ", expected about " + expected.ToString("F4"));
            Check(name + " animates in part-way", press > 0f && press < travel, "press " + press.ToString("F5"));
            Check(name + " animates in over clickPressDuration", Mathf.Abs(press - expected) < travel * 0.1f,
                "press " + press.ToString("F5") + ", expected " + expected.ToString("F5"));
            CheckActionCount(name + " no fire before the bottom", 0);
            CheckButtonAtPress(name + " press-in position", press);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            float clickPress = (float)button.GetProgramVariable("clickPress");
            float released = Time.time - clickTime - slowClickDuration;
            Debug.Log("[PushButtonTest] " + name + ": " + released.ToString("F2") + " s after the bottom, press " + press.ToString("F4"));
            CheckActionCount(name + " fires once at the bottom", 1);
            // Re-arming follows the local press, and the click releases it to 0 right after the bottom frame,
            // so the button is armed again while it is still springing back.
            Check(name + " re-armed once the click releases", (bool)button.GetProgramVariable("armed"), "not armed");
            Check(name + " click released", !(bool)button.GetProgramVariable("clickActive") && clickPress == 0f,
                "clickActive " + button.GetProgramVariable("clickActive") + ", clickPress " + clickPress.ToString("F5"));
            Check(name + " springs back gradually", press > 0f && press < travel, "press " + press.ToString("F5"));
            Check(name + " springs back at returnSpeed", Mathf.Abs(press - (travel - slowReturnSpeed * released)) < 0.002f,
                "press " + press.ToString("F5") + ", expected " + (travel - slowReturnSpeed * released).ToString("F5"));
            CheckButtonAtPress(name + " spring-back position", press);
            button.SetProgramVariable("returnSpeed", savedReturnSpeed);
            button.SetProgramVariable("clickPressDuration", savedClickDuration);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " back at rest", press == 0f, "press " + press.ToString("F5"));
            Check(name + " re-armed", (bool)button.GetProgramVariable("armed"), "not armed");
            CheckActionCount(name + " no fire on the way back", 1);
            // Second click at the default speeds.
            GameObject.Find(ButtonPath).GetComponent<ClientSimUdonHelper>().Interact();
        });
        AddStep(0.6f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            CheckActionCount(name + " second click fires once", 2);
            Check(name + " second click back at rest", press == 0f, "press " + press.ToString("F5"));
            Check(name + " second click re-armed", (bool)button.GetProgramVariable("armed"), "not armed");
            TearDownActionTarget(name);
        });
    }

    // Plan step 4.3: the network event handler, called directly as another player's click would, plays the click
    // animation flagged non-local and never fires the action. With the button moved more than 5 m from the local
    // player, the event is ignored.
    static void AddNetClickTest()
    {
        const string name = "net click";
        const float slowClickDuration = 1f;
        const float slowReturnSpeed = 0.01f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            if (!SetUpActionTarget(name)) { button = null; return; }
            savedReturnSpeed = (float)button.GetProgramVariable("returnSpeed");
            savedClickDuration = (float)button.GetProgramVariable("clickPressDuration");
            button.SetProgramVariable("returnSpeed", slowReturnSpeed);
            button.SetProgramVariable("clickPressDuration", slowClickDuration);
            Check(name + " armed and at rest before the event", (bool)button.GetProgramVariable("armed") && (float)button.GetProgramVariable("pressAmount") == 0f, "not ready");
            button.SendCustomEvent("NetPlayClickAnimation");
            clickTime = Time.time;
            Check(name + " starts a non-local click", (bool)button.GetProgramVariable("clickActive") && !(bool)button.GetProgramVariable("clickIsLocal"),
                "clickActive " + button.GetProgramVariable("clickActive") + ", clickIsLocal " + button.GetProgramVariable("clickIsLocal"));
        });
        AddStep(0.4f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            float expected = travel * (Time.time - clickTime) / slowClickDuration;
            Debug.Log("[PushButtonTest] " + name + ": press-in " + press.ToString("F4") + ", expected about " + expected.ToString("F4"));
            Check(name + " animates in over clickPressDuration", press > 0f && Mathf.Abs(press - expected) < travel * 0.1f,
                "press " + press.ToString("F5") + ", expected " + expected.ToString("F5"));
            CheckButtonAtPress(name + " press-in position", press);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            Debug.Log("[PushButtonTest] " + name + ": after the bottom, press " + press.ToString("F4"));
            CheckActionCount(name + " never fires", 0);
            Check(name + " stays armed", (bool)button.GetProgramVariable("armed"), "disarmed");
            Check(name + " click released", !(bool)button.GetProgramVariable("clickActive"), "still active");
            Check(name + " reached the bottom and springs back", press > 0f && press < travel, "press " + press.ToString("F5"));
            button.SetProgramVariable("returnSpeed", savedReturnSpeed);
            button.SetProgramVariable("clickPressDuration", savedClickDuration);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " back at rest", press == 0f, "press " + press.ToString("F5"));
            CheckActionCount(name + " no fire on the way back", 0);

            // Out of range: move the assembly 20 m away from the local player and send the event again.
            Transform parent = button.transform.parent;
            Vector3 savedParentPos = parent.position;
            parent.position = savedParentPos + new Vector3(20f, 0f, 0f);
            button.SendCustomEvent("NetPlayClickAnimation");
            Check(name + " out of range is ignored", !(bool)button.GetProgramVariable("clickActive"), "click started");
            parent.position = savedParentPos;
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " out of range stays at rest", press == 0f, "press " + press.ToString("F5"));
            CheckActionCount(name + " out of range no fire", 0);
            TearDownActionTarget(name);
        });
    }

    // Plan steps 5.1 and 5.3: the button tracks everyone in the instance and logs the count on each join and leave.
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

            trackingLogs = 0;
            lastTrackingLogCount = -1;
            Application.logMessageReceived += CountTrackingLogs;
            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote A");
            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote B");
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            Check(name + " join logs", trackingLogs == 2, "got " + trackingLogs + " tracking logs");
            Check(name + " join log count", lastTrackingLogCount == VRCPlayerApi.GetPlayerCount(),
                "logged " + lastTrackingLogCount + ", " + VRCPlayerApi.GetPlayerCount() + " players");
            CheckTrackedPlayers(name + " after two joins");
            CheckLocalFingertipKept(name + " after joins");
            VRCPlayerApi remoteA = FindPlayer("PushButtonTest Remote A");
            if (!Check(name + ": find remote A", remoteA != null, "not spawned")) return;
            ClientSimMain.RemovePlayer(remoteA);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            Check(name + " leave log", trackingLogs == 3, "got " + trackingLogs + " tracking logs");
            Check(name + " leave log count", lastTrackingLogCount == VRCPlayerApi.GetPlayerCount(),
                "logged " + lastTrackingLogCount + ", " + VRCPlayerApi.GetPlayerCount() + " players");
            CheckTrackedPlayers(name + " after a leave");
            CheckLocalFingertipKept(name + " after leave");
            VRCPlayerApi remoteB = FindPlayer("PushButtonTest Remote B");
            Check(name + " remote B tracked", remoteB != null &&
                System.Array.IndexOf((VRCPlayerApi[])button.GetProgramVariable("trackedPlayers"), remoteB) >= 0, "missing");
            if (remoteB != null) ClientSimMain.RemovePlayer(remoteB);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            Application.logMessageReceived -= CountTrackingLogs;
            Check(name + " second leave log", trackingLogs == 4 && lastTrackingLogCount == VRCPlayerApi.GetPlayerCount(),
                "got " + trackingLogs + " tracking logs, last " + lastTrackingLogCount);
            CheckTrackedPlayers(name + " after both left");
            CheckLocalFingertipKept(name + " after both left");
            Debug.Log("[PushButtonTest] " + name + ": " + trackingLogs + " tracking logs, now " + VRCPlayerApi.GetPlayerCount() + " players");
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
            savedHapticStrength = (float)button.GetProgramVariable("hapticStrength");
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
            button.SetProgramVariable("hapticStrength", 0f);
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
            button.SetProgramVariable("hapticStrength", savedHapticStrength);
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

    // The tracked list holds exactly the valid players in the instance, with four fingertip slots each.
    static void CheckTrackedPlayers(string name)
    {
        VRCPlayerApi[] tracked = (VRCPlayerApi[])button.GetProgramVariable("trackedPlayers");
        bool[] engaged = (bool[])button.GetProgramVariable("fingertipEngaged");
        bool[] wasInFront = (bool[])button.GetProgramVariable("fingertipWasInFront");
        int expected = VRCPlayerApi.GetPlayerCount();
        Debug.Log("[PushButtonTest] " + name + ": tracking " + tracked.Length + " of " + expected + " players");
        Check(name + " count", tracked.Length == expected, "tracking " + tracked.Length + ", expected " + expected);
        Check(name + " state arrays", engaged.Length == tracked.Length * 4 && wasInFront.Length == tracked.Length * 4,
            "engaged " + engaged.Length + ", wasInFront " + wasInFront.Length);
        bool allValid = true;
        foreach (VRCPlayerApi player in tracked) allValid &= Utilities.IsValid(player);
        Check(name + " all valid", allValid, "an invalid player is tracked");
        Check(name + " local player tracked", System.Array.IndexOf(tracked, Networking.LocalPlayer) >= 0, "missing");
    }

    static void CheckLocalFingertipKept(string name)
    {
        VRCPlayerApi[] tracked = (VRCPlayerApi[])button.GetProgramVariable("trackedPlayers");
        bool[] engaged = (bool[])button.GetProgramVariable("fingertipEngaged");
        int local = System.Array.IndexOf(tracked, Networking.LocalPlayer);
        Check(name + " fingertip state kept", local >= 0 && engaged[local * 4], "local fingertip state lost");
    }

    static VRCPlayerApi FindPlayer(string displayName)
    {
        foreach (VRCPlayerApi player in VRCPlayerApi.AllPlayers)
        {
            if (player != null && player.displayName == displayName) return player;
        }
        return null;
    }

    static void CountTrackingLogs(string message, string stackTrace, LogType type)
    {
        const string prefix = "[PushButton] Button: tracking ";
        if (!message.StartsWith(prefix)) return;
        trackingLogs++;
        int count;
        if (int.TryParse(message.Substring(prefix.Length).Split(' ')[0], out count)) lastTrackingLogCount = count;
    }

    static void CheckButtonAtPress(string name, float press)
    {
        Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
        Quaternion restRot = (Quaternion)button.GetProgramVariable("restWorldRot");
        Vector3 axis = (Vector3)button.GetProgramVariable("pressAxis");
        Check(name, Near(button.transform.position, restWorld + restRot * (axis * press)), "got " + button.transform.position.ToString("F4"));
    }

    // Creates a ButtonActionExample target for the test (the scene gets its own in step 7.2), points the button at
    // it with OnButtonPressed, and starts counting its "[ButtonActionExample] pressed" log lines.
    static bool SetUpActionTarget(string name)
    {
        AbstractUdonProgramSource program = AssetDatabase.LoadAssetAtPath<AbstractUdonProgramSource>("Assets/PushButton/Examples/ButtonActionExample.asset");
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

    // Puts the debug fingertip (created and assigned to debugFingertip on first use) where its press would be `press`
    // meters (negative: in front of the face), offset u and v meters from the footprint centre, in the rest frame.
    static void PlaceDebugTip(float press, float u, float v)
    {
        if (button == null) return;
        if (debugTip == null)
        {
            debugTip = new GameObject("PushButtonTestFingertip");
            button.SetProgramVariable("debugFingertip", debugTip.transform);
        }
        debugTip.transform.position = RestFramePoint(press, (float)button.GetProgramVariable("fingertipRadius"), u, v);
    }

    // Clears debugFingertip and destroys the test fingertip, so it can't press the button once the button moves.
    static void RemoveDebugTip()
    {
        if (button != null) button.SetProgramVariable("debugFingertip", null);
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
        float radius = (float)button.GetProgramVariable("fingertipRadius") * (eyeHeight > 0f ? eyeHeight / 1.6f : 1f);
        button.transform.parent.position += tip - RestFramePoint(press, radius, 0f, 0f);
        return true;
    }

    static int SlotOf(VRCPlayerApi player, int fingertip)
    {
        int index = System.Array.IndexOf((VRCPlayerApi[])button.GetProgramVariable("trackedPlayers"), player);
        return index < 0 ? -1 : index * 4 + fingertip;
    }

    static void CountActionLogs(string message, string stackTrace, LogType type)
    {
        if (message.Contains("[ButtonActionExample] pressed")) actionCount++;
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

    static bool Near(Vector3 a, Vector3 b)
    {
        return (a - b).sqrMagnitude < Tolerance * Tolerance;
    }

    static bool Check(string name, bool ok, string reason)
    {
        if (ok) passed++;
        else
        {
            failed++;
            Debug.LogError("[PushButtonTest] FAIL " + name + ": " + reason);
        }
        return ok;
    }
}
