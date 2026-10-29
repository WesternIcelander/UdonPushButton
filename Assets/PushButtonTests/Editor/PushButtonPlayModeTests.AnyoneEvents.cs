using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.SDKBase;
using VRC.Udon;

// Plan step 8.11: the "anyone pressed" and "anyone released" events.
public static partial class PushButtonPlayModeTests
{
    const string NetAnyonePressedEvent = "Others NetAnyonePressed";
    const string NetAnyoneReleasedEvent = "Others NetAnyoneReleased";

    // A local fingertip press and a click each send anyone pressed once locally, after the press event, and one
    // NetAnyonePressed; their release sends anyone released once, after the release event, and one NetAnyoneReleased;
    // the anyone events still fire with no press or release event set; NetAnyonePressed / NetAnyoneReleased called
    // directly send their event once and never the presser-only ones; a remote finger press and a networked click send
    // no anyone events; null and empty names send nothing, locally or over the network; the network events are ignored
    // while the button is disabled or its manager is; disabling mid-hold sends anyone released once.
    static void AddAnyoneEventsTest()
    {
        const string name = "anyone events";
        const string remoteName = "PushButtonTest Remote AE";
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            simulatedVRPlayers.Clear();
            sceneButton = button;
            if (!SetUpActionTarget(name)) { button = null; return; }
            SetAnyoneEventNames("OnAnyonePressed", "OnAnyoneReleased");
            errorCount = 0;
            Application.logMessageReceived += CountErrors;
            StartRecordingNetworkEvents();
            SetLocalPress(0f);
        });

        // A local fingertip press.
        AddStep(Settle, () => SetLocalPress(1f));
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckActionOrder(name + " finger: anyone pressed after the press", "pressed", "anyone pressed");
            CheckNetworkEvents(name + " finger: one NetAnyonePressed", NetAnyonePressedEvent);
            SetLocalPress(0f);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckActionOrder(name + " finger: anyone released after the release", "pressed", "anyone pressed", "released", "anyone released");
            CheckNetworkEvents(name + " finger: one NetAnyoneReleased", NetAnyonePressedEvent, NetAnyoneReleasedEvent);

            // A held click.
            ResetAnyoneRecords();
            GameObject.Find(ButtonPath).GetComponent<ClientSimUdonHelper>().Interact();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionOrder(name + " click: anyone pressed after the press", "pressed", "anyone pressed");
            CheckNetworkEvents(name + " click: one NetAnyonePressed", NetClickEvent, NetAnyonePressedEvent);
            ReleaseUse();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionOrder(name + " click: anyone released after the release", "pressed", "anyone pressed", "released", "anyone released");
            CheckNetworkEvents(name + " click: one NetAnyoneReleased", NetClickEvent, NetAnyonePressedEvent, NetReleaseEvent, NetAnyoneReleasedEvent);

            // No press or release event: the anyone events still fire.
            ResetAnyoneRecords();
            button.SetProgramVariable("eventName", "");
            button.SetProgramVariable("releaseEventName", "");
            SetLocalPress(1f);
        });
        AddStep(Settle, () => SetLocalPress(0f));
        AddStep(Settle, () =>
        {
            if (button == null) return;
            CheckActionOrder(name + " without press and release events: still sent", "anyone pressed", "anyone released");
            CheckNetworkEvents(name + " without press and release events: network events sent", NetAnyonePressedEvent, NetAnyoneReleasedEvent);
            button.SetProgramVariable("eventName", "OnButtonPressed");
            button.SetProgramVariable("releaseEventName", "OnButtonReleased");

            // The network events, called directly as another client receives them.
            ResetAnyoneRecords();
            button.SendCustomEvent("NetAnyonePressed");
            CheckActionOrder(name + " NetAnyonePressed: anyone pressed only", "anyone pressed");
            button.SendCustomEvent("NetAnyoneReleased");
            CheckActionOrder(name + " NetAnyoneReleased: anyone released only", "anyone pressed", "anyone released");
            CheckNetworkEvents(name + " received events send nothing on");
            Check(name + " received events leave the button at rest", (float)button.GetProgramVariable("pressAmount") == 0f
                && (bool)button.GetProgramVariable("armed") && !(bool)button.GetProgramVariable("releasePending"), "press or release state changed");

            // A remote VR player's fingertip, with the local player 1.5 m aside.
            ResetAnyoneRecords();
            RemoveDebugTip();
            ClientSimMain.SpawnRemotePlayer(remoteName);
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            savedPlayerPos = localPlayer.GetPosition();
            savedPlayerRot = localPlayer.GetRotation();
            localPlayer.TeleportTo(savedPlayerPos + new Vector3(1.5f, 0f, 0f), savedPlayerRot);
            savedParentPos = button.transform.parent.position;
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
            if (button != null && remote != null) MoveButtonOntoLeftIndex(remote, 0.03f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            Check(name + " remote finger: pressed the button", Mathf.Abs((float)button.GetProgramVariable("remotePress") - travel) < 1e-6f,
                "remotePress " + button.GetProgramVariable("remotePress"));
            button.transform.parent.position = savedParentPos;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionOrder(name + " remote finger: no events");
            CheckNetworkEvents(name + " remote finger: no network events");
            Networking.LocalPlayer.TeleportTo(savedPlayerPos, savedPlayerRot);
            simulatedVRPlayers.Clear();

            // A networked click from the remote player.
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) AsCaller(remote, () => button.SendCustomEvent("NetPlayClickAnimation"));
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) AsCaller(remote, () => button.SendCustomEvent("NetReleaseClick"));
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionOrder(name + " networked click: no events");
            CheckNetworkEvents(name + " networked click: no network events");
            VRCPlayerApi remote = FindPlayer(remoteName);
            if (remote != null) ClientSimMain.RemovePlayer(remote);
        });

        // Null and empty names send nothing, locally or over the network, and log nothing.
        foreach (string unset in new[] { null, "" })
        {
            string label = name + (unset == null ? " null" : " empty") + " names";
            AddStep(Settle, () =>
            {
                if (button == null) return;
                ResetAnyoneRecords();
                errorCount = 0;
                SetAnyoneEventNames(unset, unset);
                // In front first: a new debug fingertip placed straight inside never engages.
                SetLocalPress(0f);
            });
            AddStep(Settle, () => SetLocalPress(1f));
            AddStep(Settle, () => SetLocalPress(0f));
            AddStep(Settle, () =>
            {
                if (button == null) return;
                button.SendCustomEvent("NetAnyonePressed");
                button.SendCustomEvent("NetAnyoneReleased");
                CheckActionOrder(label + ": only the press and release", "pressed", "released");
                CheckNetworkEvents(label + ": no network events");
                Check(label + ": no error", errorCount == 0, errorCount + " errors");
                SetAnyoneEventNames("OnAnyonePressed", "OnAnyoneReleased");
            });
        }

        // The network events are ignored while the button is disabled, or its manager is.
        AddStep(Settle, () =>
        {
            if (button == null) return;
            ResetAnyoneRecords();
            SetButtonOn(button, "object", false);
            button.SendCustomEvent("NetAnyonePressed");
            button.SendCustomEvent("NetAnyoneReleased");
            CheckActionOrder(name + " button disabled: ignored");
            SetButtonOn(button, "object", true);
            SetManagerOn(false, false);
            button.SendCustomEvent("NetAnyonePressed");
            button.SendCustomEvent("NetAnyoneReleased");
            CheckActionOrder(name + " manager disabled: ignored");
            SetManagerOn(false, true);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            button.SendCustomEvent("NetAnyonePressed");
            CheckActionOrder(name + " back on: received again", "anyone pressed");

            // Disabling mid-hold sends anyone released once.
            ResetAnyoneRecords();
            GameObject.Find(ButtonPath).GetComponent<ClientSimUdonHelper>().Interact();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            CheckActionOrder(name + " disabled mid-hold: pressed", "pressed", "anyone pressed");
            SetButtonOn(button, "object", false);
            CheckActionOrder(name + " disabled mid-hold: released once", "pressed", "anyone pressed", "released", "anyone released");
            CheckNetworkEvents(name + " disabled mid-hold: one NetAnyoneReleased", NetClickEvent, NetAnyonePressedEvent, NetAnyoneReleasedEvent);
        });
        AddStep(Settle, () =>
        {
            if (button == null) return;
            SetButtonOn(button, "object", true);
            ReleaseUse();
        });
        AddStep(Settle, () =>
        {
            if (button != null)
            {
                CheckActionOrder(name + " disabled mid-hold: still one release once back", "pressed", "anyone pressed", "released", "anyone released");
                CheckNetworkEvents(name + " disabled mid-hold: nothing more sent", NetClickEvent, NetAnyonePressedEvent, NetAnyoneReleasedEvent);
                Check(name + " no errors logged", errorCount == 0, errorCount + " errors");
                SetAnyoneEventNames(null, null);
            }
            Application.logMessageReceived -= CountErrors;
            StopRecordingNetworkEvents();
            simulatedVRPlayers.Clear();
            RemoveDebugTip();
            TearDownActionTarget(name);
        });
    }

    static void SetAnyoneEventNames(string pressed, string released)
    {
        button.SetProgramVariable("anyonePressedEventName", pressed);
        button.SetProgramVariable("anyoneReleasedEventName", released);
    }

    // Clears the action logs and the recorded network events.
    static void ResetAnyoneRecords()
    {
        actionCount = 0;
        releaseCount = 0;
        actionOrder.Clear();
        sentNetworkEvents.Clear();
    }

    // The action target's logs since the last reset are exactly `expected`, in order.
    static void CheckActionOrder(string name, params string[] expected)
    {
        string got = string.Join(", ", actionOrder);
        Check(name, got == string.Join(", ", expected), "logged [" + got + "], expected [" + string.Join(", ", expected) + "]");
    }
}
