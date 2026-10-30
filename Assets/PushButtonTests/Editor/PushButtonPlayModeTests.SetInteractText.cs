using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// Plan step 8.13: a method to change the interaction text.
public static partial class PushButtonPlayModeTests
{
    const string CallerProgramPath = "Assets/PushButtonTests/PushButtonTestCaller.asset";
    const string OneArgument = "CallOneArgument";
    const string LiteralArgument = "CallLiteral";
    const string KeyArgument = "CallKey";
    const string MissingKeyLog = "No string found";
    static UdonBehaviour textCaller;

    // Calls SetInteractText through the compiled caller, which is how another U# behaviour reaches it.
    static void CallSetInteractText(UdonBehaviour pushButton, string text, string call)
    {
        textCaller.SetProgramVariable("button", pushButton);
        textCaller.SetProgramVariable("text", text);
        textCaller.SendCustomEvent(call);
    }

    // Calls it and checks the text, and that the call left DisableInteractive alone.
    static void CheckSetText(string name, UdonBehaviour pushButton, string text, string call, string expected)
    {
        bool disabledBefore = pushButton.DisableInteractive;
        CallSetInteractText(pushButton, text, call);
        CheckText(name, pushButton, expected);
        Check(name + ": leaves DisableInteractive", pushButton.DisableInteractive == disabledBefore, "DisableInteractive " + pushButton.DisableInteractive);
    }

    static void AddSetInteractTextTest()
    {
        const string name = "set interact text";
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
            AbstractUdonProgramSource program = AssetDatabase.LoadAssetAtPath<AbstractUdonProgramSource>(CallerProgramPath);
            if (!Check(name + ": load caller program", program != null && program.SerializedProgramAsset != null, "no program asset"))
            {
                i18n = null;
                return;
            }
            GameObject caller = new GameObject("PushButtonTestCaller");
            caller.SetActive(false);
            extraButtons.Add(caller);
            textCaller = caller.AddComponent<UdonBehaviour>();
            textCaller.AssignProgramAndVariables(program.SerializedProgramAsset, new UdonVariableTable());
            caller.SetActive(true);
            textCaller.InitializeUdonContent();

            i18nLogs.Clear();
            errorCount = 0;
            Application.logMessageReceived += RecordI18nLogs;
            Application.logMessageReceived += CountErrors;
            SetLanguage(i18n, "en");
            first = MakeI18nButton("PushButtonTestSetTextFirst", i18n, TestKey);
            second = MakeI18nButton("PushButtonTestSetTextSecond", i18n, TestKey);
        });
        AddStep(0.5f, () =>
        {
            if (i18n == null) return;
            StartRecordingNetworkEvents();

            // Without and with the second argument: the literal text, through language changes. Reading it from I18n
            // would log a missing key. The other button isn't touched.
            foreach (string call in new[] { OneArgument, LiteralArgument })
            {
                string literal = "Literal " + call;
                CheckSetText(name + " " + call + ": shows the text", first, literal, call, literal);
                SetLanguage(i18n, "is");
                CheckText(name + " " + call + ": stays through a change", first, literal);
                CheckText(name + " " + call + ": other button follows the change", second, TestKeyIs);
                SetLanguage(i18n, "en");
                CheckText(name + " " + call + ": stays through a change back", first, literal);
                CheckText(name + " " + call + ": other button keeps its text", second, TestKeyEn);
            }
            Check(name + " literal: no missing key logged", I18nLogCount(MissingKeyLog) == 0, string.Join(", ", i18nLogs));

            // A key: the current language's string, following changes.
            CheckSetText(name + " key: current language", first, "Press", KeyArgument, PressEn);
            CheckText(name + " key: other button keeps its text", second, TestKeyEn);
            SetLanguage(i18n, "is");
            CheckText(name + " key: follows a change", first, PressIs);
            SetLanguage(i18n, "en");
            CheckText(name + " key: follows a change back", first, PressEn);

            // From a key to a literal and back, including the same text as each. Still listening while literal, so a
            // later key follows the language straight away.
            CheckSetText(name + " key to literal", first, "Plain", OneArgument, "Plain");
            SetLanguage(i18n, "is");
            CheckText(name + " key to literal: stays", first, "Plain");
            Check(name + " literal: still listening", Listening(first), "not listening");
            CheckSetText(name + " literal to key: current language straight away", first, TestKey, KeyArgument, TestKeyIs);
            CheckSetText(name + " same text as a literal", first, TestKey, OneArgument, TestKey);
            CheckSetText(name + " same text as a key", first, TestKey, KeyArgument, TestKeyIs);
            SetLanguage(i18n, "en");
            CheckText(name + " back to a key: follows", first, TestKeyEn);

            // A disabled button takes the text, in each way of turning it off.
            foreach (string mode in DisableModes)
            {
                SetButtonOn(first, mode, false);
                CallSetInteractText(first, "Off " + mode, OneArgument);
                SetButtonOn(first, mode, true);
                CheckText(name + " disabled (" + mode + "): literal shows after enabling", first, "Off " + mode);
                SetButtonOn(first, mode, false);
                CallSetInteractText(first, TestKey, KeyArgument);
                SetLanguage(i18n, "is");
                SetButtonOn(first, mode, true);
                CheckText(name + " disabled (" + mode + "): key shows after enabling", first, TestKeyIs);
                Check(name + " disabled (" + mode + "): interactive again", !first.DisableInteractive, "not interactive");
                SetLanguage(i18n, "en");
            }

            // It survives a toggle and ApplySettings, as a literal and as a key.
            CallSetInteractText(first, "Kept", OneArgument);
            ApplySettingsNow();
            CheckText(name + " literal survives ApplySettings", first, "Kept");
            first.gameObject.SetActive(false);
            first.gameObject.SetActive(true);
            CheckText(name + " literal survives a toggle", first, "Kept");
            CallSetInteractText(first, TestKey, KeyArgument);
            ApplySettingsNow();
            CheckText(name + " key survives ApplySettings", first, TestKeyEn);

            // A VR player with the trigger off: stays non-interactive, and turning the trigger on shows the new text.
            SimulateVR(Networking.LocalPlayer, true);
            SetSetting("allowVRTrigger", false);
            CheckSetText(name + " VR, off: literal doesn't set the text yet", first, "VR text", OneArgument, TestKeyEn);
            Check(name + " VR, off: not interactive", first.DisableInteractive, "interactive");
            SetSetting("allowVRTrigger", true);
            Check(name + " VR, on: interactive", !first.DisableInteractive, "not interactive");
            CheckText(name + " VR, on: shows the literal", first, "VR text");
            SetSetting("allowVRTrigger", false);
            CallSetInteractText(first, TestKey, KeyArgument);
            Check(name + " VR, off again: not interactive", first.DisableInteractive, "interactive");
            SetSetting("allowVRTrigger", true);
            CheckText(name + " VR, on again: shows the key's string", first, TestKeyEn);
            SimulateVR(Networking.LocalPlayer, false);
            SetSetting("allowVRTrigger", false);
            Check(name + " desktop again: interactive", !first.DisableInteractive, "not interactive");

            // A null or empty text never reads I18n.
            CheckSetText(name + " empty key", first, "", KeyArgument, "");
            CallSetInteractText(first, null, KeyArgument);
            Check(name + " null key", string.IsNullOrEmpty(first.InteractionText), "got \"" + first.InteractionText + "\"");
            CallSetInteractText(first, null, OneArgument);
            Check(name + " null literal", string.IsNullOrEmpty(first.InteractionText), "got \"" + first.InteractionText + "\"");
            Check(name + " no missing key logged", I18nLogCount(MissingKeyLog) == 0, string.Join(", ", i18nLogs));

            CheckNetworkEvents(name + ": no network event");
            StopRecordingNetworkEvents();
            Check(name + " no errors so far", errorCount == 0, errorCount + " errors");
            DestroyClone(first);
            DestroyClone(second);
            first = MakeI18nButton("PushButtonTestSetTextNoI18n", null, TestKey);
            // A button with no manager, which can't be clicked whatever the text.
            second = MakeUnstartedI18nButton("PushButtonTestSetTextNoManager", i18n, TestKey);
            second.publicVariables.RemoveVariable("manager");
            StartI18nButton(second);
        });
        AddStep(0.5f, () =>
        {
            if (i18n == null) return;
            string noI18nLog = "PushButtonTestSetTextNoI18n";
            CheckSetText(name + " no I18n, literal", first, "Plain", OneArgument, "Plain");
            Check(name + " no I18n, literal: no warning", I18nLogCount(noI18nLog) == 0, string.Join(", ", i18nLogs));
            CheckSetText(name + " no I18n, key: shows the text", first, TestKey, KeyArgument, TestKey);
            Check(name + " no I18n, key: one warning", I18nLogCount(noI18nLog) == 1, I18nLogCount(noI18nLog) + " warnings");
            CheckSetText(name + " no I18n, empty key", first, "", KeyArgument, "");
            Check(name + " no I18n, empty key: no warning", I18nLogCount(noI18nLog) == 1, I18nLogCount(noI18nLog) + " warnings");

            Check(name + " no manager: not interactive", second.DisableInteractive, "interactive");
            CallSetInteractText(second, "Plain", OneArgument);
            Check(name + " no manager, literal: not interactive", second.DisableInteractive, "interactive");
            CallSetInteractText(second, TestKey, KeyArgument);
            Check(name + " no manager, key: not interactive", second.DisableInteractive, "interactive");
            Check(name + " no manager: no errors", errorCount == 0, errorCount + " errors");
            DestroyClone(first);
            DestroyClone(second);
            first = null;
            second = null;
        });
        AddSetInteractTextEarlySteps(name, () => i18n);
        AddSetInteractTextUnstartedSteps(name, () => i18n);
        AddStep(0.1f, () =>
        {
            Application.logMessageReceived -= RecordI18nLogs;
            Application.logMessageReceived -= CountErrors;
            if (textCaller != null)
            {
                extraButtons.Remove(textCaller.gameObject);
                Object.Destroy(textCaller.gameObject);
            }
            textCaller = null;
            if (i18n != null) SetLanguage(i18n, "en");
        });
    }

    // A call before the I18n's first frame: a key shows as it is until the button starts listening and then translates,
    // and a literal stays while the button starts listening anyway, so a later key translates straight away.
    static void AddSetInteractTextEarlySteps(string name, System.Func<UdonBehaviour> sceneI18n)
    {
        string prefix = name + " early";
        UdonBehaviour late = null;
        UdonBehaviour key = null;
        UdonBehaviour literal = null;
        AddStep(0.1f, () =>
        {
            if (sceneI18n() == null) return;
            late = MakeI18nCopy(sceneI18n(), null);
            extraButtons.Add(late.gameObject);
            key = MakeI18nButton("PushButtonTestSetTextEarlyKey", late, "Press");
            literal = MakeI18nButton("PushButtonTestSetTextEarlyLiteral", late, "Press");
        });
        AddStep(0.3f, () =>
        {
            if (late == null) return;
            Check(prefix + ": not listening yet", !Listening(key), "listening before the I18n started");
            CheckSetText(prefix + ": a key shows as it is meanwhile", key, TestKey, KeyArgument, TestKey);
            CheckSetText(prefix + ": a literal shows", literal, "Plain", OneArgument, "Plain");
            late.InitializeUdonContent();
        });
        AddStep(0.3f, () =>
        {
            if (late == null) return;
            Check(prefix + ": listens once the I18n has started", Listening(key), "not listening");
            CheckText(prefix + ": the key is translated", key, TestKeyEn);
            Check(prefix + ": a literal button listens too", Listening(literal), "not listening");
            CheckText(prefix + ": the literal stays", literal, "Plain");
            CheckSetText(prefix + ": a later key translates straight away", literal, TestKey, KeyArgument, TestKeyEn);
            SetLanguage(late, "is");
            CheckText(prefix + ": follows later changes", key, TestKeyIs);
            CheckText(prefix + ": a later key follows later changes", literal, TestKeyIs);
            DestroyClone(key);
            DestroyClone(literal);
            extraButtons.Remove(late.gameObject);
            Object.Destroy(late.gameObject);
            key = null;
            literal = null;
            late = null;
        });
    }

    // A call on a button that has never been enabled: Udon drops it, as the method's doc comment says, and the button
    // keeps its saved text.
    static void AddSetInteractTextUnstartedSteps(string name, System.Func<UdonBehaviour> sceneI18n)
    {
        UdonBehaviour unstarted = null;
        AddStep(0.1f, () =>
        {
            if (sceneI18n() == null) return;
            unstarted = MakeUnstartedI18nButton("PushButtonTestSetTextUnstarted", sceneI18n(), TestKey);
            CallSetInteractText(unstarted, "Before the first enable", OneArgument);
            StartI18nButton(unstarted);
        });
        AddStep(0.3f, () =>
        {
            if (unstarted == null) return;
            CheckText(name + " never enabled: the call is dropped", unstarted, TestKeyEn);
            DestroyClone(unstarted);
            unstarted = null;
        });
    }
}
