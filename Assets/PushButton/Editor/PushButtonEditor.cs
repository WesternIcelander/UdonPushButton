using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

/// <summary>
/// Inspector for <see cref="PushButton"/>: the default fields, with <c>eventName</c> as a dropdown of the target's
/// public events when the target is an UdonSharpBehaviour. Graph programs (and no target) keep a free-text field.
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
        while (property.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (property.name == "m_Script") continue;
            if (property.name == "eventName") DrawEventName(property);
            else EditorGUILayout.PropertyField(property, true);
        }
        serializedObject.ApplyModifiedProperties();
    }

    void DrawEventName(SerializedProperty eventName)
    {
        SerializedProperty targetProperty = serializedObject.FindProperty("targetBehaviour");
        string[] events = targetProperty.hasMultipleDifferentValues || eventName.hasMultipleDifferentValues
            ? null
            : GetEventNames(targetProperty.objectReferenceValue as UdonBehaviour);
        if (events == null || events.Length == 0)
        {
            EditorGUILayout.PropertyField(eventName);
            if (events != null) EditorGUILayout.HelpBox("The target has no public parameterless methods to call.", MessageType.Warning);
            return;
        }

        // An event name the target doesn't have stays selectable, marked, so it isn't silently replaced.
        string current = eventName.stringValue;
        List<string> options = new List<string>(events);
        int index = options.IndexOf(current);
        string[] labels = events;
        if (index < 0)
        {
            options.Insert(0, current);
            labels = new string[options.Count];
            labels[0] = string.IsNullOrEmpty(current) ? "(none)" : current + " (not found)";
            events.CopyTo(labels, 1);
            index = 0;
        }

        GUIContent label = new GUIContent(eventName.displayName, eventName.tooltip);
        Rect rect = EditorGUILayout.GetControlRect();
        label = EditorGUI.BeginProperty(rect, label, eventName);
        int chosen = EditorGUI.Popup(rect, label, index, Array.ConvertAll(labels, l => new GUIContent(l)));
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
