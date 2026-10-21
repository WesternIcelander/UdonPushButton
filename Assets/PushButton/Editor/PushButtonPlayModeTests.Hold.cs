using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.SDKBase;
using VRC.Udon;

// Plan step 8.10: holding a click keeps the button pressed, and the release action.
public static partial class PushButtonPlayModeTests
{
    // A held click stays at the bottom, fires the press once and the release once when the input is released, then
    // springs back; a quick tap animates fully and fires press then release once each; a held networked click holds
    // on this (remote) copy until its presser's NetReleaseClick, a release from anyone else is ignored, an early one
    // lets the press-in finish, and the presser leaving ends the hold; a fingertip releases once when it rises past
    // the re-arm point and not while it wobbles below it; a null or empty releaseEventName sends nothing and logs no
    // error; disabling the button or the manager mid-hold releases once and leaves the button at rest; a VR trigger
    // hold behaves like a mouse hold; remote presses never fire the release.
    static void AddHoldTest()
    {
        const string name = "hold";
        const string remoteName = "PushButtonTest Remote H";
        const float slowClickDuration = 1f;
        const float slowReturnSpeed = 0.01f;
        float travel = 0f;
        float savedDuration = 0f;
        bool savedAllow = false;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            simulatedVRPlayers.Clear();
            sceneButton = button;
            if (!SetUpActionTarget(name)) { button = null; return; }
            travel = (float)button.GetProgramVariable("travelDistance");
            savedReturnSpeed = (float)button.GetProgramVariable("returnSpeed");
            savedDuration = (float)Setting("clickPressDuration");
            savedAllow = (bool)Setting("allowVRTrigger");
            errorCount = 0;
            Application.logMessageReceived += CountErrors;
            StartRecordingNetworkEvents();

            // A click held for 1 s (Interact is the use-down; the use-up comes later).
            Check(name + " at rest before clicking", (float)button.GetProgramVariable("pressAmount") == 0f && (bool)button.GetProgramVariable("armed"), "not ready");
            go.GetComponent<ClientSimUdonHelper>().Interact();
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            Check(name + " held: at the bottom", (float)button.GetProgramVariable("pressAmount") == travel, "press " + button.GetProgramVariable("pressAmount"));
            CheckButtonAtPress(name + " held: position", travel);
            Check(name + " held: still clicking", (bool)button.GetProgramVariable("clickActive") && (bool)button.GetProgramVariable("clickHeld"),
                "clickActive " + button.GetProgramVariable("clickActive") + ", clickHeld " + button.GetProgramVariable("clickHeld"));
            CheckActionCount(name + " held: press fired once", 1);
            CheckReleaseCount(name + " held: no release yet", 0);
            CheckNetworkEvents(name + " held: only the click event", NetClickEvent);
            ReleaseUse();
        });
        AddStep(0.05f, () =>
        {
            if (button == null) return;
            CheckReleaseCount(name + " held: release fired on input release", 1);
            Check(name + " held: press then release", string.Join(", ", actionOrder) == "pressed, released", "got " + string.Join(", ", actionOrder));
            Check(name + " held: no longer held", !(bool)button.GetProgramVariable("clickHeld"), "still held");
            CheckNetworkEvents(name + " held: release event sent", NetClickEvent, NetReleaseEvent);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + " held: sprang back", (float)button.GetProgramVariable("pressAmount") == 0f && !(bool)button.GetProgramVariable("clickActive"),
                "press " + button.GetProgramVariable("pressAmount"));
            CheckActionCount(name + " held: still one press", 1);
            CheckReleaseCount(name + " held: still one release", 1);
            ReleaseUse();
        });
        AddStep(0.1f, () =>
        {
            if (button == null) return;
            CheckReleaseCount(name + " another use-up releases nothing", 1);
            CheckNetworkEvents(name + " another use-up sends nothing", NetClickEvent, NetReleaseEvent);

            // A quick tap on a slow (0.5 s) press-in: it finishes pressing in, then springs straight back.
            actionCount = 0;
            releaseCount = 0;
            actionOrder.Clear();
            SetSetting("clickPressDuration", 0.5f);
            TapWithHelper(GameObject.Find(ButtonPath));
        });
        AddStep(0.25f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " tap: still pressing in after the input is released", press > 0f && press < travel && !(bool)button.GetProgramVariable("clickHeld"),
                "press " + press + ", clickHeld " + button.GetProgramVariable("clickHeld"));
            CheckActionCount(name + " tap: no press before the bottom", 0);
        });
        AddStep(0.75f, () =>
        {
            if (button == null) return;
            Check(name + " tap: back at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));
            Check(name + " tap: press then release once each", string.Join(", ", actionOrder) == "pressed, released", "got " + string.Join(", ", actionOrder));
            SetSetting("clickPressDuration", savedDuration);

            // A networked click from a remote player.
            ClientSimMain.SpawnRemotePlayer(remoteName);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (!Check(name + ": remote spawned", remote != null, "no remote player")) { button = null; return; }
            actionCount = 0;
            releaseCount = 0;
            AsCaller(remote, () => button.SendCustomEvent("NetPlayClickAnimation"));
            Check(name + " remote: presser recorded", (int)button.GetProgramVariable("clickPresserId") == remote.playerId,
                "clickPresserId " + button.GetProgramVariable("clickPresserId") + ", remote " + remote.playerId);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            Check(name + " remote: held at the bottom", (float)button.GetProgramVariable("pressAmount") == travel && (bool)button.GetProgramVariable("clickActive"),
                "press " + button.GetProgramVariable("pressAmount"));
            // A release from someone else doesn't end it.
            AsCaller(Networking.LocalPlayer, () => button.SendCustomEvent("NetReleaseClick"));
            button.SendCustomEvent("NetReleaseClick");
            Check(name + " remote: another player's release ignored", (bool)button.GetProgramVariable("clickHeld"), "released");
            ReleaseUse();
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            Check(name + " remote: local use-up doesn't release it", (float)button.GetProgramVariable("pressAmount") == travel, "press " + button.GetProgramVariable("pressAmount"));
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) AsCaller(remote, () => button.SendCustomEvent("NetReleaseClick"));
            Check(name + " remote: presser's release ends the hold", !(bool)button.GetProgramVariable("clickHeld"), "still held");
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + " remote: sprang back", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));

            // A release that arrives early lets the slow press-in finish first.
            button.SetProgramVariable("returnSpeed", slowReturnSpeed);
            SetSetting("clickPressDuration", slowClickDuration);
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) AsCaller(remote, () => button.SendCustomEvent("NetPlayClickAnimation"));
            clickTime = Time.time;
        });
        AddStep(0.1f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) AsCaller(remote, () => button.SendCustomEvent("NetReleaseClick"));
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            float expected = travel * (Time.time - clickTime) / slowClickDuration;
            Check(name + " early release: still pressing in", press > 0f && press < travel && Mathf.Abs(press - expected) < travel * 0.1f,
                "press " + press.ToString("F5") + ", expected " + expected.ToString("F5"));
        });
        AddStep(0.9f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            float released = Time.time - clickTime - slowClickDuration;
            Check(name + " early release: reached the bottom and springs back", !(bool)button.GetProgramVariable("clickActive")
                && Mathf.Abs(press - (travel - slowReturnSpeed * released)) < 0.002f,
                "press " + press.ToString("F5") + ", expected " + (travel - slowReturnSpeed * released).ToString("F5"));
            button.SetProgramVariable("returnSpeed", savedReturnSpeed);
            SetSetting("clickPressDuration", savedDuration);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + " early release: at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));

            // The presser leaving ends a remote hold.
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) AsCaller(remote, () => button.SendCustomEvent("NetPlayClickAnimation"));
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + " leave: held before", (float)button.GetProgramVariable("pressAmount") == travel, "press " + button.GetProgramVariable("pressAmount"));
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) ClientSimMain.RemovePlayer(remote);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + " leave: hold ended", !(bool)button.GetProgramVariable("clickActive") && (float)button.GetProgramVariable("pressAmount") == 0f,
                "clickActive " + button.GetProgramVariable("clickActive") + ", press " + button.GetProgramVariable("pressAmount"));
            CheckActionCount(name + " remote: never fires the press", 0);
            CheckReleaseCount(name + " remote: never fires the release", 0);

            // A fingertip: down fires the press, wobbling above the re-arm point doesn't release.
            actionOrder.Clear();
            SetLocalPress(0f);
        });
        AddStep(Settle, () => SetLocalPress(1f));
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " finger: press fires", 1);
            SetLocalPress(0.75f);
        });
        AddStep(Settle, () => SetLocalPress(0.6f));
        AddStep(Settle, () => SetLocalPress(0.95f));
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckReleaseCount(name + " finger: no release while wobbling above the re-arm point", 0);
            SetLocalPress(0.3f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckReleaseCount(name + " finger: release at the re-arm point", 1);
            Check(name + " finger: press then release", string.Join(", ", actionOrder) == "pressed, released", "got " + string.Join(", ", actionOrder));
            SetLocalPress(0.4f);
        });
        AddStep(Settle, () => SetLocalPress(0f));
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckReleaseCount(name + " finger: still one release", 1);
            CheckActionCount(name + " finger: still one press", 1);
        });

        // A null and an empty release event send nothing and log nothing, while the press still fires.
        foreach (string unset in new[] { null, "" })
        {
            string label = name + (unset == null ? " null" : " empty") + " release event";
            AddStep(Settle, () =>
            {
                if (button == null) return;
                actionCount = 0;
                releaseCount = 0;
                errorCount = 0;
                button.SetProgramVariable("releaseEventName", unset);
                SetLocalPress(1f);
            });
            AddStep(Settle, () => SetLocalPress(0f));
            AddStep(Settle, () =>
            {
                if (button == null) return;
                CheckActionCount(label + ": press fires", 1);
                CheckReleaseCount(label + ": no release", 0);
                Check(label + ": no error", errorCount == 0, errorCount + " errors");
                Check(label + ": nothing pending", !(bool)button.GetProgramVariable("releasePending") && (bool)button.GetProgramVariable("armed"), "release still pending");
                button.SetProgramVariable("releaseEventName", "OnButtonReleased");
            });
        }

        AddHoldResetCase(name + " button disabled mid-hold", () => SetButtonOn(button, "object", false), () => SetButtonOn(button, "object", true));
        AddHoldResetCase(name + " manager disabled mid-hold", () => SetManagerOn(false, false), () => SetManagerOn(false, true));

        // With allowVRTrigger on, a VR trigger hold behaves like a mouse hold.
        AddStep(Settle, () =>
        {
            if (button == null) return;
            RemoveDebugTip();
            actionCount = 0;
            releaseCount = 0;
            SimulateVR(Networking.LocalPlayer, true);
            SetSetting("allowVRTrigger", true);
            ClientSimUdonHelper helper = GameObject.Find(ButtonPath).GetComponent<ClientSimUdonHelper>();
            Check(name + " vr trigger: can interact", helper.CanInteract(), "CanInteract false");
            helper.Interact();
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            Check(name + " vr trigger: held at the bottom", (float)button.GetProgramVariable("pressAmount") == travel, "press " + button.GetProgramVariable("pressAmount"));
            CheckActionCount(name + " vr trigger: press fired", 1);
            CheckReleaseCount(name + " vr trigger: no release yet", 0);
            ReleaseUse();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckReleaseCount(name + " vr trigger: released", 1);
            Check(name + " vr trigger: back at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));
            SimulateVR(Networking.LocalPlayer, false);
            SetSetting("allowVRTrigger", savedAllow);
        });
        AddStep(0.1f, () =>
        {
            if (button != null) Check(name + " no errors logged", errorCount == 0, errorCount + " errors");
            Application.logMessageReceived -= CountErrors;
            StopRecordingNetworkEvents();
            simulatedVRPlayers.Clear();
            ApplySettingsNow();
            TearDownActionTarget(name);
        });
    }

    // A held local click whose press has fired, then reset by `turnOff`: the release fires once, the button is at
    // rest, and it stays one release when the button comes back and the input is released afterwards.
    static void AddHoldResetCase(string name, System.Action turnOff, System.Action turnOn)
    {
        AddStep(Settle, () =>
        {
            if (button == null) return;
            RemoveDebugTip();
            actionCount = 0;
            releaseCount = 0;
            GameObject.Find(ButtonPath).GetComponent<ClientSimUdonHelper>().Interact();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + ": press fired", 1);
            CheckReleaseCount(name + ": held, no release yet", 0);
            turnOff();
            CheckReleaseCount(name + ": release fired once", 1);
            CheckAtRestCleared(name, button);
            Check(name + ": no longer held", !(bool)button.GetProgramVariable("clickHeld"), "still held");
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            turnOn();
            ReleaseUse();
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckReleaseCount(name + ": still one release once back", 1);
            CheckAtRestCleared(name + " back on", button);
        });
    }
}
