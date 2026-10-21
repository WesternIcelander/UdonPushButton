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

// Plan phases 3 and 4: triggering, input mode, and the local and networked click.
public static partial class PushButtonPlayModeTests
{
    // Plan step 4.1: the collider becomes a trigger, and for the (desktop) ClientSim player the button is interactive
    // with the "Press" tooltip. Reads what ClientSim's tooltip shows through its helper on the button.
    static void AddInputModeTest()
    {
        const string name = "input mode";
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            UdonBehaviour ub = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", ub != null, "no UdonBehaviour on " + ButtonPath)) return;

            Collider collider = go.GetComponent<Collider>();
            Check(name + " collider is trigger", collider != null && collider.isTrigger, collider == null ? "no collider" : "isTrigger false");

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (!Check(name + ": ClientSim local player", localPlayer != null, "Networking.LocalPlayer is null")) return;
            bool inVR = localPlayer.IsUserInVR();
            Check(name + " local player on desktop", !inVR, "IsUserInVR true");
            Check(name + " localIsVR matches", (bool)ub.GetProgramVariable("localIsVR") == inVR, "localIsVR differs");
            Check(name + " interact enabled", !ub.DisableInteractive && ub.IsInteractive,
                "DisableInteractive " + ub.DisableInteractive + ", IsInteractive " + ub.IsInteractive);

            ClientSimUdonHelper helper = go.GetComponent<ClientSimUdonHelper>();
            if (!Check(name + ": ClientSim helper", helper != null, "no ClientSimUdonHelper on " + ButtonPath)) return;
            string tooltip = helper.GetInteractText();
            Debug.Log("[PushButtonTest] " + name + ": ClientSim can interact " + helper.CanInteract() + ", tooltip \"" + tooltip + "\"");
            Check(name + " ClientSim can interact", helper.CanInteract(), "CanInteract false");
            Check(name + " tooltip text", tooltip == "Press", "got \"" + tooltip + "\"");
        });
    }

    // Plan step 3.2: the local press (the debug fingertip) fires the action once per sweep to the bottom, and hovering
    // at the bottom or rising only part-way (above releaseFraction) doesn't fire again.
    // The target is a ButtonActionExample created for the test (not the scene's own); presses are
    // counted from its "[ButtonActionExample] pressed" log line.
    static void AddTriggerTest()
    {
        const string name = "trigger";
        const float settle = 0.3f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            if (!SetUpActionTarget(name)) { button = null; return; }
            Check(name + " armed at start", (bool)button.GetProgramVariable("armed"), "not armed");
            SetLocalPress(0f);
        });
        AddSweepSteps(name + " sweep 1", 1, settle);
        AddSweepSteps(name + " sweep 2", 2, settle);

        // Down, part-way up (still above releaseFraction), down again: no second fire.
        AddStep(settle, () => SetLocalPress(1f));
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " partial: first bottom fires", 3);
            SetLocalPress(0.75f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            Check(name + " partial: still disarmed above releaseFraction", !(bool)button.GetProgramVariable("armed"), "re-armed");
            SetLocalPress(1f);
        });
        AddStep(settle, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " partial: no fire without re-arming", 3);
            SetLocalPress(0f);
        });

        // A remote player's press moving the button without firing is checked in AddRemotePressTest.
        AddStep(0.5f, () =>
        {
            if (button != null)
            {
                Check(name + " re-armed at the end", (bool)button.GetProgramVariable("armed"), "not armed");
                CheckActionCount(name + " total presses", 3);
                float press = (float)button.GetProgramVariable("pressAmount");
                Check(name + " back at rest", press == 0f, "press " + press.ToString("F5"));
            }
            TearDownActionTarget(name);
        });
    }

    // Plan step 4.2: a ClientSim click (Interact through the button's ClientSimUdonHelper, as ClientSim's own
    // interact does) animates the button in over clickPressDuration and back at returnSpeed, and fires exactly once.
    // The first click uses a 1 s press-in and a 0.01 m/s spring-back so the animation can be caught part-way.
    static void AddClickTest(string setup = "")
    {
        string name = "click" + setup;
        const float slowClickDuration = 1f;
        const float slowReturnSpeed = 0.01f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            ClientSimUdonHelper helper = go.GetComponent<ClientSimUdonHelper>();
            if (!Check(name + ": ClientSim helper", helper != null, "no ClientSimUdonHelper on " + ButtonPath)) { button = null; return; }
            if (!SetUpActionTarget(name)) { button = null; return; }
            savedReturnSpeed = (float)button.GetProgramVariable("returnSpeed");
            savedClickDuration = (float)Setting("clickPressDuration");
            button.SetProgramVariable("returnSpeed", slowReturnSpeed);
            SetSetting("clickPressDuration", slowClickDuration);
            Check(name + " armed and at rest before clicking", (bool)button.GetProgramVariable("armed") && (float)button.GetProgramVariable("pressAmount") == 0f, "not ready");
            helper.Interact();
            clickTime = Time.time;
        });
        AddStep(0.4f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            float expected = travel * (Time.time - clickTime) / slowClickDuration;
            Debug.Log("[PushButtonTest] " + name + ": press-in " + press.ToString("F4") + ", expected about " + expected.ToString("F4"));
            Check(name + " animates in part-way", press > 0f && press < travel, "press " + press.ToString("F5"));
            Check(name + " animates in over clickPressDuration", Mathf.Abs(press - expected) < travel * 0.1f,
                "press " + press.ToString("F5") + ", expected " + expected.ToString("F5"));
            CheckActionCount(name + " no fire before the bottom", 0);
            CheckButtonAtPress(name + " press-in position", press);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            float clickPress = (float)button.GetProgramVariable("clickPress");
            float released = Time.time - clickTime - slowClickDuration;
            Debug.Log("[PushButtonTest] " + name + ": " + released.ToString("F2") + " s after the bottom, press " + press.ToString("F4"));
            CheckActionCount(name + " fires once at the bottom", 1);
            // Re-arming follows the local press, and the click releases it to 0 right after the bottom frame,
            // so the button is armed again while it is still springing back.
            Check(name + " re-armed once the click releases", (bool)button.GetProgramVariable("armed"), "not armed");
            Check(name + " click released", !(bool)button.GetProgramVariable("clickActive") && clickPress == 0f,
                "clickActive " + button.GetProgramVariable("clickActive") + ", clickPress " + clickPress.ToString("F5"));
            Check(name + " springs back gradually", press > 0f && press < travel, "press " + press.ToString("F5"));
            Check(name + " springs back at returnSpeed", Mathf.Abs(press - (travel - slowReturnSpeed * released)) < 0.002f,
                "press " + press.ToString("F5") + ", expected " + (travel - slowReturnSpeed * released).ToString("F5"));
            CheckButtonAtPress(name + " spring-back position", press);
            button.SetProgramVariable("returnSpeed", savedReturnSpeed);
            SetSetting("clickPressDuration", savedClickDuration);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " back at rest", press == 0f, "press " + press.ToString("F5"));
            Check(name + " re-armed", (bool)button.GetProgramVariable("armed"), "not armed");
            CheckActionCount(name + " no fire on the way back", 1);
            // Second click at the default speeds.
            GameObject.Find(ButtonPath).GetComponent<ClientSimUdonHelper>().Interact();
        });
        AddStep(0.6f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            CheckActionCount(name + " second click fires once", 2);
            Check(name + " second click back at rest", press == 0f, "press " + press.ToString("F5"));
            Check(name + " second click re-armed", (bool)button.GetProgramVariable("armed"), "not armed");
            TearDownActionTarget(name);
        });
    }

    // Plan step 4.3: the network event handler, called directly as another player's click would, plays the click
    // animation flagged non-local and never fires the action. With the button moved more than 5 m from the local
    // player, the event is ignored.
    static void AddNetClickTest()
    {
        const string name = "net click";
        const float slowClickDuration = 1f;
        const float slowReturnSpeed = 0.01f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            if (!SetUpActionTarget(name)) { button = null; return; }
            savedReturnSpeed = (float)button.GetProgramVariable("returnSpeed");
            savedClickDuration = (float)Setting("clickPressDuration");
            button.SetProgramVariable("returnSpeed", slowReturnSpeed);
            SetSetting("clickPressDuration", slowClickDuration);
            Check(name + " armed and at rest before the event", (bool)button.GetProgramVariable("armed") && (float)button.GetProgramVariable("pressAmount") == 0f, "not ready");
            button.SendCustomEvent("NetPlayClickAnimation");
            clickTime = Time.time;
            Check(name + " starts a non-local click", (bool)button.GetProgramVariable("clickActive") && !(bool)button.GetProgramVariable("clickIsLocal"),
                "clickActive " + button.GetProgramVariable("clickActive") + ", clickIsLocal " + button.GetProgramVariable("clickIsLocal"));
        });
        AddStep(0.4f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            float expected = travel * (Time.time - clickTime) / slowClickDuration;
            Debug.Log("[PushButtonTest] " + name + ": press-in " + press.ToString("F4") + ", expected about " + expected.ToString("F4"));
            Check(name + " animates in over clickPressDuration", press > 0f && Mathf.Abs(press - expected) < travel * 0.1f,
                "press " + press.ToString("F5") + ", expected " + expected.ToString("F5"));
            CheckButtonAtPress(name + " press-in position", press);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            Debug.Log("[PushButtonTest] " + name + ": after the bottom, press " + press.ToString("F4"));
            CheckActionCount(name + " never fires", 0);
            Check(name + " stays armed", (bool)button.GetProgramVariable("armed"), "disarmed");
            Check(name + " click released", !(bool)button.GetProgramVariable("clickActive"), "still active");
            Check(name + " reached the bottom and springs back", press > 0f && press < travel, "press " + press.ToString("F5"));
            button.SetProgramVariable("returnSpeed", savedReturnSpeed);
            SetSetting("clickPressDuration", savedClickDuration);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " back at rest", press == 0f, "press " + press.ToString("F5"));
            CheckActionCount(name + " no fire on the way back", 0);

            // Out of range: move the assembly 20 m away from the local player and send the event again.
            Transform parent = button.transform.parent;
            Vector3 savedParentPos = parent.position;
            parent.position = savedParentPos + new Vector3(20f, 0f, 0f);
            button.SendCustomEvent("NetPlayClickAnimation");
            Check(name + " out of range is ignored", !(bool)button.GetProgramVariable("clickActive"), "click started");
            parent.position = savedParentPos;
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " out of range stays at rest", press == 0f, "press " + press.ToString("F5"));
            CheckActionCount(name + " out of range no fire", 0);
            TearDownActionTarget(name);
        });
    }
}
