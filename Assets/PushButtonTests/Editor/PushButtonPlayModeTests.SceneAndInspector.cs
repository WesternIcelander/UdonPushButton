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

// Plan phase 7: the scene wiring and the custom inspector.
public static partial class PushButtonPlayModeTests
{
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
            CheckDemoWiring(name, button, target);

            actionCount = 0;
            Application.logMessageReceived += CountActionLogs;
            TapWithHelper(go);
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

    // Plan step 9.3: the demo scene shows every feature. The button has its release and anyone events, the manager has
    // allowVRTrigger on, and the example enables the inactive PushButtonAssembly 2 (a button that starts disabled).
    // Then this resets those to the defaults the other tests expect: no release or anyone events, allowVRTrigger off,
    // and no toggle target, so clicks don't switch on a second button.
    static void CheckDemoWiring(string name, UdonBehaviour pushButton, UdonBehaviour example)
    {
        Check(name + " release event OnButtonReleased", (string)pushButton.GetProgramVariable("releaseEventName") == "OnButtonReleased",
            "got \"" + pushButton.GetProgramVariable("releaseEventName") + "\"");
        Check(name + " anyone events", (string)pushButton.GetProgramVariable("anyonePressedEventName") == "OnAnyonePressed"
            && (string)pushButton.GetProgramVariable("anyoneReleasedEventName") == "OnAnyoneReleased",
            "got \"" + pushButton.GetProgramVariable("anyonePressedEventName") + "\", \"" + pushButton.GetProgramVariable("anyoneReleasedEventName") + "\"");
        Check(name + " allowVRTrigger on", true.Equals(Setting("allowVRTrigger")), "off");

        GameObject second = example != null ? example.GetProgramVariable("toggleTarget") as GameObject : null;
        if (Check(name + " toggles " + AssemblyName + " 2", second != null && second.name == AssemblyName + " 2",
            second == null ? "no toggle target" : "got " + second.name))
        {
            Check(name + " second button starts disabled", !second.activeSelf, "active");
            Transform child = second.transform.Find("Button");
            UdonBehaviour secondButton = child != null ? child.GetComponent<UdonBehaviour>() : null;
            UdonBehaviour secondTarget = secondButton != null ? (UdonBehaviour)secondButton.GetProgramVariable("targetBehaviour") : null;
            Check(name + " second button wired to its own example and the manager", secondTarget != null && secondTarget.gameObject == second
                && ManagerOf(secondButton) == ManagerOf(pushButton), secondButton == null ? "no button" : "target " + (secondTarget != null ? secondTarget.gameObject.name : "none"));
        }

        pushButton.SetProgramVariable("releaseEventName", "");
        pushButton.SetProgramVariable("anyonePressedEventName", "");
        pushButton.SetProgramVariable("anyoneReleasedEventName", "");
        SetSetting("allowVRTrigger", false);
        if (example != null) example.SetProgramVariable("toggleTarget", null);
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

            // Plan step 8.8: PushButtonEditor draws every visible serialized field, and the shared settings aren't
            // among them any more (they're on the manager).
            List<string> shown = new List<string>();
            SerializedProperty property = new SerializedObject(proxy).GetIterator();
            for (bool enter = true; property.NextVisible(enter); enter = false) shown.Add(property.name);
            string[] moved = { "fingertipRadius", "useIndexFingers", "useMiddleFingers", "hapticStrength", "clickPressDuration", "debugFingertip" };
            List<string> stillShown = new List<string>(moved).FindAll(shown.Contains);
            Check(name + " moved settings not shown", stillShown.Count == 0, "still shows " + string.Join(", ", stillShown));
            Check(name + " interactText still shown", shown.Contains("interactText"), "fields: " + string.Join(", ", shown));
            // Plan step 8.12: i18n comes right after manager under Setup.
            Check(name + " i18n under manager", shown.IndexOf("manager") >= 0 && shown.IndexOf("i18n") == shown.IndexOf("manager") + 1
                && HeaderOf(typeof(PushButton), "i18n") == "Setup", "fields: " + string.Join(", ", shown) + ", header " + HeaderOf(typeof(PushButton), "i18n"));
            // Plan step 8.9: interactText heads the Action group, above targetBehaviour, and there's no Desktop header.
            int interactIndex = shown.IndexOf("interactText");
            Check(name + " interactText first under Action", interactIndex >= 0 && shown.IndexOf("targetBehaviour") == interactIndex + 1
                && HeaderOf(typeof(PushButton), "interactText") == "Action", "fields: " + string.Join(", ", shown) + ", header " + HeaderOf(typeof(PushButton), "interactText"));
            List<string> headers = new List<string>();
            foreach (System.Reflection.FieldInfo field in typeof(PushButton).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                foreach (HeaderAttribute header in field.GetCustomAttributes(typeof(HeaderAttribute), false)) headers.Add(header.header);
            }
            Check(name + " no Desktop header", !headers.Contains("Desktop"), "headers: " + string.Join(", ", headers));
            // Plan step 8.10: releaseEventName sits under eventName in the Action group (the scene's values are checked
            // by the scene wiring test, 9.3).
            Check(name + " releaseEventName under eventName", shown.IndexOf("releaseEventName") == shown.IndexOf("eventName") + 1
                && HeaderOf(typeof(PushButton), "releaseEventName") == "Action", "fields: " + string.Join(", ", shown));
            // Plan step 8.11: the anyone events follow releaseEventName in the Action group.
            Check(name + " anyone events under releaseEventName", shown.IndexOf("anyonePressedEventName") == shown.IndexOf("releaseEventName") + 1
                && shown.IndexOf("anyoneReleasedEventName") == shown.IndexOf("releaseEventName") + 2
                && HeaderOf(typeof(PushButton), "anyonePressedEventName") == "Action", "fields: " + string.Join(", ", shown));

            AbstractUdonProgramSource program = AssetDatabase.LoadAssetAtPath<AbstractUdonProgramSource>(ExampleProgramPath);
            if (!Check(name + ": load ButtonActionExample program", program != null, "no program asset")) return;
            GameObject holder = new GameObject("PushButtonTestInspectorTarget");
            holder.SetActive(false);
            UdonBehaviour example = holder.AddComponent<UdonBehaviour>();
            UdonBehaviour noProgram = holder.AddComponent<UdonBehaviour>();
            example.programSource = program;

            string[] events = PushButtonEditor.GetEventNames(example);
            string listed = events != null ? string.Join(", ", events) : "null";
            Check(name + " ButtonActionExample lists its four events", listed == "OnAnyonePressed, OnAnyoneReleased, OnButtonPressed, OnButtonReleased", "got " + listed);
            Check(name + " no target falls back to text", PushButtonEditor.GetEventNames(null) == null, "not null");
            Check(name + " non-UdonSharp program falls back to text", PushButtonEditor.GetEventNames(noProgram) == null, "not null");

            string[] buttonEvents = PushButtonEditor.GetEventNames(go.GetComponent<UdonBehaviour>());
            listed = buttonEvents != null ? string.Join(", ", buttonEvents) : "null";
            Check(name + " PushButton lists its own events", buttonEvents != null && System.Array.IndexOf(buttonEvents, "NetPlayClickAnimation") >= 0
                && System.Array.IndexOf(buttonEvents, "NetReleaseClick") >= 0, "got " + listed);
            Check(name + " built-in events left out", buttonEvents != null && System.Array.IndexOf(buttonEvents, "Interact") < 0
                && System.Array.IndexOf(buttonEvents, "InputUse") < 0 && System.Array.IndexOf(buttonEvents, "OnPlayerLeft") < 0
                && System.Array.IndexOf(buttonEvents, "OnPlayerJoined") < 0 && System.Array.IndexOf(buttonEvents, "GetType") < 0, "got " + listed);
            Object.DestroyImmediate(holder);
        });
        AddManagerWarningTest(name);
    }

    // The Header attribute on a public field of the type, or on the nearest field declared before it, or null.
    static string HeaderOf(System.Type type, string fieldName)
    {
        string header = null;
        foreach (System.Reflection.FieldInfo field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            foreach (HeaderAttribute attribute in field.GetCustomAttributes(typeof(HeaderAttribute), false)) header = attribute.header;
            if (field.Name == fieldName) return header;
        }
        return null;
    }

    // Plan step 8.6: the inspector's wiring warning shows for a null manager, another manager, and proxy/Udon data that
    // disagree, and not for the scene's first manager. Changes only the proxy's field (Udon doesn't read it) and puts it
    // back.
    static void AddManagerWarningTest(string inspector)
    {
        string name = inspector + " manager warning";
        AddStep(0.1f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            PushButton proxy = go != null ? go.GetComponent<PushButton>() : null;
            if (!Check(name + ": find PushButton component", proxy != null, "no PushButton on " + ButtonPath)) return;
            PushButtonManager first = PushButtonWiring.FirstManager();
            if (!Check(name + ": scene has a manager", first != null && first.gameObject.name == ManagerName, "got " + (first != null ? first.name : "null"))) return;
            UdonBehaviour firstBacking = UdonSharpEditorUtility.GetBackingUdonBehaviour(first);
            Object[] targets = { proxy };

            string warning = PushButtonEditor.WiringWarning(targets);
            Check(name + " none for the scene's manager", warning == null, "got \"" + warning + "\"");
            Check(name + " decision: right manager", PushButtonWiring.Problem(first, firstBacking, first) == null, "warned");

            PushButtonManager saved = proxy.manager;
            GameObject other = new GameObject("PushButtonTest OtherManager");
            other.SetActive(false);
            PushButtonManager otherManager = other.AddComponent<PushButtonManager>();
            Check(name + ": first manager unchanged by a later one", PushButtonWiring.FirstManager() == first, "got " + PushButtonWiring.FirstManager());
            try
            {
                proxy.manager = null;
                warning = PushButtonEditor.WiringWarning(targets);
                Check(name + " shows for a null manager", warning != null, "no warning");
                proxy.manager = otherManager;
                warning = PushButtonEditor.WiringWarning(targets);
                Check(name + " shows for another manager", warning != null, "no warning");
            }
            finally
            {
                proxy.manager = saved;
                Object.DestroyImmediate(other);
            }
            Check(name + " gone once restored", PushButtonEditor.WiringWarning(targets) == null, "still warns");

            Check(name + " decision: null manager", PushButtonWiring.Problem(null, firstBacking, first) != null, "no warning");
            Check(name + " decision: Udon data null", PushButtonWiring.Problem(first, null, first) != null, "no warning");
            Check(name + " decision: Udon data disagrees", PushButtonWiring.Problem(first, go.GetComponent<UdonBehaviour>(), first) != null, "no warning");
            Check(name + " decision: no manager in the scene", PushButtonWiring.Problem(null, null, null) != null, "no warning");
        });
    }
}
