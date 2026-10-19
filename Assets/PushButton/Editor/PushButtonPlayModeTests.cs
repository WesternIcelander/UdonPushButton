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

/// <summary>
/// Play Mode checks for <see cref="PushButton"/>, run unattended through the editor trigger:
/// queue `menu Tools/PushButton/Run Tests On Next Play`, then `play 120 [PushButtonTest] done`.
/// Logs `[PushButtonTest] FAIL name: reason` as errors and one `[PushButtonTest] done: N passed, M failed` line.
/// </summary>
[InitializeOnLoad]
public static class PushButtonPlayModeTests
{
    const string ArmedKey = "PushButtonTest.armed";
    const string AssemblyName = "PushButtonAssembly";
    const string ButtonPath = AssemblyName + "/Button";
    const string ManagerName = "PushButtonManager";
    const string NoManagerName = "PushButtonTestNoManager";
    const string ButtonProgramPath = "Assets/PushButton/PushButton.asset";
    const string ExampleProgramPath = "Assets/PushButton/Examples/ButtonActionExample.asset";
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
    static Vector3 savedPlayerPos;
    static Quaternion savedPlayerRot;
    static Vector3[] tipsBeforeTeleport;
    static GameObject debugTip;
    static Vector3 savedParentPos;
    static Vector3 sceneParentPos;
    static Quaternion sceneParentRot;
    // Player IDs that IsUserInVR reports as in VR (ClientSim players are all desktop), while the override is installed.
    static readonly HashSet<int> simulatedVRPlayers = new HashSet<int>();
    static bool isUserInVROverridden;
    static int hapticCount;
    static int noManagerWarnings;
    static UdonBehaviour savedButton;
    static GameObject noManagerButton;
    static Vector3 noManagerStartPos;
    static VRC_Pickup.PickupHand lastHapticHand;
    static float lastHapticAmplitude;
    static float savedHapticStrength;
    // GetBonePosition calls per Time.frameCount, while the counting hook is installed.
    static readonly Dictionary<int, int> boneReadsPerFrame = new Dictionary<int, int>();
    static bool boneReadsCounted;
    static int boneCountFrom;
    static readonly List<GameObject> extraButtons = new List<GameObject>();

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

    // Plan step 7.2: renames the parent to PushButtonAssembly and turns it 180° on Y to face the spawn, gives the child
    // button a PushButton with travel 0.019 along local -Z, points it at a ButtonActionExample on the parent with
    // OnButtonPressed, wires it to the scene's first PushButtonManager (adding one at the scene root if there is none,
    // plan step 8.1), and saves the scene. Safe to run again.
    [MenuItem("Tools/PushButton/Wire Up Scene")]
    static void WireUpScene()
    {
        GameObject go = GameObject.Find(ButtonPath);
        if (go == null) go = GameObject.Find("Button/Button");
        if (go == null || go.transform.parent == null)
        {
            Debug.LogError("[PushButtonTest] no button at " + ButtonPath + " or Button/Button");
            return;
        }
        Transform parent = go.transform.parent;
        Undo.RecordObjects(new Object[] { parent.gameObject, parent }, "Wire Up PushButton Scene");
        parent.name = AssemblyName;
        parent.localRotation = Quaternion.Euler(0f, 180f, 0f);

        PushButton pushButton = go.GetComponent<PushButton>();
        if (pushButton == null) pushButton = UdonSharpUndo.AddComponent<PushButton>(go);
        ButtonActionExample example = parent.GetComponent<ButtonActionExample>();
        if (example == null) example = UdonSharpUndo.AddComponent<ButtonActionExample>(parent.gameObject);

        PushButtonManager manager = FirstManager(go.scene);
        if (manager == null)
        {
            GameObject managerObject = new GameObject(ManagerName);
            SceneManager.MoveGameObjectToScene(managerObject, go.scene);
            Undo.RegisterCreatedObjectUndo(managerObject, "Wire Up PushButton Scene");
            manager = UdonSharpUndo.AddComponent<PushButtonManager>(managerObject);
        }

        SerializedObject serialized = new SerializedObject(pushButton);
        serialized.FindProperty("manager").objectReferenceValue = manager;
        serialized.FindProperty("localPressAxis").vector3Value = new Vector3(0f, 0f, -1f);
        serialized.FindProperty("travelDistance").floatValue = 0.019f;
        serialized.FindProperty("targetBehaviour").objectReferenceValue = UdonSharpEditorUtility.GetBackingUdonBehaviour(example);
        serialized.FindProperty("eventName").stringValue = "OnButtonPressed";
        serialized.ApplyModifiedProperties();
        UdonSharpEditorUtility.CopyProxyToUdon(pushButton);

        EditorSceneManager.MarkSceneDirty(go.scene);
        EditorSceneManager.SaveScene(go.scene);
        Debug.Log("[PushButtonTest] wired up " + ButtonPath + " and saved " + go.scene.name);
    }

    // The scene's first PushButtonManager in hierarchy order (root objects in scene order, then depth first), or null.
    static PushButtonManager FirstManager(UnityEngine.SceneManagement.Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            PushButtonManager manager = root.GetComponentInChildren<PushButtonManager>(true);
            if (manager != null) return manager;
        }
        return null;
    }

    static void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingPlayMode)
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= CountActionLogs;
            Application.logMessageReceived -= CountNoManagerWarnings;
            VRCPlayerApi._PlayHapticEventInHand -= RecordHaptic;
            RestoreIsUserInVR();
            StopCountingBoneReads();
        }
        if (change != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(ArmedKey, false)) return;
        SessionState.EraseBool(ArmedKey);

        steps.Clear();
        stepDelays.Clear();
        stepIndex = 0;
        passed = 0;
        failed = 0;
        AddSceneWiringTest();
        AddContactBoxTest();
        AddInputModeTest();
        AddFingertipTest();
        AddGizmoTest("gizmo unrotated");
        AddPressTest("press unrotated", true);
        AddTriggerTest();
        AddEngagementTest();
        AddClickTest();
        AddNetClickTest();
        AddRangeGateTest();
        AddRestPoseTest();
        AddPressTest("press rotated parent", false);
        AddGizmoTest("gizmo rotated parent");
        AddPlayerTrackingTest();
        AddManagerSlotsTest();
        AddRemotePressTest();
        AddVRFilterTest();
        AddHapticsTest();
        AddHandEarlyOutTest();
        AddBoneReadCountTest();
        AddNoManagerTest();
        AddInspectorTest();
        AddRegressionPass(" (rotated parent)", true);
        AddRegressionPass(" (no plate)", false);
        AddRegressionCleanup();

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

    // Plan step 7.2: the saved scene is wired up (parent turned to face the spawn, travel 0.019 along local -Z, the
    // ButtonActionExample on the parent called with OnButtonPressed), and a ClientSim desktop click logs once.
    static void AddSceneWiringTest()
    {
        const string name = "scene wiring";
        AddStep(2f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            Transform parent = go.transform.parent;
            sceneParentPos = parent.position;
            sceneParentRot = parent.rotation;
            Check(name + " parent faces the spawn", Quaternion.Angle(parent.rotation, Quaternion.Euler(0f, 180f, 0f)) < 0.01f,
                "rotation " + parent.eulerAngles.ToString("F2"));
            float travel = (float)button.GetProgramVariable("travelDistance");
            Check(name + " travel 0.019", Mathf.Abs(travel - 0.019f) < 1e-6f, "got " + travel.ToString("F5"));
            Vector3 axis = (Vector3)button.GetProgramVariable("localPressAxis");
            Check(name + " local press axis", Near(axis, new Vector3(0f, 0f, -1f)), "got " + axis.ToString("F4"));

            UdonBehaviour target = (UdonBehaviour)button.GetProgramVariable("targetBehaviour");
            AbstractUdonProgramSource program = AssetDatabase.LoadAssetAtPath<AbstractUdonProgramSource>(ExampleProgramPath);
            Check(name + " target is the ButtonActionExample on the parent", target != null && target.gameObject == parent.gameObject
                && target.programSource == program, target == null ? "no target" : "got " + target.gameObject.name + " running " + target.programSource);
            string eventName = (string)button.GetProgramVariable("eventName");
            Check(name + " event OnButtonPressed", eventName == "OnButtonPressed", "got \"" + eventName + "\"");
            UdonBehaviour manager = ManagerOf(button);
            Check(name + " wired to the scene's manager", manager != null && manager.gameObject.name == ManagerName && manager.transform.parent == null,
                manager == null ? "no manager" : "got " + manager.gameObject.name);

            actionCount = 0;
            Application.logMessageReceived += CountActionLogs;
            go.GetComponent<ClientSimUdonHelper>().Interact();
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            Application.logMessageReceived -= CountActionLogs;
            Debug.Log("[PushButtonTest] " + name + ": desktop click, " + actionCount + " action logs");
            CheckActionCount(name + " desktop click logs once", 1);
            Check(name + " back at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "still pressed");
        });
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

    // Plan step 7.1: PushButton's inspector is PushButtonEditor, and its event dropdown lists an UdonSharp target's
    // public events (ButtonActionExample: OnButtonPressed) but falls back to free text without an UdonSharp program.
    static void AddInspectorTest()
    {
        const string name = "inspector";
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            PushButton proxy = go != null ? go.GetComponent<PushButton>() : null;
            if (!Check(name + ": find PushButton component", proxy != null, "no PushButton on " + ButtonPath)) return;
            // UdonSharp's own override editor draws every UdonSharpBehaviour and hosts the user's editor in _userEditor.
            Editor editor = Editor.CreateEditor(proxy);
            System.Reflection.FieldInfo userEditorField = editor != null
                ? editor.GetType().GetField("_userEditor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                : null;
            Object userEditor = userEditorField != null ? (Object)userEditorField.GetValue(editor) : null;
            Check(name + " uses PushButtonEditor", userEditor is PushButtonEditor,
                "got " + (editor != null ? editor.GetType().Name : "null") + " hosting " + (userEditor != null ? userEditor.GetType().Name : "null"));
            if (editor != null) Object.DestroyImmediate(editor);

            AbstractUdonProgramSource program = AssetDatabase.LoadAssetAtPath<AbstractUdonProgramSource>(ExampleProgramPath);
            if (!Check(name + ": load ButtonActionExample program", program != null, "no program asset")) return;
            GameObject holder = new GameObject("PushButtonTestInspectorTarget");
            holder.SetActive(false);
            UdonBehaviour example = holder.AddComponent<UdonBehaviour>();
            UdonBehaviour noProgram = holder.AddComponent<UdonBehaviour>();
            example.programSource = program;

            string[] events = PushButtonEditor.GetEventNames(example);
            string listed = events != null ? string.Join(", ", events) : "null";
            Check(name + " ButtonActionExample lists OnButtonPressed", events != null && events.Length == 1 && events[0] == "OnButtonPressed", "got " + listed);
            Check(name + " no target falls back to text", PushButtonEditor.GetEventNames(null) == null, "not null");
            Check(name + " non-UdonSharp program falls back to text", PushButtonEditor.GetEventNames(noProgram) == null, "not null");

            string[] buttonEvents = PushButtonEditor.GetEventNames(go.GetComponent<UdonBehaviour>());
            listed = buttonEvents != null ? string.Join(", ", buttonEvents) : "null";
            Check(name + " PushButton lists its own events", buttonEvents != null && System.Array.IndexOf(buttonEvents, "NetPlayClickAnimation") >= 0, "got " + listed);
            Check(name + " built-in events left out", buttonEvents != null && System.Array.IndexOf(buttonEvents, "Interact") < 0
                && System.Array.IndexOf(buttonEvents, "OnPlayerJoined") < 0 && System.Array.IndexOf(buttonEvents, "GetType") < 0, "got " + listed);
            Object.DestroyImmediate(holder);
        });
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

    // Plan step 8.1: the click (4.2), engagement (5.3) and range gate (6.1) checks again, with the parent moved
    // 1 m aside and rotated to (35, 120, -70), and the ButtonPlate either active or disabled. `setup` names the setup in
    // every check's name.
    static void AddRegressionPass(string setup, bool plateActive)
    {
        string name = "regression" + setup;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            buttonParent = go.transform.parent;
            Transform plate = buttonParent.Find("ButtonPlate");
            if (!Check(name + ": find plate", plate != null, "no ButtonPlate next to " + ButtonPath)) { button = null; return; }
            RemoveDebugTip();
            SetLocalPress(0f);
            buttonParent.position = sceneParentPos + new Vector3(1f, 0.5f, -2f);
            buttonParent.rotation = Quaternion.Euler(35f, 120f, -70f);
            plate.gameObject.SetActive(plateActive);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            CheckWorldRestPose(name + " rest pose", restLocal);
            Transform plate = buttonParent.Find("ButtonPlate");
            Check(name + " plate " + (plateActive ? "active" : "disabled"), plate.gameObject.activeInHierarchy == plateActive,
                "activeInHierarchy " + plate.gameObject.activeInHierarchy);
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Debug.Log("[PushButtonTest] " + name + ": parent at " + buttonParent.position.ToString("F2") + " " + buttonParent.eulerAngles.ToString("F2")
                + ", plate " + (plateActive ? "active" : "disabled") + ", local player "
                + (Networking.LocalPlayer.GetPosition() - restWorld).magnitude.ToString("F2") + " m away");
            Check(name + " at rest and armed", (float)button.GetProgramVariable("pressAmount") == 0f && (bool)button.GetProgramVariable("armed"), "not ready");
        });
        AddClickTest(setup);
        AddEngagementTest(setup);
        AddRangeGateTest(setup);
    }

    // Puts the parent back where the scene has it and turns the plate back on.
    static void AddRegressionCleanup()
    {
        AddStep(0.3f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            if (go == null) return;
            button = go.GetComponent<UdonBehaviour>();
            RemoveDebugTip();
            Transform parent = go.transform.parent;
            parent.position = sceneParentPos;
            parent.rotation = sceneParentRot;
            Transform plate = parent.Find("ButtonPlate");
            if (plate != null) plate.gameObject.SetActive(true);
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
    // With the scene's parent (turned only on Y) and travel 0.019, full travel puts the button 1 mm in front of the plate,
    // at (0, 0, 0.011) in the parent.
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
                Check(name + " 1 mm in front of plate", Near(actual, buttonParent.TransformPoint(new Vector3(0f, 0f, 0.011f))), "got " + actual.ToString("F4"));
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
    // The target is a ButtonActionExample created for the test (not the scene's own); presses are
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

    // Plan step 4.2: a ClientSim click (Interact through the button's ClientSimUdonHelper, as ClientSim's own
    // interact does) animates the button in over clickPressDuration and back at returnSpeed, and fires exactly once.
    // The first click uses a 1 s press-in and a 0.01 m/s spring-back so the animation can be caught part-way.
    static void AddClickTest(string setup = "")
    {
        string name = "click" + setup;
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
            savedClickDuration = (float)button.GetProgramVariable("clickPressDuration");
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
            button.SetProgramVariable("clickPressDuration", 1f);
            GameObject.Find(ButtonPath).GetComponent<ClientSimUdonHelper>().Interact();
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
            button.SetProgramVariable("clickPressDuration", savedClickDuration);
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

    // Plan step 8.1: the manager keeps every player in a stable slot. Two remote players join and one leaves: the
    // slots still match GetPlayerCount, and a third player who joins takes the freed slot with a new generation.
    static void AddManagerSlotsTest()
    {
        const string name = "manager slots";
        int freedSlot = -1;
        int freedGeneration = 0;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            CheckTrackedPlayers(name + " at start");
            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote F");
            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote G");
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after two joins");
            VRCPlayerApi remoteF = FindPlayer("PushButtonTest Remote F");
            int tip = SlotOf(remoteF, 0);
            if (!Check(name + ": remote F has a slot", tip >= 0, "no slot")) { button = null; return; }
            freedSlot = tip / 4;
            freedGeneration = ((int[])ManagerOf(button).GetProgramVariable("slotGenerations"))[freedSlot];
            ClientSimMain.RemovePlayer(remoteF);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after a leave");
            VRCPlayerApi[] slots = (VRCPlayerApi[])ManagerOf(button).GetProgramVariable("slotPlayers");
            Check(name + " leaving player's slot freed", slots[freedSlot] == null, "slot " + freedSlot + " still taken");
            Check(name + " remote G kept a slot", SlotOf(FindPlayer("PushButtonTest Remote G"), 0) >= 0, "missing");
            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote H");
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after a rejoin");
            int tip = SlotOf(FindPlayer("PushButtonTest Remote H"), 0);
            int slot = tip < 0 ? -1 : tip / 4;
            int generation = slot >= 0 ? ((int[])ManagerOf(button).GetProgramVariable("slotGenerations"))[slot] : -1;
            Debug.Log("[PushButtonTest] " + name + ": remote H in slot " + slot + " generation " + generation + ", freed slot " + freedSlot + " was generation " + freedGeneration);
            // Remote F took the lowest free slot and remote G the next one, so F's slot is the lowest free one again.
            Check(name + " new player reuses the freed slot", slot == freedSlot, "slot " + slot + ", freed " + freedSlot);
            Check(name + " reused slot gets a new generation", generation == freedGeneration + 1, "generation " + generation + ", was " + freedGeneration);
            ClientSimMain.RemovePlayer(FindPlayer("PushButtonTest Remote H"));
            ClientSimMain.RemovePlayer(FindPlayer("PushButtonTest Remote G"));
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after all left");
        });
    }

    // Plan step 8.1: a button with no manager is inert. A PushButton created at runtime without one logs exactly one
    // warning, can't be clicked (not interactive, and a direct Interact or networked click does nothing), ignores the
    // debug fingertip and stays at rest.
    static void AddNoManagerTest()
    {
        const string name = "no manager";
        float travel = 0f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            savedButton = button;
            AbstractUdonProgramSource program = AssetDatabase.LoadAssetAtPath<AbstractUdonProgramSource>(ButtonProgramPath);
            if (!Check(name + ": load PushButton program", program != null && program.SerializedProgramAsset != null, "no program asset")) { button = null; return; }

            noManagerButton = new GameObject(NoManagerName);
            noManagerButton.SetActive(false);
            noManagerButton.transform.SetPositionAndRotation(go.transform.position + new Vector3(0.2f, 0f, 0f), go.transform.rotation);
            noManagerButton.transform.localScale = go.transform.lossyScale;
            noManagerButton.AddComponent<BoxCollider>();
            UdonBehaviour inert = noManagerButton.AddComponent<UdonBehaviour>();
            inert.AssignProgramAndVariables(program.SerializedProgramAsset, new UdonVariableTable());
            noManagerWarnings = 0;
            Application.logMessageReceived += CountNoManagerWarnings;
            noManagerButton.SetActive(true);
            inert.InitializeUdonContent();
            noManagerStartPos = noManagerButton.transform.position;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            UdonBehaviour inert = noManagerButton.GetComponent<UdonBehaviour>();
            button = inert;
            travel = (float)inert.GetProgramVariable("travelDistance");
            Check(name + ": manager unset", ManagerOf(inert) == null, "has a manager");
            Check(name + " interact disabled", inert.DisableInteractive && !inert.IsInteractive, "DisableInteractive " + inert.DisableInteractive);
            ClientSimUdonHelper helper = noManagerButton.GetComponent<ClientSimUdonHelper>();
            Check(name + " ClientSim can't interact", helper == null || !helper.CanInteract(), "CanInteract true");
            inert.Interact();
            inert.SendCustomEvent("NetPlayClickAnimation");
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            Check(name + " click ignored", !(bool)button.GetProgramVariable("clickActive") && (float)button.GetProgramVariable("pressAmount") == 0f,
                "clickActive " + button.GetProgramVariable("clickActive") + ", press " + button.GetProgramVariable("pressAmount"));
            PlaceDebugTip(travel * 1.5f, 0f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " debug fingertip ignored", (float)button.GetProgramVariable("localPress") == 0f && !(bool)button.GetProgramVariable("debugEngaged") && press == 0f,
                "localPress " + button.GetProgramVariable("localPress") + ", press " + press.ToString("F5"));
            Check(name + " stays at rest", Near(noManagerButton.transform.position, noManagerStartPos), "moved to " + noManagerButton.transform.position.ToString("F4"));
            Check(name + " still armed", (bool)button.GetProgramVariable("armed"), "disarmed");
        });
        AddStep(0.5f, () =>
        {
            Application.logMessageReceived -= CountNoManagerWarnings;
            Debug.Log("[PushButtonTest] " + name + ": " + noManagerWarnings + " warnings");
            if (noManagerButton != null) Check(name + " warns exactly once", noManagerWarnings == 1, noManagerWarnings + " warnings");
            RemoveDebugTip();
            if (noManagerButton != null) Object.Destroy(noManagerButton);
            noManagerButton = null;
            button = savedButton;
        });
    }

    static void CountNoManagerWarnings(string message, string stackTrace, LogType type)
    {
        if (type == LogType.Warning && message.Contains("[PushButton] " + NoManagerName + ": no manager")) noManagerWarnings++;
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

    // Plan step 8.2: the manager reads each player's bones once per frame, however many buttons are near. With the
    // local player in VR and the scene button 2 cm in front of their left index, it counts GetBonePosition calls per
    // frame, then adds 9 more buttons beside it (all in range and near the hands) and counts again: the counts are
    // equal, both hands plus two bones per index finger. With every button 1 m above the hands only the hands are read.
    static void AddBoneReadCountTest()
    {
        const string name = "bone reads";
        const int extraCount = 9;
        int oneButtonReads = -1;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            savedParentPos = button.transform.parent.position;
            SimulateVR(Networking.LocalPlayer, true);
            if (!Check(name + ": local avatar has index bones", MoveButtonOntoLeftIndex(Networking.LocalPlayer, -0.02f), "no finger bones")) button = null;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            StartCountingBoneReads();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            oneButtonReads = BoneReadsPerFrame(name + " 1 button");
            Check(name + " 1 button: hands and index fingers", oneButtonReads == 6, oneButtonReads + " reads per frame, expected 6");
            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            for (int k = 0; k < extraCount; k++)
            {
                GameObject clone = Object.Instantiate(button.gameObject, button.transform.parent);
                clone.name = "PushButtonTestExtra" + k;
                // A 5 x 2 grid 6 cm apart on the plate's plane; the scene button keeps cell 2, at its own rest pose.
                int cell = k < 2 ? k : k + 1;
                clone.transform.localPosition = restLocal + new Vector3((cell % 5 - 2) * 0.06f, (cell / 5) * 0.06f, 0f);
                extraButtons.Add(clone);
                // Instantiated UdonBehaviours aren't set up by ClientSim (VRChat's own instantiate does that).
                UdonBehaviour extra = clone.GetComponent<UdonBehaviour>();
                if (extra != null) extra.InitializeUdonContent();
            }
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            int active = true.Equals(button.GetProgramVariable("fingerCheckActive")) ? 1 : 0;
            foreach (GameObject clone in extraButtons)
            {
                UdonBehaviour extra = clone.GetComponent<UdonBehaviour>();
                if (extra != null && true.Equals(extra.GetProgramVariable("fingerCheckActive"))) active++;
            }
            Check(name + ": 10 buttons in range", active == extraCount + 1, active + " in range");
            StartCountingBoneReads();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            int tenButtonReads = BoneReadsPerFrame(name + " 10 buttons");
            Check(name + " 10 buttons read as much as 1", tenButtonReads == oneButtonReads && oneButtonReads > 0,
                tenButtonReads + " reads per frame, 1 button " + oneButtonReads);
            button.transform.parent.position += new Vector3(0f, 1f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            StartCountingBoneReads();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            int farReads = BoneReadsPerFrame(name + " hands far");
            Check(name + " hands far: hands only", farReads == 2, farReads + " reads per frame, expected 2");
            StopCountingBoneReads();
            foreach (GameObject clone in extraButtons) Object.Destroy(clone);
            extraButtons.Clear();
            button.transform.parent.position = savedParentPos;
            simulatedVRPlayers.Clear();
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            Check(name + " at rest at the end", (float)button.GetProgramVariable("pressAmount") == 0f
                && (float)button.GetProgramVariable("localPress") == 0f, "pressed");
        });
    }

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
