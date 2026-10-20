using System;
using System.Collections.Generic;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDKBase.Editor.BuildPipeline;
using VRC.Udon;
using Object = UnityEngine.Object;

/// <summary>
/// Edit Mode checks for <see cref="PushButtonBuildCheck"/> (plan step 8.7), run unattended through the editor trigger:
/// queue `save`, then `menu Tools/PushButton/Run Build Check Tests`, and read the Editor log. Each case builds its own
/// scene in a temporary scene file, calls the build callback with <see cref="PushButtonBuildCheck.testAnswer"/>
/// answering, and checks what changed. The open scenes are restored afterwards. Logs
/// `[PushButtonBuildCheckTest] FAIL name: reason` as errors and one `[PushButtonBuildCheckTest] done: N passed, M failed`
/// line.
/// </summary>
public static class PushButtonBuildCheckTests
{
    const string Prefix = "[PushButtonBuildCheckTest] ";
    const string ScenePath = "Assets/PushButtonBuildCheckTest.unity";

    static int passed;
    static int failed;
    static int asked;
    static readonly List<string> warnings = new List<string>();

    [MenuItem("Tools/PushButton/Run Build Check Tests")]
    static void RunAll()
    {
        if (Application.isPlaying)
        {
            Debug.LogError(Prefix + "run in Edit Mode");
            return;
        }
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (!SceneManager.GetSceneAt(i).isDirty) continue;
            Debug.LogError(Prefix + "save the open scenes first; " + SceneManager.GetSceneAt(i).name + " has unsaved changes");
            return;
        }

        SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
        passed = 0;
        failed = 0;
        Application.logMessageReceived += RecordWarning;
        try
        {
            NoButtons();
            NoManager(true);
            NoManager(false);
            CorrectlyWired();
            foreach (string kind in new[] { "null manager", "second manager", "proxy set, Udon data null", "Udon data on the second manager" })
            {
                Unwired(kind, false);
                Unwired(kind, true);
            }
            TwoManagers();
        }
        catch (Exception e)
        {
            failed++;
            Debug.LogException(e);
        }
        finally
        {
            Application.logMessageReceived -= RecordWarning;
            PushButtonBuildCheck.testAnswer = null;
            EditorSceneManager.RestoreSceneManagerSetup(setup);
            AssetDatabase.DeleteAsset(ScenePath);
        }
        Debug.Log(Prefix + "done: " + passed + " passed, " + failed + " failed");
    }

    // No buttons: asks nothing, even without a manager.
    static void NoButtons()
    {
        const string name = "no buttons";
        NewScene();
        new GameObject("Something else");
        Save();
        RunCheck(name, true);
        Check(name + " asks nothing", asked == 0, "asked " + asked);
        Check(name + " adds no manager", PushButtonWiring.AllManagers().Count == 0, "added one");
    }

    // No manager: one question. Yes adds a manager at the scene root and wires every button, inactive ones included,
    // and saves; No changes nothing.
    static void NoManager(bool yes)
    {
        string name = "no manager + " + (yes ? "yes" : "no");
        NewScene();
        List<PushButton> buttons = AddButtonsIncludingInactive();
        Save();
        RunCheck(name, yes);
        Check(name + " asks once", asked == 1, "asked " + asked);
        List<PushButtonManager> managers = PushButtonWiring.AllManagers();
        if (!yes)
        {
            Check(name + " adds no manager", managers.Count == 0, managers.Count + " managers");
            foreach (PushButton button in buttons)
            {
                Check(name + " " + button.name + " proxy untouched", button.manager == null, "got " + button.manager);
                Check(name + " " + button.name + " Udon data untouched", UdonManager(button) == null, "got " + UdonManager(button));
            }
            return;
        }
        if (!Check(name + " adds one manager", managers.Count == 1, managers.Count + " managers")) return;
        PushButtonManager manager = managers[0];
        Check(name + " manager at the scene root", manager.transform.parent == null && manager.name == PushButtonWiring.ManagerName,
            "got " + manager.name + (manager.transform.parent != null ? " under " + manager.transform.parent.name : ""));
        CheckAllWired(name, buttons, manager);
        Check(name + " scene saved", !SceneManager.GetActiveScene().isDirty, "unsaved changes");
        RunCheck(name + ", run again", false);
        Check(name + ", run again asks nothing", asked == 0, "asked " + asked);
    }

    // A correctly wired scene: asks nothing.
    static void CorrectlyWired()
    {
        const string name = "correctly wired";
        NewScene();
        PushButtonManager manager = AddManager("Manager", null);
        List<PushButton> buttons = AddButtonsIncludingInactive();
        foreach (PushButton button in buttons) Wire(button, manager);
        Save();
        RunCheck(name, false);
        Check(name + " asks nothing", asked == 0, "asked " + asked);
        CheckAllWired(name, buttons, manager);
    }

    // One wired button and one broken one (inactive). Each kind counts as unwired: one question; Yes wires the broken
    // button to the first manager and saves, No leaves both stored values as they were.
    static void Unwired(string kind, bool yes)
    {
        string name = kind + " + " + (yes ? "yes" : "no");
        NewScene();
        PushButtonManager first = AddManager("First manager", null);
        PushButtonManager second = AddManager("Second manager", null);
        PushButton good = AddButton("Good", null);
        PushButton bad = AddButton("Bad", null);
        bad.gameObject.SetActive(false);
        Wire(good, first);
        if (kind == "second manager" || kind == "Udon data on the second manager") Wire(bad, second);
        Save();
        // Changed after saving, so only the proxy holds it.
        if (kind == "proxy set, Udon data null" || kind == "Udon data on the second manager") bad.manager = first;
        if (!Check(name + ": broken button counts as unwired", PushButtonWiring.Problem(bad, first) != null, "counted as wired")) return;
        PushButtonManager proxyBefore = bad.manager;
        object udonBefore = UdonManager(bad);

        RunCheck(name, yes);
        Check(name + " asks once", asked == 1, "asked " + asked);
        Check(name + " good button stays wired", PushButtonWiring.Problem(good, first) == null, PushButtonWiring.Problem(good, first));
        Check(name + " adds no manager", PushButtonWiring.AllManagers().Count == 2, PushButtonWiring.AllManagers().Count + " managers");
        if (yes)
        {
            CheckAllWired(name, new List<PushButton> { good, bad }, first);
            Check(name + " scene saved", !SceneManager.GetActiveScene().isDirty, "unsaved changes");
        }
        else
        {
            Check(name + " proxy untouched", bad.manager == proxyBefore, "got " + bad.manager);
            Check(name + " Udon data untouched", Equals(UdonManager(bad), udonBefore), "got " + UdonManager(bad));
        }
    }

    // Two managers: the warning names the one used (the first in hierarchy order, here nested under an earlier root
    // though created last) and the ignored one, on every check, and buttons are wired to the first.
    static void TwoManagers()
    {
        const string name = "two managers";
        NewScene();
        GameObject earlierRoot = new GameObject("Earlier root");
        PushButtonManager ignored = AddManager("Root manager", null);
        PushButton button = AddButton("Button", null);
        PushButtonManager used = AddManager("Nested manager", earlierRoot.transform);
        Save();
        Check(name + " first manager is the nested one", PushButtonWiring.FirstManager() == used, "got " + PushButtonWiring.FirstManager());

        RunCheck(name, true);
        Check(name + " asks once", asked == 1, "asked " + asked);
        CheckWarning(name, used, ignored);
        CheckAllWired(name, new List<PushButton> { button }, used);

        RunCheck(name + ", wired", false);
        Check(name + ", wired asks nothing", asked == 0, "asked " + asked);
        CheckWarning(name + ", wired", used, ignored);
    }

    static void CheckWarning(string name, PushButtonManager used, PushButtonManager ignored)
    {
        string warning = warnings.Find(w => w.Contains("2 PushButtonManagers"));
        Check(name + " logs the warning", warning != null, "no warning; got " + warnings.Count + " others");
        if (warning == null) return;
        Check(name + " warning names the one used", warning.Contains("Using the first, Earlier root/" + used.name + ";"), warning);
        Check(name + " warning names the ignored one", warning.Contains("ignoring " + ignored.name + "."), warning);
    }

    // Calls the build callback as a world build would, answering with yes or no, and checks it lets the build go on.
    static void RunCheck(string name, bool yes)
    {
        asked = 0;
        warnings.Clear();
        PushButtonBuildCheck.testAnswer = question =>
        {
            asked++;
            return yes;
        };
        bool goOn = new PushButtonBuildCheck().OnBuildRequested(VRCSDKRequestedBuildType.Scene);
        PushButtonBuildCheck.testAnswer = null;
        Check(name + " lets the build continue", goOn, "returned false");
    }

    static void CheckAllWired(string name, List<PushButton> buttons, PushButtonManager manager)
    {
        UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(manager);
        foreach (PushButton button in buttons)
        {
            string problem = PushButtonWiring.Problem(button, manager);
            Check(name + " " + button.name + " wired", problem == null, problem);
            Check(name + " " + button.name + " proxy and Udon data on " + manager.name, button.manager == manager && Equals(UdonManager(button), backing),
                "proxy " + button.manager + ", Udon data " + UdonManager(button));
        }
    }

    // An active button, an inactive one, and one under an inactive parent.
    static List<PushButton> AddButtonsIncludingInactive()
    {
        PushButton active = AddButton("Active", null);
        PushButton inactive = AddButton("Inactive", null);
        inactive.gameObject.SetActive(false);
        GameObject parent = new GameObject("Inactive parent");
        PushButton nested = AddButton("Under inactive parent", parent.transform);
        parent.SetActive(false);
        return new List<PushButton> { active, inactive, nested };
    }

    static PushButton AddButton(string name, Transform parent)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        return UdonSharpUndo.AddComponent<PushButton>(go);
    }

    static PushButtonManager AddManager(string name, Transform parent)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return UdonSharpUndo.AddComponent<PushButtonManager>(go);
    }

    static void Wire(PushButton button, PushButtonManager manager)
    {
        SerializedObject serialized = new SerializedObject(button);
        serialized.FindProperty("manager").objectReferenceValue = manager;
        serialized.ApplyModifiedProperties();
        UdonSharpEditorUtility.CopyProxyToUdon(button);
    }

    static object UdonManager(PushButton button)
    {
        UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(button);
        object value = null;
        if (backing != null && backing.publicVariables != null) backing.publicVariables.TryGetVariableValue("manager", out value);
        return value is Object o && o == null ? null : value;
    }

    static void NewScene()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    static void Save()
    {
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath);
    }

    static void RecordWarning(string message, string stackTrace, LogType type)
    {
        if (type == LogType.Warning && message.StartsWith("[PushButton] ")) warnings.Add(message);
    }

    static bool Check(string name, bool ok, string reason)
    {
        if (ok) passed++;
        else
        {
            failed++;
            Debug.LogError(Prefix + "FAIL " + name + ": " + reason);
        }
        return ok;
    }
}
