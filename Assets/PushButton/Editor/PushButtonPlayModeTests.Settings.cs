using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

// Plan step 8.8: the shared settings live on the manager, which pushes them to its buttons.
public static partial class PushButtonPlayModeTests
{
    // Non-default values for the shared settings, used by the settings tests.
    const float TestRadius = 0.02f;
    const float TestHaptic = 0.7f;
    const float TestClickDuration = 0.8f;
    static readonly string[] SettingNames = { "fingertipRadius", "useIndexFingers", "useMiddleFingers", "hapticStrength", "clickPressDuration" };
    static readonly object[] TestSettings = { TestRadius, false, true, TestHaptic, TestClickDuration };
    static object[] savedSettings;
    // Set by the first-tick watcher: whether the scene-load button's copies matched once it was first registered.
    static string firstRegisteredSettings;
    static UdonBehaviour loadButton;

    // Changing the settings on the manager and calling ApplySettings changes two buttons at once; a change without
    // ApplySettings reaches a button only once it's next enabled; a button that's active when its manager first
    // loads uses the manager's saved values from its first tick; the debug fingertip on the manager presses only the
    // button it's in front of.
    static void AddSettingsTest()
    {
        const string name = "settings";
        UdonBehaviour other = null;
        float travel = 0f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            sceneButton = button;
            travel = (float)button.GetProgramVariable("travelDistance");
            savedSettings = new object[SettingNames.Length];
            for (int i = 0; i < SettingNames.Length; i++) savedSettings[i] = Setting(SettingNames[i]);
            other = MakeHolderClone("PushButtonTestSettingsOther", "object", true);
        });
        AddStep(0.5f, () =>
        {
            if (button == null || other == null) return;
            CheckCopies(name + " defaults on the scene button", button, savedSettings);
            CheckCopies(name + " defaults on another button", other, savedSettings);
            for (int i = 0; i < SettingNames.Length; i++) SetSetting(SettingNames[i], TestSettings[i]);
            CheckCopies(name + " ApplySettings: scene button", button, TestSettings);
            CheckCopies(name + " ApplySettings: other button", other, TestSettings);
            button.SendCustomEvent("NetPlayClickAnimation");
            other.SendCustomEvent("NetPlayClickAnimation");
            clickTime = Time.time;
        });
        AddStep(0.4f, () =>
        {
            if (button == null || other == null) return;
            float expected = travel * (Time.time - clickTime) / TestClickDuration;
            foreach (UdonBehaviour b in new[] { button, other })
            {
                float press = (float)b.GetProgramVariable("pressAmount");
                Check(name + " ApplySettings: " + b.name + " clicks over the new clickPressDuration", press > 0f && Mathf.Abs(press - expected) < travel * 0.1f,
                    "press " + press.ToString("F5") + ", expected " + expected.ToString("F5"));
            }
        });
        // The new radius: the debug fingertip, placed for a half press with that radius, presses each button half-way.
        AddSettingsRadiusSteps(name + " ApplySettings: scene button", () => button, travel);
        AddSettingsRadiusSteps(name + " ApplySettings: other button", () => other, travel);

        // A change without ApplySettings: the other button picks it up when it's next enabled, the scene button
        // (still enabled) doesn't.
        AddStep(0.5f, () =>
        {
            if (button == null || other == null) return;
            UdonBehaviour manager = SceneManagerUdon();
            manager.SetProgramVariable("clickPressDuration", 0.3f);
            Check(name + " no ApplySettings: unchanged while enabled", (float)other.GetProgramVariable("clickPressDuration") == TestClickDuration,
                "got " + other.GetProgramVariable("clickPressDuration"));
            other.gameObject.SetActive(false);
            other.gameObject.SetActive(true);
            Check(name + " no ApplySettings: re-enabled button has it", (float)other.GetProgramVariable("clickPressDuration") == 0.3f,
                "got " + other.GetProgramVariable("clickPressDuration"));
            Check(name + " no ApplySettings: enabled button doesn't", (float)button.GetProgramVariable("clickPressDuration") == TestClickDuration,
                "got " + button.GetProgramVariable("clickPressDuration"));
        });

        // The debug fingertip on the manager presses only the button it's in front of.
        AddStep(0.5f, () =>
        {
            if (button == null || other == null) return;
            for (int i = 0; i < SettingNames.Length; i++) SetSetting(SettingNames[i], savedSettings[i]);
            CheckCopies(name + " restored: scene button", button, savedSettings);
            CheckCopies(name + " restored: other button", other, savedSettings);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (button == null || other == null) return;
            PlaceDebugTip(travel * 0.5f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (button == null || other == null) return;
            Check(name + " debug fingertip presses the button it's in", (float)button.GetProgramVariable("localPress") > 0f, "localPress " + button.GetProgramVariable("localPress"));
            Check(name + " debug fingertip leaves the other button alone", (float)other.GetProgramVariable("localPress") == 0f
                && !(bool)other.GetProgramVariable("debugEngaged") && (float)other.GetProgramVariable("pressAmount") == 0f,
                "localPress " + other.GetProgramVariable("localPress") + ", engaged " + other.GetProgramVariable("debugEngaged"));
            RemoveDebugTip();
            button = other;
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (sceneButton == null || other == null) return;
            PlaceDebugTip(travel * 0.5f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (sceneButton == null || other == null) return;
            Check(name + " debug fingertip presses the other button it's in", (float)other.GetProgramVariable("localPress") > 0f, "localPress " + other.GetProgramVariable("localPress"));
            Check(name + " debug fingertip leaves the scene button alone", (float)sceneButton.GetProgramVariable("localPress") == 0f
                && !(bool)sceneButton.GetProgramVariable("debugEngaged"), "localPress " + sceneButton.GetProgramVariable("localPress"));
            RemoveDebugTip();
            button = sceneButton;
            DestroyClone(other);
            other = null;
        });

        AddSceneLoadSettingsSteps(name + " scene load");
    }

    // Places the debug fingertip in front of the button, then pushes it to a half press as worked out with the new
    // radius: the button's local press is half the travel only if the button uses that radius.
    static void AddSettingsRadiusSteps(string name, System.Func<UdonBehaviour> target, float travel)
    {
        UdonBehaviour saved = null;
        AddStep(1f, () =>
        {
            if (button == null || target() == null) return;
            saved = button;
            button = target();
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (saved == null) return;
            PlaceDebugTip(travel * 0.5f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (saved == null) return;
            float local = (float)button.GetProgramVariable("localPress");
            Check(name + " presses with the new fingertipRadius", Mathf.Abs(local - travel * 0.5f) < 1e-4f,
                "localPress " + local.ToString("F5") + ", expected " + (travel * 0.5f).ToString("F5"));
            RemoveDebugTip();
            button = saved;
        });
    }

    // A new manager and button, as a scene load creates them: the button comes first in the hierarchy (so it's enabled
    // before the manager has started), and the manager's saved data holds non-default settings. The button has the
    // manager's values from the moment it's registered, so from its first tick, and clicks over the saved duration.
    static void AddSceneLoadSettingsSteps(string name)
    {
        GameObject holder = null;
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            UdonBehaviour manager = SceneManagerUdon();
            if (!Check(name + ": scene manager", manager != null, "not found")) return;
            holder = new GameObject("PushButtonTestSceneLoad");
            holder.SetActive(false);
            holder.transform.SetPositionAndRotation(button.transform.parent.position + button.transform.parent.right * 0.3f, button.transform.parent.rotation);
            extraButtons.Add(holder);
            GameObject buttonCopy = Object.Instantiate(button.gameObject, holder.transform);
            buttonCopy.transform.localPosition = (Vector3)button.GetProgramVariable("restLocalPosition");
            buttonCopy.transform.localRotation = (Quaternion)button.GetProgramVariable("restLocalRotation");
            GameObject managerCopy = Object.Instantiate(manager.gameObject, holder.transform);
            UdonBehaviour newButton = buttonCopy.GetComponent<UdonBehaviour>();
            UdonBehaviour newManager = managerCopy.GetComponent<UdonBehaviour>();
            for (int i = 0; i < SettingNames.Length; i++)
            {
                if (TestSettings[i] is float) SetSavedVariable(newManager.publicVariables, SettingNames[i], (float)TestSettings[i]);
                else SetSavedVariable(newManager.publicVariables, SettingNames[i], (bool)TestSettings[i]);
            }
            SetSavedVariable(newManager.publicVariables, "debugFingertip", (Transform)null);
            SetSavedVariable(newButton.publicVariables, "manager", newManager);
            loadButton = newButton;
            firstRegisteredSettings = null;
            EditorApplication.update += WatchFirstRegistration;
            holder.SetActive(true);
            // Instantiated UdonBehaviours aren't set up by ClientSim; the button first, as at scene load.
            newButton.InitializeUdonContent();
            newManager.InitializeUdonContent();
        });
        AddStep(0.5f, () =>
        {
            EditorApplication.update -= WatchFirstRegistration;
            if (holder == null || loadButton == null) return;
            Check(name + ": registered", true.Equals(loadButton.GetProgramVariable("registeredWithManager")), "not registered");
            Check(name + " uses the saved settings from its first tick", firstRegisteredSettings == "",
                firstRegisteredSettings == null ? "never registered" : firstRegisteredSettings);
            CheckCopies(name + " still has the saved settings", loadButton, TestSettings);
            loadButton.SendCustomEvent("NetPlayClickAnimation");
            clickTime = Time.time;
        });
        AddStep(0.4f, () =>
        {
            if (holder == null || loadButton == null) return;
            float travel = (float)loadButton.GetProgramVariable("travelDistance");
            float press = (float)loadButton.GetProgramVariable("pressAmount");
            float expected = travel * (Time.time - clickTime) / TestClickDuration;
            Check(name + " clicks over the saved clickPressDuration", press > 0f && Mathf.Abs(press - expected) < travel * 0.1f,
                "press " + press.ToString("F5") + ", expected " + expected.ToString("F5"));
        });
        AddStep(1.5f, () =>
        {
            if (holder != null)
            {
                extraButtons.Remove(holder);
                Object.Destroy(holder);
            }
            holder = null;
            loadButton = null;
        });
    }

    // Runs every editor frame until the scene-load button is registered, then records which of its copies didn't
    // match the saved settings (empty when all did).
    static void WatchFirstRegistration()
    {
        if (loadButton == null || !true.Equals(loadButton.GetProgramVariable("registeredWithManager"))) return;
        EditorApplication.update -= WatchFirstRegistration;
        firstRegisteredSettings = CopyMismatches(loadButton, TestSettings);
    }

    static void SetSavedVariable<T>(IUdonVariableTable table, string symbol, T value)
    {
        if (!table.TrySetVariableValue(symbol, value)) table.TryAddVariable(new UdonVariable<T>(symbol, value));
    }

    // The button's copies of the settings that differ from `expected` (in SettingNames order), or "" when none do.
    static string CopyMismatches(UdonBehaviour pushButton, object[] expected)
    {
        string mismatches = "";
        for (int i = 0; i < SettingNames.Length; i++)
        {
            object value = pushButton.GetProgramVariable(SettingNames[i]);
            if (!expected[i].Equals(value)) mismatches += SettingNames[i] + " " + value + " (expected " + expected[i] + ") ";
        }
        return mismatches;
    }

    static void CheckCopies(string name, UdonBehaviour pushButton, object[] expected)
    {
        string mismatches = CopyMismatches(pushButton, expected);
        Check(name, mismatches == "", mismatches);
    }
}
