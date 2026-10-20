using System.Collections.Generic;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;

/// <summary>
/// Shared code for wiring buttons to the scene's <see cref="PushButtonManager"/>: used by the inspector's Fix button
/// and Tools/PushButton/Wire Up Scene. A button is wired only if its <c>manager</c> is exactly the first manager in
/// hierarchy order, in both the UdonSharp proxy and the UdonBehaviour's data.
/// </summary>
public static class PushButtonWiring
{
    public const string ManagerName = "PushButtonManager";
    const string UndoName = "Wire PushButtons to Manager";

    /// <summary>
    /// The first PushButtonManager in hierarchy order across the loaded scenes (scenes in load order, then root
    /// objects in scene order, then depth first by sibling index), inactive ones included, or null.
    /// </summary>
    public static PushButtonManager FirstManager()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                PushButtonManager manager = root.GetComponentInChildren<PushButtonManager>(true);
                if (manager != null) return manager;
            }
        }
        return null;
    }

    /// <summary>Every PushButton in the loaded scenes, inactive ones included, in hierarchy order.</summary>
    public static List<PushButton> AllButtons()
    {
        List<PushButton> buttons = new List<PushButton>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects()) buttons.AddRange(root.GetComponentsInChildren<PushButton>(true));
        }
        return buttons;
    }

    /// <summary>
    /// Why <paramref name="button"/> isn't wired to <paramref name="first"/> (the scene's first manager), or null when
    /// it is. Reads the manager from the proxy and from the UdonBehaviour's data (its live program variable in Play
    /// Mode).
    /// </summary>
    public static string Problem(PushButton button, PushButtonManager first)
    {
        UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(button);
        object udonManager = null;
        if (backing != null)
        {
            if (Application.isPlaying) udonManager = backing.GetProgramVariable("manager");
            else if (backing.publicVariables != null) backing.publicVariables.TryGetVariableValue("manager", out udonManager);
        }
        return Problem(button.manager, udonManager, first);
    }

    /// <summary>
    /// The decision behind <see cref="Problem(PushButton, PushButtonManager)"/>, on the two stored values: the proxy's
    /// manager and the UdonBehaviour data's (the manager's backing UdonBehaviour when wired).
    /// </summary>
    public static string Problem(PushButtonManager proxyManager, object udonManager, PushButtonManager first)
    {
        if (first == null) return "The scene has no PushButtonManager, so this button does nothing.";
        if (proxyManager == null) return "No manager is set, so this button does nothing.";
        if (proxyManager != first) return "The manager isn't the scene's first PushButtonManager (" + first.name + "), so this button does nothing.";
        UdonBehaviour expected = UdonSharpEditorUtility.GetBackingUdonBehaviour(first);
        if (!(udonManager is UdonBehaviour) || (UdonBehaviour)udonManager != expected)
            return "The manager in the UdonBehaviour's data doesn't match the one shown here.";
        return null;
    }

    /// <summary>
    /// Adds a PushButtonManager at the root of the active scene if the loaded scenes have none, wires every button
    /// that isn't wired to the first manager, and saves the scenes it changed. Doesn't ask. Returns the number of
    /// buttons wired. Does nothing in Play Mode or when there are no buttons.
    /// </summary>
    public static int Fix()
    {
        if (Application.isPlaying) return 0;
        List<PushButton> buttons = AllButtons();
        if (buttons.Count == 0) return 0;

        HashSet<Scene> changed = new HashSet<Scene>();
        PushButtonManager first = FirstManager();
        if (first == null)
        {
            GameObject managerObject = new GameObject(ManagerName);
            SceneManager.MoveGameObjectToScene(managerObject, SceneManager.GetActiveScene());
            Undo.RegisterCreatedObjectUndo(managerObject, UndoName);
            first = UdonSharpUndo.AddComponent<PushButtonManager>(managerObject);
            changed.Add(managerObject.scene);
            Debug.Log("[PushButton] added " + ManagerName + " to " + managerObject.scene.name);
        }

        int wired = 0;
        foreach (PushButton button in buttons)
        {
            if (Problem(button, first) == null) continue;
            SerializedObject serialized = new SerializedObject(button);
            serialized.FindProperty("manager").objectReferenceValue = first;
            serialized.ApplyModifiedProperties();
            UdonSharpEditorUtility.CopyProxyToUdon(button);
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(button);
            if (backing != null) EditorUtility.SetDirty(backing);
            changed.Add(button.gameObject.scene);
            wired++;
        }

        foreach (Scene scene in changed)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
        if (wired > 0) Debug.Log("[PushButton] wired " + wired + " button(s) to " + first.name);
        return wired;
    }
}
