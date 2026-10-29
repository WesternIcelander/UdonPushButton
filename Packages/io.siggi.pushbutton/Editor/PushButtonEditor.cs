using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

/// <summary>
/// Inspector for <see cref="PushButton"/>: the default fields, with <c>eventName</c> and the optional event names
/// (<c>releaseEventName</c>, <c>anyonePressedEventName</c>, <c>anyoneReleasedEventName</c>) as dropdowns of the
/// target's public events when the target is an UdonSharpBehaviour (the optional ones with a "(none)" choice). Graph programs (and no target) keep a free-text field.
/// Below <c>manager</c>, a warning box shows when a button isn't wired to the scene's first manager, with a Fix button
/// that runs <see cref="PushButtonWiring.Fix"/>.
/// </summary>
[CustomEditor(typeof(PushButton))]
public class PushButtonEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;

        serializedObject.Update();
        SerializedProperty property = serializedObject.GetIterator();
        bool enterChildren = true;
        bool fix = false;
        while (property.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (property.name == "m_Script") continue;
            if (property.name == "eventName") DrawEventName(property, false);
            else if (property.name == "releaseEventName" || property.name == "anyonePressedEventName"
                || property.name == "anyoneReleasedEventName") DrawEventName(property, true);
            else EditorGUILayout.PropertyField(property, true);
            if (property.name == "manager") fix = DrawManagerWarning();
        }
        serializedObject.ApplyModifiedProperties();
        // After ApplyModifiedProperties, so this inspector's own values don't overwrite the fix.
        if (fix)
        {
            PushButtonWiring.Fix();
            GUIUtility.ExitGUI();
        }
    }

    /// <summary>The warning for the first of <paramref name="buttons"/> that isn't wired, or null when all are.</summary>
    internal static string WiringWarning(UnityEngine.Object[] buttons)
    {
        PushButtonManager first = PushButtonWiring.FirstManager();
        foreach (UnityEngine.Object o in buttons)
        {
            PushButton button = o as PushButton;
            string problem = button != null ? PushButtonWiring.Problem(button, first) : null;
            if (problem != null) return problem;
        }
        return null;
    }

    // Draws the warning box and its Fix button (off in Play Mode) when needed. Returns whether Fix was clicked.
    bool DrawManagerWarning()
    {
        string warning = WiringWarning(targets);
        if (warning == null) return false;
        EditorGUILayout.HelpBox(warning, MessageType.Warning);
        using (new EditorGUI.DisabledScope(Application.isPlaying))
        {
            return GUILayout.Button(new GUIContent("Fix",
                "Adds a PushButtonManager if the scene has none, wires every button to the first one, and saves the scene."));
        }
    }

    // An optional event (the release and anyone events) always offers "(none)", which leaves it empty.
    void DrawEventName(SerializedProperty eventName, bool optional)
    {
        SerializedProperty targetProperty = serializedObject.FindProperty("targetBehaviour");
        string[] events = targetProperty.hasMultipleDifferentValues || eventName.hasMultipleDifferentValues
            ? null
            : GetEventNames(targetProperty.objectReferenceValue as UdonBehaviour);
        if (events == null || events.Length == 0)
        {
            EditorGUILayout.PropertyField(eventName);
            if (events != null && !optional) EditorGUILayout.HelpBox("The target has no public parameterless methods to call.", MessageType.Warning);
            return;
        }

        List<string> options = new List<string>(events);
        List<string> labels = new List<string>(events);
        if (optional)
        {
            options.Insert(0, "");
            labels.Insert(0, "(none)");
        }
        // An event name the target doesn't have stays selectable, marked, so it isn't silently replaced.
        string current = eventName.stringValue;
        int index = options.IndexOf(current);
        if (index < 0)
        {
            options.Insert(0, current);
            labels.Insert(0, string.IsNullOrEmpty(current) ? "(none)" : current + " (not found)");
            index = 0;
        }

        GUIContent label = new GUIContent(eventName.displayName, eventName.tooltip);
        Rect rect = EditorGUILayout.GetControlRect();
        label = EditorGUI.BeginProperty(rect, label, eventName);
        int chosen = EditorGUI.Popup(rect, label, index, labels.ConvertAll(l => new GUIContent(l)).ToArray());
        EditorGUI.EndProperty();
        if (chosen != index) eventName.stringValue = options[chosen];
    }

    /// <summary>
    /// The events <c>SendCustomEvent</c> can call on an UdonSharpBehaviour: its public, parameterless instance methods
    /// (inherited ones included), minus overrides of UdonSharpBehaviour's own built-in events. Null for a missing
    /// target or a Graph program, whose events can't be listed from C#.
    /// </summary>
    internal static string[] GetEventNames(UdonBehaviour behaviour)
    {
        if (behaviour == null) return null;
        Type type = UdonSharpEditorUtility.GetUdonSharpBehaviourType(behaviour);
        if (type == null) return null;

        List<string> names = new List<string>();
        for (Type t = type; t != null && t != typeof(UdonSharpBehaviour); t = t.BaseType)
        {
            foreach (MethodInfo method in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName || method.IsGenericMethodDefinition || method.GetParameters().Length > 0) continue;
                Type baseDeclaringType = method.GetBaseDefinition().DeclaringType;
                if (baseDeclaringType != null && baseDeclaringType.IsAssignableFrom(typeof(UdonSharpBehaviour))) continue;
                if (!names.Contains(method.Name)) names.Add(method.Name);
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }
}
