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
}
