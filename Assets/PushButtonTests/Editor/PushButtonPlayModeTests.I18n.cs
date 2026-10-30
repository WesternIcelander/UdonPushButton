using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDKBase;
using VRC.Udon;

// Plan step 8.12: the interaction text comes from I18n.
public static partial class PushButtonPlayModeTests
{
    const string PressEn = "Press";
    const string PressIs = "Ýta";
    const string TestKey = "test_key";
    const string TestKeyEn = "Test text";
    const string TestKeyIs = "Prófunartexti";
    const string MissingI18nKey = "no_such_key";
    static readonly List<string> i18nLogs = new List<string>();

    static void RecordI18nLogs(string message, string stackTrace, LogType type)
    {
        if (message.Contains("[PushButton]") || message.Contains("[I18n]")) i18nLogs.Add(message);
    }

    // The logged [PushButton] and [I18n] lines holding the text.
    static int I18nLogCount(string text)
    {
        return i18nLogs.FindAll(line => line.Contains(text)).Count;
    }

    // I18n.SetLanguage, by its name in the compiled program, which has no plain name because it takes a parameter.
    static void SetLanguage(UdonBehaviour i18n, string code)
    {
        i18n.SetProgramVariable("__0_rfc5646Code__param", code);
        i18n.SendCustomEvent("__0_SetLanguage");
    }

    // A clone of the scene button on its own root holder with these saved values, as a scene holds them, and set up
    // only now, with its holder active.
    static UdonBehaviour MakeI18nButton(string cloneName, UdonBehaviour i18n, string key)
    {
        UdonBehaviour udon = MakeUnstartedI18nButton(cloneName, i18n, key);
        StartI18nButton(udon);
        return udon;
    }

    static void StartI18nButton(UdonBehaviour udon)
    {
        udon.transform.parent.gameObject.SetActive(true);
        udon.InitializeUdonContent();
    }

    // The same, still off and never started, until StartI18nButton.
    static UdonBehaviour MakeUnstartedI18nButton(string cloneName, UdonBehaviour i18n, string key)
    {
        Transform parent = sceneButton.transform.parent;
        GameObject holder = new GameObject(cloneName + "Holder");
        holder.SetActive(false);
        holder.transform.SetPositionAndRotation(parent.position, parent.rotation);
        extraButtons.Add(holder);
        GameObject clone = Object.Instantiate(sceneButton.gameObject, holder.transform);
        clone.name = cloneName;
        clone.transform.localPosition = (Vector3)sceneButton.GetProgramVariable("restLocalPosition") + new Vector3(0.08f, 0f, 0f);
        clone.transform.localRotation = (Quaternion)sceneButton.GetProgramVariable("restLocalRotation");
        UdonBehaviour udon = clone.GetComponent<UdonBehaviour>();
        // A saved null would make Udon point the variable at this behaviour itself. UdonSharp saves an unset field by
        // leaving the variable out.
        if (i18n != null) SetSavedVariable(udon.publicVariables, "i18n", i18n);
        else udon.publicVariables.RemoveVariable("i18n");
        SetSavedVariable(udon.publicVariables, "interactText", key);
        return udon;
    }

    // A copy of the scene's I18n that hasn't started: nothing sets up an instantiated UdonBehaviour until
    // InitializeUdonContent, so it drops calls until then.
    static UdonBehaviour MakeI18nCopy(UdonBehaviour i18n, Transform parent)
    {
        GameObject copy = Object.Instantiate(i18n.gameObject, parent);
        copy.name = "PushButtonTest I18n copy";
        return copy.GetComponent<UdonBehaviour>();
    }

    static void CheckText(string name, UdonBehaviour pushButton, string expected)
    {
        Check(name, pushButton.InteractionText == expected, "got \"" + pushButton.InteractionText + "\", expected \"" + expected + "\"");
    }

    static bool Listening(UdonBehaviour pushButton)
    {
        return true.Equals(pushButton.GetProgramVariable("i18nListening"));
    }

    // How many times the I18n's listeners hold the button.
    static int ListenerCount(UdonBehaviour i18n, UdonBehaviour pushButton)
    {
        DataList listeners = (DataList)i18n.GetProgramVariable("_listeners");
        int count = 0;
        for (int i = 0; listeners != null && i < listeners.Count; i++)
        {
            if (listeners[i].Reference == (object)pushButton) count++;
        }
        return count;
    }

    static void AddI18nTest()
    {
        const string name = "i18n";
        UdonBehaviour i18n = null;
        UdonBehaviour first = null;
        UdonBehaviour second = null;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            sceneButton = button;
            i18n = button.GetProgramVariable("i18n") as UdonBehaviour;
            if (!Check(name + ": scene button has an I18n", i18n != null, "none")) return;
            Check(name + " scene button listens", Listening(button), "not listening");
            Check(name + " scene I18n is the main one", true.Equals(i18n.GetProgramVariable("mainI18n")) && i18n.transform.parent == null, "mainI18n " + i18n.GetProgramVariable("mainI18n"));
            i18nLogs.Clear();
            errorCount = 0;
            Application.logMessageReceived += RecordI18nLogs;
            Application.logMessageReceived += CountErrors;

            SetLanguage(i18n, "en");
            CheckText(name + " scene button: en", button, PressEn);
            SetLanguage(i18n, "is");
            CheckText(name + " scene button: is", button, PressIs);
            SetLanguage(i18n, "en");
            CheckText(name + " scene button: back to en", button, PressEn);
            first = MakeI18nButton("PushButtonTestI18nFirst", i18n, TestKey);
            second = MakeI18nButton("PushButtonTestI18nSecond", i18n, TestKey);
        });
        AddStep(0.5f, () =>
        {
            if (i18n == null) return;
            CheckText(name + " new button reads the current language", first, TestKeyEn);

            // Several buttons at once, and the scene's, with a language change leaving DisableInteractive alone.
            bool disabledBefore = first.DisableInteractive;
            SetLanguage(i18n, "is");
            CheckText(name + " several: first", first, TestKeyIs);
            CheckText(name + " several: second", second, TestKeyIs);
            CheckText(name + " several: scene button", button, PressIs);
            Check(name + " language change leaves DisableInteractive", first.DisableInteractive == disabledBefore && !first.DisableInteractive, "DisableInteractive " + first.DisableInteractive);

            // A button disabled during a change has the new text when it comes back, in each way of turning it off.
            foreach (string mode in DisableModes)
            {
                SetButtonOn(first, mode, false);
                SetLanguage(i18n, "en");
                SetButtonOn(first, mode, true);
                CheckText(name + " disabled (" + mode + ") during a change", first, TestKeyEn);
                SetButtonOn(first, mode, false);
                SetLanguage(i18n, "is");
                SetButtonOn(first, mode, true);
                CheckText(name + " disabled (" + mode + ") during a change back", first, TestKeyIs);
            }

            // 20 toggles leave one listener.
            for (int i = 0; i < 20; i++)
            {
                first.gameObject.SetActive(false);
                first.gameObject.SetActive(true);
            }
            Check(name + " 20 toggles leave one listener", ListenerCount(i18n, first) == 1, ListenerCount(i18n, first) + " listeners");
            SetLanguage(i18n, "en");
            CheckText(name + " follows after 20 toggles", first, TestKeyEn);

            // For a VR player with the trigger off, a change leaves it non-interactive, and turning the trigger on
            // shows the current language.
            SimulateVR(Networking.LocalPlayer, true);
            SetSetting("allowVRTrigger", false);
            Check(name + " VR, off: not interactive", first.DisableInteractive, "interactive");
            SetLanguage(i18n, "is");
            Check(name + " VR, off: a change leaves it non-interactive", first.DisableInteractive, "interactive");
            CheckText(name + " VR, off: a change doesn't set the text", first, TestKeyEn);
            SetSetting("allowVRTrigger", true);
            Check(name + " VR, on: interactive", !first.DisableInteractive, "not interactive");
            CheckText(name + " VR, on: current language", first, TestKeyIs);
            SimulateVR(Networking.LocalPlayer, false);
            SetSetting("allowVRTrigger", false);
            CheckText(name + " desktop again: current language", first, TestKeyIs);
            SetLanguage(i18n, "en");
            Check(name + " no errors so far", errorCount == 0, errorCount + " errors");
            DestroyClone(first);
            DestroyClone(second);
            first = null;
            second = null;
        });
        AddStep(0.1f, () =>
        {
            if (i18n == null) return;
            // I18n skips destroyed listeners.
            SetLanguage(i18n, "is");
            SetLanguage(i18n, "en");
            Check(name + " destroyed buttons are skipped", errorCount == 0, errorCount + " errors");
            first = MakeI18nButton("PushButtonTestI18nNone", null, TestKey);
            second = MakeI18nButton("PushButtonTestI18nEmpty", i18n, "");
        });
        AddStep(0.5f, () =>
        {
            if (i18n == null) return;
            Check(name + " no I18n: not listening", !Listening(first), "listening");
            CheckText(name + " no I18n: the text itself", first, TestKey);
            Check(name + " no I18n: nothing logged", I18nLogCount("PushButtonTestI18nNone") == 0, string.Join(", ", i18nLogs));
            Check(name + " empty text: listens", Listening(second), "not listening");
            CheckText(name + " empty text: stays empty", second, "");
            SetLanguage(i18n, "is");
            CheckText(name + " empty text: stays empty after a change", second, "");
            SetLanguage(i18n, "en");
            second.SetProgramVariable("interactText", null);
            second.SendCustomEvent("OnLanguageChange");
            Check(name + " null text: stays empty", string.IsNullOrEmpty(second.InteractionText), "got \"" + second.InteractionText + "\"");
            Check(name + " empty and null texts log no error", errorCount == 0 && I18nLogCount("No string found") == 0, string.Join(", ", i18nLogs));
            DestroyClone(first);
            DestroyClone(second);
            first = null;
            second = MakeI18nButton("PushButtonTestI18nMissing", i18n, MissingI18nKey);
        });
        AddStep(0.5f, () =>
        {
            if (i18n == null) return;
            // Expected: the run's Play Mode summary counts this one logged error.
            CheckText(name + " missing key: shows the key", second, MissingI18nKey);
            Check(name + " missing key: I18n logs its error", I18nLogCount("No string found for key " + MissingI18nKey) >= 1, "not logged");
            DestroyClone(second);
            second = null;
        });
        AddI18nEarlyEnableSteps(name, () => i18n);
        AddI18nSwitchedOffSteps(name + " inactive I18n", () => i18n, false);
        AddI18nSwitchedOffSteps(name + " disabled I18n", () => i18n, true);
        AddStep(0.1f, () =>
        {
            Application.logMessageReceived -= RecordI18nLogs;
            Application.logMessageReceived -= CountErrors;
            if (i18n != null) SetLanguage(i18n, "en");
        });
    }

    // A button enabled before its I18n has started: the first call is dropped, the button keeps retrying, and it ends
    // up with the translated text and follows later changes.
    static void AddI18nEarlyEnableSteps(string name, System.Func<UdonBehaviour> sceneI18n)
    {
        string prefix = name + " early enable";
        UdonBehaviour late = null;
        UdonBehaviour early = null;
        AddStep(0.1f, () =>
        {
            if (sceneI18n() == null) return;
            late = MakeI18nCopy(sceneI18n(), null);
            extraButtons.Add(late.gameObject);
            early = MakeI18nButton("PushButtonTestI18nEarly", late, TestKey);
        });
        AddStep(0.3f, () =>
        {
            if (late == null) return;
            Check(prefix + ": the early call is dropped", !Listening(early), "listening before the I18n started");
            CheckText(prefix + ": shows the key meanwhile", early, TestKey);
            Check(prefix + ": no warning, since the I18n will start", I18nLogCount("PushButtonTestI18nEarly") == 0, string.Join(", ", i18nLogs));
            late.InitializeUdonContent();
        });
        AddStep(0.3f, () =>
        {
            if (late == null) return;
            Check(prefix + ": listens once the I18n has started", Listening(early), "not listening");
            CheckText(prefix + ": translated", early, TestKeyEn);
            SetLanguage(late, "is");
            CheckText(prefix + ": follows later changes", early, TestKeyIs);
            DestroyClone(early);
            extraButtons.Remove(late.gameObject);
            Object.Destroy(late.gameObject);
            early = null;
            late = null;
        });
    }

    // A button whose I18n is switched off and has never started: one warning however often the button is enabled, and
    // interactText as the text. Once the I18n has started, the button's next enable starts listening.
    static void AddI18nSwitchedOffSteps(string name, System.Func<UdonBehaviour> sceneI18n, bool disableBehaviour)
    {
        UdonBehaviour off = null;
        UdonBehaviour pushButton = null;
        AddStep(0.1f, () =>
        {
            if (sceneI18n() == null) return;
            off = MakeI18nCopy(sceneI18n(), null);
            extraButtons.Add(off.gameObject);
            if (disableBehaviour) off.enabled = false;
            else off.gameObject.SetActive(false);
            pushButton = MakeI18nButton("PushButtonTestI18nOff", off, TestKey);
        });
        AddStep(0.3f, () =>
        {
            if (off == null) return;
            CheckText(name + ": shows interactText", pushButton, TestKey);
            Check(name + ": not listening", !Listening(pushButton), "listening");
            Check(name + ": one warning", I18nLogCount("PushButtonTestI18nOff") == 1, I18nLogCount("PushButtonTestI18nOff") + " warnings");
            pushButton.gameObject.SetActive(false);
            pushButton.gameObject.SetActive(true);
            Check(name + ": still one warning after an enable", I18nLogCount("PushButtonTestI18nOff") == 1, I18nLogCount("PushButtonTestI18nOff") + " warnings");
            if (disableBehaviour) off.enabled = true;
            else off.gameObject.SetActive(true);
            off.InitializeUdonContent();
        });
        AddStep(0.3f, () =>
        {
            if (off == null) return;
            pushButton.gameObject.SetActive(false);
            pushButton.gameObject.SetActive(true);
            Check(name + ": the next enable listens", Listening(pushButton), "not listening");
            CheckText(name + ": translated after that", pushButton, TestKeyEn);
            DestroyClone(pushButton);
            extraButtons.Remove(off.gameObject);
            Object.Destroy(off.gameObject);
            i18nLogs.Clear();
            off = null;
            pushButton = null;
        });
    }
}
