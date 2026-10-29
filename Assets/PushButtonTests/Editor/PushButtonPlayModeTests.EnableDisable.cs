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

// Plan step 8.4: enabling and disabling buttons and the manager safely.
public static partial class PushButtonPlayModeTests
{
    // Plan step 8.4: enabling and disabling a button at any time, in any of the three ways (its GameObject, a parent
    // GameObject, its UdonBehaviour), leaves no stale state. A local desktop player, the test action target, and a
    // remote VR player (Toggler) who presses with their left index.
    static void AddEnableDisableTest()
    {
        const string name = "enable/disable";
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            simulatedVRPlayers.Clear();
            sceneButton = button;
            savedParentPos = button.transform.parent.position;
            if (!SetUpActionTarget(name)) { button = null; return; }
            errorCount = 0;
            Application.logMessageReceived += CountErrors;
            ClientSimMain.SpawnRemotePlayer(TogglerName);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            VRCPlayerApi toggler = FindPlayer(TogglerName);
            if (!Check(name + ": toggler spawned", toggler != null && SlotOf(toggler, 0) >= 0, "not tracked")) { button = null; return; }
            CheckRegistrationList(name + " at start");
            Check(name + " scene button registered once at start", Registrations(button) == 1, Registrations(button) + " registrations");
        });
        foreach (string mode in DisableModes)
        {
            string setup = name + " (" + mode + ")";
            AddMidPressFingerCase(setup + " local finger mid-press", mode, false);
            AddMidPressFingerCase(setup + " remote finger mid-press", mode, true);
            AddMidPressClickCase(setup + " local click mid-press", mode, true);
            AddMidPressClickCase(setup + " networked click mid-press", mode, false);
            AddStartsDisabledCase(setup + " starts disabled", mode);
            AddJoinLeaveWhileDisabledCase(setup + " join and leave while disabled", mode);
            AddParentMovedCase(setup + " parent moved while disabled", mode);
            AddActionTogglesCases(setup + " action", mode);
            AddToggleBurstCase(setup + " 20 toggles", mode);
            AddNetClickWhileDisabledCase(setup + " click while disabled", mode);
        }
        AddManagerDisabledCase(name + " manager object off", true);
        AddManagerDisabledCase(name + " manager behaviour off", false);
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            VRCPlayerApi toggler = FindPlayer(TogglerName);
            if (toggler != null) ClientSimMain.RemovePlayer(toggler);
            simulatedVRPlayers.Clear();
            RemoveDebugTip();
            button.transform.parent.position = savedParentPos;
            TearDownActionTarget(name);
            CheckRegistrationList(name + " at the end");
            Check(name + " scene button registered once at the end", Registrations(button) == 1, Registrations(button) + " registrations");
        });
        AddStep(0.5f, () =>
        {
            Application.logMessageReceived -= CountErrors;
            onAction = null;
            Debug.Log("[PushButtonTest] " + name + ": " + errorCount + " errors");
            Check(name + " no errors", errorCount == 0, errorCount + " errors logged");
            if (sceneButton != null) button = sceneButton;
        });
    }

    // Disabled while a fingertip (the debug fingertip as the local one, or the toggler's left index) presses half-way:
    // snaps to rest with everything cleared, stays there while off, and once enabled again with the finger still
    // inside it doesn't engage until the finger comes in from the front.
    static void AddMidPressFingerCase(string name, string mode, bool remote)
    {
        float travel = 0f;
        AddStep(Settle, () =>
        {
            if (button == null) return;
            travel = (float)button.GetProgramVariable("travelDistance");
            actionCount = 0;
            PlaceCaseFinger(remote, -0.01f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            PlaceCaseFinger(remote, travel * 0.5f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable(remote ? "remotePress" : "localPress");
            Check(name + ": pressing before the disable", press > 0f && (float)button.GetProgramVariable("pressAmount") > 0f, "press " + press.ToString("F5"));
            SetButtonOn(button, mode, false);
            CheckAtRestCleared(name + " disabled", button);
            CheckUnregistered(name + " disabled", button);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckAtRestCleared(name + " still at rest while off", button);
            SetButtonOn(button, mode, true);
            CheckRegisteredOnce(name + " enabled again", button);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable(remote ? "remotePress" : "localPress");
            Check(name + " finger still inside doesn't engage", press == 0f && (float)button.GetProgramVariable("pressAmount") == 0f && !AnyEngaged(button),
                "press " + press.ToString("F5") + ", engaged " + AnyEngaged(button));
            PlaceCaseFinger(remote, -0.01f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            PlaceCaseFinger(remote, travel * 0.5f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable(remote ? "remotePress" : "localPress");
            Check(name + " presses again from the front", press > 0f && AnyEngaged(button), "press " + press.ToString("F5"));
            CheckActionCount(name + " never fires", 0);
            RemoveDebugTip();
            simulatedVRPlayers.Clear();
            button.transform.parent.position = savedParentPos;
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            Check(name + " released at the end", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));
        });
    }

    // The case's fingertip at `press` meters: the debug fingertip, or the toggler's left index (moving the parent).
    // The toggler only counts as in VR while a case uses them (cleared at each case's end), so their fingers never
    // press in the other cases, whatever spot ClientSim puts them on.
    static void PlaceCaseFinger(bool remote, float press)
    {
        if (!remote)
        {
            PlaceDebugTip(press, 0f, 0f);
            return;
        }
        VRCPlayerApi toggler = FindPlayer(TogglerName);
        if (toggler != null) SimulateVR(toggler, true);
        if (toggler == null || !MoveButtonOntoLeftIndex(toggler, press)) Check("enable/disable: toggler's index bones", false, "no toggler or bones");
    }

    // Disabled half-way through a slow click (local, or a networked one): snaps to rest with the click cleared, and
    // once enabled again the click doesn't carry on or fire. A new local click afterwards fires once.
    static void AddMidPressClickCase(string name, string mode, bool local)
    {
        AddStep(Settle, () =>
        {
            if (button == null) return;
            actionCount = 0;
            savedClickDuration = (float)Setting("clickPressDuration");
            SetSetting("clickPressDuration", 1f);
            if (local) Tap(button);
            else NetTap(button);
        });
        AddStep(0.4f, () =>
        {
            if (button == null) return;
            Check(name + ": clicking before the disable", (bool)button.GetProgramVariable("clickActive") && (bool)button.GetProgramVariable("clickIsLocal") == local
                && (float)button.GetProgramVariable("pressAmount") > 0f, "clickActive " + button.GetProgramVariable("clickActive") + ", press " + button.GetProgramVariable("pressAmount"));
            SetButtonOn(button, mode, false);
            CheckAtRestCleared(name + " disabled", button);
            CheckUnregistered(name + " disabled", button);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckAtRestCleared(name + " still at rest while off", button);
            SetButtonOn(button, mode, true);
            CheckRegisteredOnce(name + " enabled again", button);
        });
        AddStep(1.2f, () =>
        {
            if (button == null) return;
            Check(name + " the click doesn't carry on", !(bool)button.GetProgramVariable("clickActive") && (float)button.GetProgramVariable("pressAmount") == 0f,
                "clickActive " + button.GetProgramVariable("clickActive") + ", press " + button.GetProgramVariable("pressAmount"));
            CheckActionCount(name + " never fires", 0);
            SetSetting("clickPressDuration", savedClickDuration);
            Tap(button);
        });
        AddStep(0.6f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " a new click fires once", 1);
            actionCount = 0;
        });
    }

    // A clone that starts disabled (never started) and is enabled with players already present: the local player's
    // and the toggler's VR index fingers both press it.
    static void AddStartsDisabledCase(string name, string mode)
    {
        UdonBehaviour clone = null;
        float travel = 0f;
        AddStep(Settle, () =>
        {
            if (button == null) return;
            travel = (float)button.GetProgramVariable("travelDistance");
            clone = MakeHolderClone("PushButtonTestStartsOff", mode, false);
        });
        AddStep(0.5f, () =>
        {
            if (button == null || clone == null) return;
            Check(name + ": not set up while off", false.Equals(clone.GetProgramVariable("initialized")), "initialized " + clone.GetProgramVariable("initialized"));
            CheckUnregistered(name + " while off", clone);
            SetButtonOn(clone, mode, true);
        });
        AddStep(Settle, () =>
        {
            if (button == null || clone == null) return;
            CheckRegisteredOnce(name + " enabled", clone);
            CheckAtRestCleared(name + " enabled", clone);
            SimulateVR(Networking.LocalPlayer, true);
            button = clone;
            if (!MoveButtonOntoLeftIndex(Networking.LocalPlayer, -0.02f)) Check(name + ": local index bones", false, "no bones");
            button = sceneButton;
        });
        AddStep(Settle, () =>
        {
            if (button == null || clone == null) return;
            button = clone;
            MoveButtonOntoLeftIndex(Networking.LocalPlayer, travel * 0.5f);
            button = sceneButton;
        });
        AddStep(Settle, () =>
        {
            if (button == null || clone == null) return;
            float press = (float)clone.GetProgramVariable("localPress");
            Check(name + " local VR finger presses", press > 0f && (float)clone.GetProgramVariable("pressAmount") > 0f, "localPress " + press.ToString("F5"));
            button = clone;
            PlaceCaseFinger(true, -0.02f);
            button = sceneButton;
        });
        AddStep(Settle, () =>
        {
            if (button == null || clone == null) return;
            button = clone;
            PlaceCaseFinger(true, travel * 0.5f);
            button = sceneButton;
        });
        AddStep(Settle, () =>
        {
            if (button == null || clone == null) return;
            float press = (float)clone.GetProgramVariable("remotePress");
            Check(name + " remote VR finger presses", press > 0f && (float)clone.GetProgramVariable("pressAmount") > 0f, "remotePress " + press.ToString("F5"));
            simulatedVRPlayers.Clear();
            DestroyClone(clone);
            clone = null;
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckRegistrationList(name + " after destroying the clone");
        });
    }

    // While the button is off, the player pressing it (a Leaver) leaves and a Joiner joins (taking the freed slot):
    // once enabled, nothing is engaged, the joiner's finger already inside doesn't press, and from the front it does.
    static void AddJoinLeaveWhileDisabledCase(string name, string mode)
    {
        string leaverName = "PushButtonTest Leaver " + mode;
        string joinerName = "PushButtonTest Joiner " + mode;
        float travel = 0f;
        AddStep(Settle, () =>
        {
            if (button == null) return;
            travel = (float)button.GetProgramVariable("travelDistance");
            simulatedVRPlayers.Clear();
            ClientSimMain.SpawnRemotePlayer(leaverName);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            VRCPlayerApi leaver = FindPlayer(leaverName);
            if (!Check(name + ": leaver spawned", leaver != null, "not spawned")) return;
            SimulateVR(leaver, true);
            MoveButtonOntoLeftIndex(leaver, -0.02f);
        });
        AddStep(Settle, () =>
        {
            VRCPlayerApi leaver = FindPlayer(leaverName);
            if (button == null || leaver == null) return;
            MoveButtonOntoLeftIndex(leaver, travel * 0.5f);
        });
        AddStep(Settle, () =>
        {
            VRCPlayerApi leaver = FindPlayer(leaverName);
            if (button == null || leaver == null) return;
            CheckPlayerTip(name + " leaver before the disable", leaver, true, false);
            SetButtonOn(button, mode, false);
            CheckAtRestCleared(name + " disabled", button);
            ClientSimMain.RemovePlayer(leaver);
            ClientSimMain.SpawnRemotePlayer(joinerName);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckAtRestCleared(name + " still at rest while off", button);
            SetButtonOn(button, mode, true);
            VRCPlayerApi joiner = FindPlayer(joinerName);
            if (!Check(name + ": joiner spawned", joiner != null, "not spawned")) return;
            SimulateVR(joiner, true);
            MoveButtonOntoLeftIndex(joiner, travel * 0.5f);
        });
        AddStep(Settle, () =>
        {
            VRCPlayerApi joiner = FindPlayer(joinerName);
            if (button == null || joiner == null) return;
            CheckTrackedPlayers(name + " enabled");
            CheckRegisteredOnce(name + " enabled", button);
            Check(name + " nothing engaged", !AnyEngaged(button) && (float)button.GetProgramVariable("remotePress") == 0f, "remotePress " + button.GetProgramVariable("remotePress"));
            MoveButtonOntoLeftIndex(joiner, -0.02f);
        });
        AddStep(Settle, () =>
        {
            VRCPlayerApi joiner = FindPlayer(joinerName);
            if (button == null || joiner == null) return;
            MoveButtonOntoLeftIndex(joiner, travel * 0.5f);
        });
        AddStep(Settle, () =>
        {
            VRCPlayerApi joiner = FindPlayer(joinerName);
            if (button == null || joiner == null) return;
            CheckPlayerTip(name + " joiner from the front", joiner, true, false);
            Check(name + " joiner presses", (float)button.GetProgramVariable("remotePress") > 0f, "remotePress " + button.GetProgramVariable("remotePress"));
            button.transform.parent.position = savedParentPos;
            ClientSimMain.RemovePlayer(joiner);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " at the end");
            Check(name + " released at the end", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));
        });
    }

    // The assembly moved and turned while the button is off: enabled again, it's at the new rest pose and presses there.
    static void AddParentMovedCase(string name, string mode)
    {
        Quaternion savedRotation = Quaternion.identity;
        float travel = 0f;
        AddStep(Settle, () =>
        {
            if (button == null) return;
            travel = (float)button.GetProgramVariable("travelDistance");
            SetButtonOn(button, mode, false);
            Transform parent = button.transform.parent;
            savedRotation = parent.rotation;
            parent.position = savedParentPos + new Vector3(0.3f, 0.1f, 0f);
            parent.rotation = savedRotation * Quaternion.Euler(20f, 30f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            SetButtonOn(button, mode, true);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            buttonParent = button.transform.parent;
            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            CheckWorldRestPose(name + " rest pose", restLocal);
            Check(name + " at the new rest pose", Near(button.transform.position, buttonParent.TransformPoint(restLocal)), "at " + button.transform.position.ToString("F4"));
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            PlaceDebugTip(travel * 0.5f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            Check(name + " presses at the new pose", (float)button.GetProgramVariable("localPress") > 0f, "localPress " + button.GetProgramVariable("localPress"));
            RemoveDebugTip();
            button.transform.parent.SetPositionAndRotation(savedParentPos, savedRotation);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            buttonParent = button.transform.parent;
            CheckWorldRestPose(name + " rest pose back", (Vector3)button.GetProgramVariable("restLocalPosition"));
        });
    }

    // The scene button's own action (run from inside the manager's tick, through the action log) disables the scene
    // button, enables another button, disables another button mid-click, and toggles another 20 times. No errors, no
    // move after a disable, a registration list with no duplicates or holes, and the other buttons keep working.
    static void AddActionTogglesCases(string name, string mode)
    {
        UdonBehaviour other = null;
        float travel = 0f;
        float savedDuration = 0f;
        AddStep(Settle, () =>
        {
            if (button == null) return;
            travel = (float)button.GetProgramVariable("travelDistance");
            other = MakeHolderClone("PushButtonTestOther", mode, true);
        });
        AddStep(0.5f, () =>
        {
            if (button == null || other == null) return;
            savedDuration = (float)Setting("clickPressDuration");
            SetSetting("clickPressDuration", 1f);
            other.SetProgramVariable("targetBehaviour", null);
            CheckRegisteredOnce(name + ": other button", other);
        });

        // Disables itself.
        AddFireSteps(() => SetButtonOn(button, mode, false));
        AddStep(0f, () =>
        {
            if (button == null || other == null) return;
            CheckActionCount(name + " disables itself: fired", 1);
            CheckAtRestCleared(name + " disables itself", button);
            CheckUnregistered(name + " disables itself", button);
            CheckRegisteredOnce(name + " disables itself: other button", other);
        });
        AddStep(Settle, () =>
        {
            if (button == null || other == null) return;
            CheckAtRestCleared(name + " disables itself: no move after", button);
            SetButtonOn(button, mode, true);
            CheckRegisteredOnce(name + " disables itself: enabled again", button);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStillWorksSteps(name + " disables itself: other button", () => other);

        // Enables another button.
        AddStep(Settle, () =>
        {
            if (button == null || other == null) return;
            SetButtonOn(other, mode, false);
            CheckUnregistered(name + " enables another: other off", other);
        });
        AddFireSteps(() => SetButtonOn(other, mode, true));
        AddStep(0f, () =>
        {
            if (button == null || other == null) return;
            CheckActionCount(name + " enables another: fired", 1);
            CheckRegisteredOnce(name + " enables another: other button", other);
            CheckRegisteredOnce(name + " enables another: itself", button);
            CheckAtRestCleared(name + " enables another: other button", other);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStillWorksSteps(name + " enables another: other button", () => other);

        // Disables another button (registered after this one, so later in the manager's loop) mid-click.
        AddStep(1.5f, () =>
        {
            if (button == null || other == null) return;
            NetTap(other);
        });
        AddFireSteps(() => SetButtonOn(other, mode, false));
        AddStep(0f, () =>
        {
            if (button == null || other == null) return;
            CheckActionCount(name + " disables another: fired", 1);
            CheckAtRestCleared(name + " disables another: other button", other);
            CheckUnregistered(name + " disables another: other button", other);
            Check(name + " disables another: itself at the bottom", Mathf.Abs((float)button.GetProgramVariable("pressAmount") - travel) < 1e-6f,
                "press " + button.GetProgramVariable("pressAmount"));
        });
        AddStep(Settle, () =>
        {
            if (button == null || other == null) return;
            CheckAtRestCleared(name + " disables another: no move after", other);
            SetButtonOn(other, mode, true);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStillWorksSteps(name + " disables another: other button", () => other);
        AddStillWorksSteps(name + " disables another: itself", () => button);

        // Toggles another button 20 times, ending enabled.
        AddFireSteps(() =>
        {
            for (int k = 0; k < 20; k++)
            {
                SetButtonOn(other, mode, false);
                SetButtonOn(other, mode, true);
            }
        });
        AddStep(0f, () =>
        {
            if (button == null || other == null) return;
            CheckActionCount(name + " toggles another 20 times: fired", 1);
            CheckRegisteredOnce(name + " toggles another 20 times: other button", other);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStillWorksSteps(name + " toggles another 20 times: other button", () => other);
        AddStep(1.5f, () =>
        {
            if (button == null) return;
            RemoveDebugTip();
            if (other != null) DestroyClone(other);
            other = null;
            actionCount = 0;
            if (savedDuration > 0f) SetSetting("clickPressDuration", savedDuration);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckRegistrationList(name + " after destroying the other button");
            Check(name + " no errors so far", errorCount == 0, errorCount + " errors logged");
        });
    }

    // Presses the scene button to the bottom with the debug fingertip (from in front), with `action` run once from
    // inside the button's action. The step after these runs in the frame after the press reached the bottom.
    static void AddFireSteps(System.Action action)
    {
        AddStep(Settle, () =>
        {
            if (button == null) return;
            actionCount = 0;
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            onAction = action;
            PlaceDebugTip((float)button.GetProgramVariable("travelDistance") * 1.5f, 0f, 0f);
        });
        AddStep(Settle, () =>
        {
            onAction = null;
        });
    }

    // The button still takes a networked click: it starts at once and moves the button (a 1 s click on the other
    // button, the default on the scene button, slowed down here for the check).
    static void AddStillWorksSteps(string name, System.Func<UdonBehaviour> target)
    {
        float savedDuration = 0f;
        AddStep(Settle, () =>
        {
            UdonBehaviour b = target();
            if (button == null || b == null) return;
            CheckRegisteredOnce(name, b);
            CheckRegistrationList(name);
            savedDuration = (float)Setting("clickPressDuration");
            SetSetting("clickPressDuration", 1f);
            NetTap(b);
            Check(name + " takes a click", (bool)b.GetProgramVariable("clickActive"), "click ignored");
        });
        AddStep(0.4f, () =>
        {
            UdonBehaviour b = target();
            if (button == null || b == null) return;
            Check(name + " moves", (float)b.GetProgramVariable("pressAmount") > 0f, "press " + b.GetProgramVariable("pressAmount"));
            SetSetting("clickPressDuration", savedDuration);
        });
    }

    // 20 disable/enable toggles in one frame, outside the manager's tick: registered exactly once, and still works.
    static void AddToggleBurstCase(string name, string mode)
    {
        AddStep(Settle, () =>
        {
            if (button == null) return;
            for (int k = 0; k < 20; k++)
            {
                SetButtonOn(button, mode, false);
                SetButtonOn(button, mode, true);
            }
            CheckRegisteredOnce(name, button);
            CheckRegistrationList(name);
        });
        AddStillWorksSteps(name, () => button);
        AddStep(1.5f, () =>
        {
            if (button == null) return;
            Check(name + " back at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));
        });
    }

    // A networked click (and a direct Interact) that reaches the button while it's off is ignored, also once it's on.
    static void AddNetClickWhileDisabledCase(string name, string mode)
    {
        AddStep(Settle, () =>
        {
            if (button == null) return;
            actionCount = 0;
            SetButtonOn(button, mode, false);
            NetTap(button);
            Tap(button);
            Check(name + " ignored", !(bool)button.GetProgramVariable("clickActive"), "click started");
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            SetButtonOn(button, mode, true);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckAtRestCleared(name + " enabled again", button);
            CheckActionCount(name + " never fires", 0);
        });
    }

    // The manager turned off (its GameObject or its UdonBehaviour) while the scene button is pressed by the debug
    // fingertip and another button is mid-click: both snap to rest and stay registered, nothing moves or clicks while
    // it's off, and a player who joins meanwhile presses once it's back on.
    static void AddManagerDisabledCase(string name, bool objectOff)
    {
        string joinerName = "PushButtonTest Manager Joiner " + (objectOff ? "object" : "behaviour");
        UdonBehaviour other = null;
        float travel = 0f;
        float savedDuration = 0f;
        AddStep(Settle, () =>
        {
            if (button == null) return;
            travel = (float)button.GetProgramVariable("travelDistance");
            simulatedVRPlayers.Clear();
            other = MakeHolderClone("PushButtonTestManagerOther", "object", true);
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(0.5f, () =>
        {
            if (button == null || other == null) return;
            savedDuration = (float)Setting("clickPressDuration");
            SetSetting("clickPressDuration", 1f);
            PlaceDebugTip(travel * 0.5f, 0f, 0f);
            NetTap(other);
        });
        AddStep(0.4f, () =>
        {
            if (button == null || other == null) return;
            Check(name + ": both pressed before", (float)button.GetProgramVariable("pressAmount") > 0f && (float)other.GetProgramVariable("pressAmount") > 0f,
                "press " + button.GetProgramVariable("pressAmount") + " and " + other.GetProgramVariable("pressAmount"));
            SetManagerOn(objectOff, false);
            Check(name + " not running", false.Equals(ManagerOf(button).GetProgramVariable("running")), "still running");
            CheckAtRestCleared(name + " scene button", button);
            CheckAtRestCleared(name + " other button", other);
            CheckRegisteredOnce(name + " scene button stays registered", button);
            CheckRegisteredOnce(name + " other button stays registered", other);
            Tap(button);
            NetTap(other);
            Check(name + " clicks ignored", !(bool)button.GetProgramVariable("clickActive") && !(bool)other.GetProgramVariable("clickActive"), "a click started");
            ClientSimMain.SpawnRemotePlayer(joinerName);
        });
        AddStep(1f, () =>
        {
            if (button == null || other == null) return;
            CheckAtRestCleared(name + " scene button still at rest while off", button);
            CheckAtRestCleared(name + " other button still at rest while off", other);
            SetManagerOn(objectOff, true);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " back on");
            Check(name + " finger still inside doesn't engage", (float)button.GetProgramVariable("localPress") == 0f && !AnyEngaged(button), "localPress " + button.GetProgramVariable("localPress"));
            RemoveDebugTip();
            VRCPlayerApi joiner = FindPlayer(joinerName);
            if (!Check(name + ": joiner spawned", joiner != null && SlotOf(joiner, 0) >= 0, "not tracked")) return;
            SimulateVR(joiner, true);
            MoveButtonOntoLeftIndex(joiner, -0.02f);
        });
        AddStep(Settle, () =>
        {
            VRCPlayerApi joiner = FindPlayer(joinerName);
            if (button == null || joiner == null) return;
            MoveButtonOntoLeftIndex(joiner, travel * 0.5f);
        });
        AddStep(Settle, () =>
        {
            VRCPlayerApi joiner = FindPlayer(joinerName);
            if (button == null || joiner == null) return;
            Check(name + " the new player presses", (float)button.GetProgramVariable("remotePress") > 0f, "remotePress " + button.GetProgramVariable("remotePress"));
            button.transform.parent.position = savedParentPos;
            ClientSimMain.RemovePlayer(joiner);
        });
        AddStillWorksSteps(name + " other button", () => other);
        AddStep(1.5f, () =>
        {
            if (button == null) return;
            if (other != null) DestroyClone(other);
            other = null;
            if (savedDuration > 0f) SetSetting("clickPressDuration", savedDuration);
            Check(name + " released at the end", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));
        });
    }

    static void SetManagerOn(bool objectOff, bool on)
    {
        UdonBehaviour manager = ManagerOf(sceneButton);
        if (manager == null) return;
        if (objectOff) manager.gameObject.SetActive(on);
        else manager.enabled = on;
    }

    // Turns a button off or on: its own GameObject, its parent GameObject, or its UdonBehaviour.
    static void SetButtonOn(UdonBehaviour pushButton, string mode, bool on)
    {
        if (mode == "object") pushButton.gameObject.SetActive(on);
        else if (mode == "parent") pushButton.transform.parent.gameObject.SetActive(on);
        else pushButton.enabled = on;
    }

    // A clone of the scene button, 8 cm beside it, on its own root holder at the assembly's pose (so turning the
    // holder off leaves the scene button alone). Unless `on`, it starts off in the given mode, so it never starts.
    static UdonBehaviour MakeHolderClone(string cloneName, string mode, bool on)
    {
        Transform parent = sceneButton.transform.parent;
        GameObject holder = new GameObject(cloneName + "Holder");
        holder.transform.SetPositionAndRotation(parent.position, parent.rotation);
        if (!on && mode == "parent") holder.SetActive(false);
        GameObject clone = Object.Instantiate(sceneButton.gameObject, holder.transform);
        clone.name = cloneName;
        clone.transform.localPosition = (Vector3)sceneButton.GetProgramVariable("restLocalPosition") + new Vector3(0.08f, 0f, 0f);
        clone.transform.localRotation = (Quaternion)sceneButton.GetProgramVariable("restLocalRotation");
        extraButtons.Add(holder);
        UdonBehaviour udon = clone.GetComponent<UdonBehaviour>();
        if (!on && mode == "object") clone.SetActive(false);
        if (!on && mode == "behaviour") udon.enabled = false;
        // Instantiated UdonBehaviours aren't set up by ClientSim (VRChat's own instantiate does that).
        udon.InitializeUdonContent();
        return udon;
    }

    static void DestroyClone(UdonBehaviour clone)
    {
        GameObject holder = clone.transform.parent.gameObject;
        extraButtons.Remove(holder);
        Object.Destroy(holder);
    }

    // The button is at its rest pose (from its parent now), not pressed, armed, and with nothing engaged or clicking.
    static void CheckAtRestCleared(string name, UdonBehaviour pushButton)
    {
        Vector3 rest = pushButton.transform.parent.TransformPoint((Vector3)pushButton.GetProgramVariable("restLocalPosition"));
        Check(name + ": at the rest pose", Near(pushButton.transform.position, rest), "at " + pushButton.transform.position.ToString("F4") + ", rest " + rest.ToString("F4"));
        float press = (float)pushButton.GetProgramVariable("pressAmount");
        float local = (float)pushButton.GetProgramVariable("localPress");
        float remote = (float)pushButton.GetProgramVariable("remotePress");
        float click = (float)pushButton.GetProgramVariable("clickPress");
        Check(name + ": no press", press == 0f && local == 0f && remote == 0f && click == 0f,
            "press " + press + ", local " + local + ", remote " + remote + ", click " + click);
        Check(name + ": armed", (bool)pushButton.GetProgramVariable("armed"), "disarmed");
        Check(name + ": nothing engaged or clicking", !AnyEngaged(pushButton) && !(bool)pushButton.GetProgramVariable("clickActive"),
            "engaged " + AnyEngaged(pushButton) + ", clickActive " + pushButton.GetProgramVariable("clickActive"));
    }

    // Whether any fingertip (a player's or the debug one) is engaged or marked as in front.
    static bool AnyEngaged(UdonBehaviour pushButton)
    {
        bool any = (bool)pushButton.GetProgramVariable("debugEngaged") || (bool)pushButton.GetProgramVariable("debugWasInFront");
        foreach (bool b in (bool[])pushButton.GetProgramVariable("fingertipEngaged")) any |= b;
        foreach (bool b in (bool[])pushButton.GetProgramVariable("fingertipWasInFront")) any |= b;
        return any;
    }

    static void CheckRegisteredOnce(string name, UdonBehaviour pushButton)
    {
        int count = Registrations(pushButton);
        Check(name + ": registered once", count == 1 && true.Equals(pushButton.GetProgramVariable("registeredWithManager")),
            count + " registrations, registeredWithManager " + pushButton.GetProgramVariable("registeredWithManager"));
    }

    static void CheckUnregistered(string name, UdonBehaviour pushButton)
    {
        int count = Registrations(pushButton);
        Check(name + ": unregistered", count == 0 && false.Equals(pushButton.GetProgramVariable("registeredWithManager")),
            count + " registrations, registeredWithManager " + pushButton.GetProgramVariable("registeredWithManager"));
    }

    // The manager's registered buttons, in order.
    static List<UdonBehaviour> Registered()
    {
        var result = new List<UdonBehaviour>();
        UdonBehaviour manager = ManagerOf(sceneButton != null ? sceneButton : button);
        if (manager == null) return result;
        object[] list = manager.GetProgramVariable("buttons") as object[];
        int count = (int)manager.GetProgramVariable("buttonCount");
        for (int i = 0; i < count && list != null && i < list.Length; i++) result.Add(list[i] as UdonBehaviour);
        return result;
    }

    static int Registrations(UdonBehaviour pushButton)
    {
        return Registered().FindAll(b => b == pushButton).Count;
    }

    // The registration list has no holes (null or destroyed entries) and no duplicates.
    static void CheckRegistrationList(string name)
    {
        List<UdonBehaviour> list = Registered();
        Check(name + ": registration list has no holes", !list.Exists(b => b == null), list.FindAll(b => b == null).Count + " holes in " + list.Count);
        Check(name + ": registration list has no duplicates", new HashSet<UdonBehaviour>(list).Count == list.Count, list.Count + " entries");
    }
}
