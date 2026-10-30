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
public static partial class PushButtonPlayModeTests
{
    const string ArmedKey = "PushButtonTest.armed";
    const string AssemblyName = "PushButtonAssembly";
    const string ButtonPath = AssemblyName + "/Button";
    const string ManagerName = "PushButtonManager";
    const string NoManagerName = "PushButtonTestNoManager";
    const string ButtonProgramPath = "Packages/io.siggi.pushbutton/Runtime/PushButton.asset";
    const string ExampleProgramPath = "Packages/io.siggi.pushbutton/Example/ButtonActionExample.asset";
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
    static string savedReleaseEventName;
    static int actionCount;
    // Release action logs (plan step 8.10), and the order of press and release logs since SetUpActionTarget.
    static int releaseCount;
    static readonly List<string> actionOrder = new List<string>();
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
    // Network events sent while the recording hook is installed (plan step 8.9), as "<target> <event>".
    static readonly List<string> sentNetworkEvents = new List<string>();
    static bool networkEventsRecorded;
    static System.Delegate networkEventRecorder;
    static readonly List<GameObject> extraButtons = new List<GameObject>();
    // The enable/disable test (plan step 8.4): the three ways to turn a button off, the scene button while the test
    // swaps `button`, the remote VR player whose finger presses, run-once code for inside the next action (called from
    // its log line, so it runs inside the manager's tick), and the errors logged meanwhile.
    static readonly string[] DisableModes = { "object", "parent", "behaviour" };
    const string TogglerName = "PushButtonTest Toggler";
    const float Settle = 0.25f;
    static UdonBehaviour sceneButton;
    static System.Action onAction;
    static int errorCount;

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
    // plan step 8.1) through the inspector's shared PushButtonWiring.Fix (plan step 8.6), and saves the scene. Safe to
    // run again.
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

        SerializedObject serialized = new SerializedObject(pushButton);
        serialized.FindProperty("localPressAxis").vector3Value = new Vector3(0f, 0f, -1f);
        serialized.FindProperty("travelDistance").floatValue = 0.019f;
        serialized.FindProperty("targetBehaviour").objectReferenceValue = UdonSharpEditorUtility.GetBackingUdonBehaviour(example);
        serialized.FindProperty("eventName").stringValue = "OnButtonPressed";
        serialized.ApplyModifiedProperties();
        UdonSharpEditorUtility.CopyProxyToUdon(pushButton);

        EditorSceneManager.MarkSceneDirty(go.scene);
        PushButtonWiring.Fix();
        EditorSceneManager.SaveScene(go.scene);
        Debug.Log("[PushButtonTest] wired up " + ButtonPath + " and saved " + go.scene.name);
    }

    static void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingPlayMode)
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= CountActionLogs;
            Application.logMessageReceived -= CountNoManagerWarnings;
            Application.logMessageReceived -= CountErrors;
            Application.logMessageReceived -= RecordI18nLogs;
            onAction = null;
            VRCPlayerApi._PlayHapticEventInHand -= RecordHaptic;
            RestoreIsUserInVR();
            StopCountingBoneReads();
            StopRecordingNetworkEvents();
            EditorApplication.update -= WatchFirstRegistration;
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
        AddVRTriggerTest();
        AddHoldTest();
        AddAnyoneEventsTest();
        AddHandEarlyOutTest();
        AddBoneReadCountTest();
        AddManagerTickTest();
        AddEnableDisableTest();
        AddTransformWriteTest();
        AddSettingsTest();
        AddI18nTest();
        AddSetInteractTextTest();
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
