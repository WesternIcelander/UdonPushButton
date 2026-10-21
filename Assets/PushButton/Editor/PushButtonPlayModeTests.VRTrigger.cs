using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.SDKBase;
using VRC.Udon;

// Plan step 8.9: VR players can click with the trigger when the manager's allowVRTrigger is on.
public static partial class PushButtonPlayModeTests
{
    const string NetClickEvent = "Others NetPlayClickAnimation";
    const string NetReleaseEvent = "Others NetReleaseClick";

    // With the local player faked as VR: with allowVRTrigger off, the button isn't interactive and Interact does
    // nothing; with it on, it's interactive with interactText, and Interact animates, fires once, sends the network
    // event and plays no haptics; the local (debug) fingertip holding the button partly in blocks Interact, a remote VR
    // finger doesn't; ApplySettings flips DisableInteractive both ways; a desktop player is interactive either way.
    static void AddVRTriggerTest()
    {
        const string name = "vr trigger";
        const string remoteName = "PushButtonTest Remote T";
        float travel = 0f;
        bool savedAllow = false;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            Check(name + " manager header is Click", HeaderOf(typeof(PushButtonManager), "allowVRTrigger") == "Click"
                && HeaderOf(typeof(PushButtonManager), "clickPressDuration") == "Click", "got " + HeaderOf(typeof(PushButtonManager), "allowVRTrigger"));
            savedAllow = (bool)Setting("allowVRTrigger");
            Check(name + " off by default", !savedAllow, "allowVRTrigger on");
            if (!SetUpActionTarget(name)) { button = null; return; }
            travel = (float)button.GetProgramVariable("travelDistance");
            savedParentPos = button.transform.parent.position;
            hapticCount = 0;
            VRCPlayerApi._PlayHapticEventInHand += RecordHaptic;
            StartRecordingNetworkEvents();

            // Off: the VR player can't click.
            SimulateVR(Networking.LocalPlayer, true);
            SetSetting("allowVRTrigger", false);
            Check(name + " off: local player counts as VR", (bool)button.GetProgramVariable("localIsVR"), "localIsVR false");
            Check(name + " off: not interactive", button.DisableInteractive && !button.IsInteractive, "DisableInteractive " + button.DisableInteractive);
            ClientSimUdonHelper helper = go.GetComponent<ClientSimUdonHelper>();
            Check(name + " off: ClientSim can't interact", helper == null || !helper.CanInteract(), "CanInteract true");
            Tap(button);
            Check(name + " off: Interact starts no click", !(bool)button.GetProgramVariable("clickActive"), "click started");
            CheckNetworkEvents(name + " off: no network event");
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " off: never fires", 0);
            Check(name + " off: at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));

            // On: interactive with interactText, and a click is exactly a desktop click.
            SetSetting("allowVRTrigger", true);
            string text = (string)button.GetProgramVariable("interactText");
            Check(name + " on: interactive", !button.DisableInteractive && button.IsInteractive, "DisableInteractive " + button.DisableInteractive);
            Check(name + " on: interactText", button.InteractionText == text, "got \"" + button.InteractionText + "\", expected \"" + text + "\"");
            TapWithHelper(GameObject.Find(ButtonPath));
            Check(name + " on: starts a local click", (bool)button.GetProgramVariable("clickActive") && (bool)button.GetProgramVariable("clickIsLocal"),
                "clickActive " + button.GetProgramVariable("clickActive") + ", clickIsLocal " + button.GetProgramVariable("clickIsLocal"));
            CheckNetworkEvents(name + " on: sends the network events for the click and its release", NetClickEvent, NetReleaseEvent);
        });
        AddStep(0.05f, () =>
        {
            if (button == null) return;
            Check(name + " on: animates in", (float)button.GetProgramVariable("pressAmount") > 0f, "press " + button.GetProgramVariable("pressAmount"));
        });
        AddStep(0.6f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " on: fires once", 1);
            Check(name + " on: no haptics", hapticCount == 0, hapticCount + " pulses");
            Check(name + " on: back at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));
            CheckNetworkEvents(name + " on: one network event each for the click and its release", NetClickEvent, NetReleaseEvent);

            // The local (debug) fingertip holding the button partly in blocks a trigger click.
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
            Check(name + " local finger: holding it in", (float)button.GetProgramVariable("localPress") > 0f, "localPress " + button.GetProgramVariable("localPress"));
            Tap(button);
            Check(name + " local finger: Interact ignored", !(bool)button.GetProgramVariable("clickActive"), "click started");
            CheckNetworkEvents(name + " local finger: no network event", NetClickEvent, NetReleaseEvent);
            RemoveDebugTip();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " local finger: no fire", 1);
            Check(name + " local finger: back at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));

            // A remote VR finger holding it in doesn't block it. The local player steps aside so their own
            // (VR) hands stay clear of the button.
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            savedPlayerPos = localPlayer.GetPosition();
            savedPlayerRot = localPlayer.GetRotation();
            localPlayer.TeleportTo(savedPlayerPos + new Vector3(1.5f, 0f, 0f), savedPlayerRot);
            ClientSimMain.SpawnRemotePlayer(remoteName);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (!Check(name + ": remote spawned and tracked", remote != null && SlotOf(remote, 0) >= 0, "not tracked")) return;
            SimulateVR(remote, true);
            Check(name + ": remote avatar has index bones", MoveButtonOntoLeftIndex(remote, -0.02f), "no finger bones");
        });
        AddStep(0.3f, () =>
        {
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (button == null || remote == null) return;
            MoveButtonOntoLeftIndex(remote, travel * 0.5f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null || FindPlayer(remoteName) == null) return;
            float remotePress = (float)button.GetProgramVariable("remotePress");
            Check(name + " remote finger: holding it in", remotePress > 0f && (float)button.GetProgramVariable("localPress") == 0f,
                "remotePress " + remotePress + ", localPress " + button.GetProgramVariable("localPress"));
            Tap(button);
            Check(name + " remote finger: Interact clicks", (bool)button.GetProgramVariable("clickActive") && (bool)button.GetProgramVariable("clickIsLocal"),
                "clickActive " + button.GetProgramVariable("clickActive"));
            CheckNetworkEvents(name + " remote finger: sends the network events", NetClickEvent, NetReleaseEvent, NetClickEvent, NetReleaseEvent);
        });
        AddStep(0.4f, () =>
        {
            if (button == null) return;
            CheckActionCount(name + " remote finger: fires", 2);
            button.transform.parent.position = savedParentPos;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) ClientSimMain.RemovePlayer(remote);
            Networking.LocalPlayer.TeleportTo(savedPlayerPos, savedPlayerRot);

            // ApplySettings toggles it at runtime, both ways.
            SetSetting("allowVRTrigger", false);
            Check(name + " toggled off", button.DisableInteractive, "still interactive");
            SetSetting("allowVRTrigger", true);
            Check(name + " toggled on", !button.DisableInteractive, "not interactive");

            // A desktop player is interactive either way.
            SimulateVR(Networking.LocalPlayer, false);
            SetSetting("allowVRTrigger", false);
            Check(name + " desktop, off: interactive", !button.DisableInteractive && !(bool)button.GetProgramVariable("localIsVR"), "DisableInteractive " + button.DisableInteractive);
            SetSetting("allowVRTrigger", true);
            Check(name + " desktop, on: interactive", !button.DisableInteractive, "DisableInteractive " + button.DisableInteractive);
            SetSetting("allowVRTrigger", savedAllow);
        });
        AddStep(0.5f, () =>
        {
            if (button != null)
            {
                Check(name + " back at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "press " + button.GetProgramVariable("pressAmount"));
                CheckActionCount(name + " total presses", 2);
                Check(name + " never any haptics", hapticCount == 0, hapticCount + " pulses");
            }
            VRCPlayerApi._PlayHapticEventInHand -= RecordHaptic;
            StopRecordingNetworkEvents();
            simulatedVRPlayers.Clear();
            ApplySettingsNow();
            TearDownActionTarget(name);
        });
    }
}
