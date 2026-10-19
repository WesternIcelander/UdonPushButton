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

// Plan steps 8.1 to 8.3: manager slots, no-manager buttons, bone reads per frame and ticking only in-range buttons.
public static partial class PushButtonPlayModeTests
{
    // Plan step 8.1: the manager keeps every player in a stable slot. Two remote players join and one leaves: the
    // slots still match GetPlayerCount, and a third player who joins takes the freed slot with a new generation.
    static void AddManagerSlotsTest()
    {
        const string name = "manager slots";
        int freedSlot = -1;
        int freedGeneration = 0;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            CheckTrackedPlayers(name + " at start");
            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote F");
            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote G");
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after two joins");
            VRCPlayerApi remoteF = FindPlayer("PushButtonTest Remote F");
            int tip = SlotOf(remoteF, 0);
            if (!Check(name + ": remote F has a slot", tip >= 0, "no slot")) { button = null; return; }
            freedSlot = tip / 4;
            freedGeneration = ((int[])ManagerOf(button).GetProgramVariable("slotGenerations"))[freedSlot];
            ClientSimMain.RemovePlayer(remoteF);
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after a leave");
            VRCPlayerApi[] slots = (VRCPlayerApi[])ManagerOf(button).GetProgramVariable("slotPlayers");
            Check(name + " leaving player's slot freed", slots[freedSlot] == null, "slot " + freedSlot + " still taken");
            Check(name + " remote G kept a slot", SlotOf(FindPlayer("PushButtonTest Remote G"), 0) >= 0, "missing");
            ClientSimMain.SpawnRemotePlayer("PushButtonTest Remote H");
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after a rejoin");
            int tip = SlotOf(FindPlayer("PushButtonTest Remote H"), 0);
            int slot = tip < 0 ? -1 : tip / 4;
            int generation = slot >= 0 ? ((int[])ManagerOf(button).GetProgramVariable("slotGenerations"))[slot] : -1;
            Debug.Log("[PushButtonTest] " + name + ": remote H in slot " + slot + " generation " + generation + ", freed slot " + freedSlot + " was generation " + freedGeneration);
            // Remote F took the lowest free slot and remote G the next one, so F's slot is the lowest free one again.
            Check(name + " new player reuses the freed slot", slot == freedSlot, "slot " + slot + ", freed " + freedSlot);
            Check(name + " reused slot gets a new generation", generation == freedGeneration + 1, "generation " + generation + ", was " + freedGeneration);
            ClientSimMain.RemovePlayer(FindPlayer("PushButtonTest Remote H"));
            ClientSimMain.RemovePlayer(FindPlayer("PushButtonTest Remote G"));
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            CheckTrackedPlayers(name + " after all left");
        });
    }

    // Plan step 8.1: a button with no manager is inert. A PushButton created at runtime without one logs exactly one
    // warning, can't be clicked (not interactive, and a direct Interact or networked click does nothing), ignores the
    // debug fingertip and stays at rest.
    static void AddNoManagerTest()
    {
        const string name = "no manager";
        float travel = 0f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            savedButton = button;
            AbstractUdonProgramSource program = AssetDatabase.LoadAssetAtPath<AbstractUdonProgramSource>(ButtonProgramPath);
            if (!Check(name + ": load PushButton program", program != null && program.SerializedProgramAsset != null, "no program asset")) { button = null; return; }

            noManagerButton = new GameObject(NoManagerName);
            noManagerButton.SetActive(false);
            noManagerButton.transform.SetPositionAndRotation(go.transform.position + new Vector3(0.2f, 0f, 0f), go.transform.rotation);
            noManagerButton.transform.localScale = go.transform.lossyScale;
            noManagerButton.AddComponent<BoxCollider>();
            UdonBehaviour inert = noManagerButton.AddComponent<UdonBehaviour>();
            inert.AssignProgramAndVariables(program.SerializedProgramAsset, new UdonVariableTable());
            noManagerWarnings = 0;
            Application.logMessageReceived += CountNoManagerWarnings;
            noManagerButton.SetActive(true);
            inert.InitializeUdonContent();
            noManagerStartPos = noManagerButton.transform.position;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            UdonBehaviour inert = noManagerButton.GetComponent<UdonBehaviour>();
            button = inert;
            travel = (float)inert.GetProgramVariable("travelDistance");
            Check(name + ": manager unset", ManagerOf(inert) == null, "has a manager");
            Check(name + " interact disabled", inert.DisableInteractive && !inert.IsInteractive, "DisableInteractive " + inert.DisableInteractive);
            ClientSimUdonHelper helper = noManagerButton.GetComponent<ClientSimUdonHelper>();
            Check(name + " ClientSim can't interact", helper == null || !helper.CanInteract(), "CanInteract true");
            inert.Interact();
            inert.SendCustomEvent("NetPlayClickAnimation");
            PlaceDebugTip(-0.01f, 0f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            Check(name + " click ignored", !(bool)button.GetProgramVariable("clickActive") && (float)button.GetProgramVariable("pressAmount") == 0f,
                "clickActive " + button.GetProgramVariable("clickActive") + ", press " + button.GetProgramVariable("pressAmount"));
            PlaceDebugTip(travel * 1.5f, 0f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + " debug fingertip ignored", (float)button.GetProgramVariable("localPress") == 0f && !(bool)button.GetProgramVariable("debugEngaged") && press == 0f,
                "localPress " + button.GetProgramVariable("localPress") + ", press " + press.ToString("F5"));
            Check(name + " stays at rest", Near(noManagerButton.transform.position, noManagerStartPos), "moved to " + noManagerButton.transform.position.ToString("F4"));
            Check(name + " still armed", (bool)button.GetProgramVariable("armed"), "disarmed");
        });
        AddStep(0.5f, () =>
        {
            Application.logMessageReceived -= CountNoManagerWarnings;
            Debug.Log("[PushButtonTest] " + name + ": " + noManagerWarnings + " warnings");
            if (noManagerButton != null) Check(name + " warns exactly once", noManagerWarnings == 1, noManagerWarnings + " warnings");
            RemoveDebugTip();
            if (noManagerButton != null) Object.Destroy(noManagerButton);
            noManagerButton = null;
            button = savedButton;
        });
    }

    static void CountNoManagerWarnings(string message, string stackTrace, LogType type)
    {
        if (type == LogType.Warning && message.Contains("[PushButton] " + NoManagerName + ": no manager")) noManagerWarnings++;
    }

    // Plan step 8.2: the manager reads each player's bones once per frame, however many buttons are near. With the
    // local player in VR and the scene button 2 cm in front of their left index, it counts GetBonePosition calls per
    // frame, then adds 9 more buttons beside it (all in range and near the hands) and counts again: the counts are
    // equal, both hands plus two bones per index finger. With every button 1 m above the hands only the hands are read.
    static void AddBoneReadCountTest()
    {
        const string name = "bone reads";
        const int extraCount = 9;
        int oneButtonReads = -1;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            savedParentPos = button.transform.parent.position;
            SimulateVR(Networking.LocalPlayer, true);
            if (!Check(name + ": local avatar has index bones", MoveButtonOntoLeftIndex(Networking.LocalPlayer, -0.02f), "no finger bones")) button = null;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            StartCountingBoneReads();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            oneButtonReads = BoneReadsPerFrame(name + " 1 button");
            Check(name + " 1 button: hands and index fingers", oneButtonReads == 6, oneButtonReads + " reads per frame, expected 6");
            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            for (int k = 0; k < extraCount; k++)
            {
                GameObject clone = Object.Instantiate(button.gameObject, button.transform.parent);
                clone.name = "PushButtonTestExtra" + k;
                // A 5 x 2 grid 6 cm apart on the plate's plane; the scene button keeps cell 2, at its own rest pose.
                int cell = k < 2 ? k : k + 1;
                clone.transform.localPosition = restLocal + new Vector3((cell % 5 - 2) * 0.06f, (cell / 5) * 0.06f, 0f);
                extraButtons.Add(clone);
                // Instantiated UdonBehaviours aren't set up by ClientSim (VRChat's own instantiate does that).
                UdonBehaviour extra = clone.GetComponent<UdonBehaviour>();
                if (extra != null) extra.InitializeUdonContent();
            }
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            int active = InRange(button) ? 1 : 0;
            foreach (GameObject clone in extraButtons)
            {
                if (InRange(clone.GetComponent<UdonBehaviour>())) active++;
            }
            Check(name + ": 10 buttons in range", active == extraCount + 1, active + " in range");
            Check(name + ": 10 buttons ticked", TickedButtons() == extraCount + 1, TickedButtons() + " ticked");
            StartCountingBoneReads();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            int tenButtonReads = BoneReadsPerFrame(name + " 10 buttons");
            Check(name + " 10 buttons read as much as 1", tenButtonReads == oneButtonReads && oneButtonReads > 0,
                tenButtonReads + " reads per frame, 1 button " + oneButtonReads);
            button.transform.parent.position += new Vector3(0f, 1f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            StartCountingBoneReads();
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            int farReads = BoneReadsPerFrame(name + " hands far");
            Check(name + " hands far: hands only", farReads == 2, farReads + " reads per frame, expected 2");
            StopCountingBoneReads();
            foreach (GameObject clone in extraButtons) Object.Destroy(clone);
            extraButtons.Clear();
            button.transform.parent.position = savedParentPos;
            simulatedVRPlayers.Clear();
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            Check(name + " at rest at the end", (float)button.GetProgramVariable("pressAmount") == 0f
                && (float)button.GetProgramVariable("localPress") == 0f, "pressed");
        });
    }

    // Plan step 8.3: the manager ticks only the buttons in range. With 9 clones of the scene button beside it (10 in
    // range) and 40 more on a holder 20 m away, it ticks exactly 10 a frame, the far ones are out of range and at rest,
    // and pressing one clone with its debug fingertip moves only that clone. Destroyed buttons drop out of the count.
    static void AddManagerTickTest()
    {
        const string name = "manager ticks";
        const int nearCount = 9;
        const int farCount = 40;
        float travel = 0f;
        GameObject farHolder = null;
        UdonBehaviour pressed = null;
        var restPositions = new Dictionary<UdonBehaviour, Vector3>();
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            simulatedVRPlayers.Clear();
            travel = (float)button.GetProgramVariable("travelDistance");
            Transform parent = button.transform.parent;
            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            farHolder = new GameObject("PushButtonTestFarButtons");
            farHolder.transform.SetPositionAndRotation(parent.position + new Vector3(20f, 0f, 0f), parent.rotation);
            for (int k = 0; k < nearCount + farCount; k++)
            {
                bool near = k < nearCount;
                GameObject clone = Object.Instantiate(button.gameObject, near ? parent : farHolder.transform);
                clone.name = (near ? "PushButtonTestNear" : "PushButtonTestFar") + k;
                // Near: a 5 x 2 grid 6 cm apart, the scene button keeping cell 2. Far: an 8 x 5 grid.
                int cell = near ? (k < 2 ? k : k + 1) : k - nearCount;
                int columns = near ? 5 : 8;
                clone.transform.localPosition = restLocal + new Vector3((cell % columns - 2) * 0.06f, (cell / columns) * 0.06f, 0f);
                extraButtons.Add(clone);
                UdonBehaviour extra = clone.GetComponent<UdonBehaviour>();
                if (extra != null) extra.InitializeUdonContent();
            }
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + ": 10 ticked", TickedButtons() == nearCount + 1, TickedButtons() + " ticked, expected " + (nearCount + 1));
            int nearInRange = InRange(button) ? 1 : 0;
            int farOut = 0;
            int farAtRest = 0;
            restPositions.Clear();
            restPositions[button] = button.transform.position;
            for (int k = 0; k < extraButtons.Count; k++)
            {
                UdonBehaviour extra = extraButtons[k].GetComponent<UdonBehaviour>();
                if (extra == null) continue;
                restPositions[extra] = extra.transform.position;
                if (k < nearCount)
                {
                    if (InRange(extra)) nearInRange++;
                    continue;
                }
                if (true.Equals(extra.GetProgramVariable("outOfRange"))) farOut++;
                if ((float)extra.GetProgramVariable("pressAmount") == 0f) farAtRest++;
            }
            Check(name + ": 10 in range", nearInRange == nearCount + 1, nearInRange + " in range");
            Check(name + ": 40 out of range", farOut == farCount, farOut + " out of range");
            Check(name + ": 40 at rest", farAtRest == farCount, farAtRest + " at rest");

            // Press the near clone in cell 0 with its own debug fingertip, from in front.
            pressed = extraButtons[0].GetComponent<UdonBehaviour>();
            UdonBehaviour sceneButton = button;
            button = pressed;
            PlaceDebugTip(-0.01f, 0f, 0f);
            button = sceneButton;
        });
        AddStep(0.3f, () =>
        {
            if (button == null || pressed == null) return;
            UdonBehaviour sceneButton = button;
            button = pressed;
            PlaceDebugTip(0.03f, 0f, 0f);
            button = sceneButton;
        });
        AddStep(0.3f, () =>
        {
            if (button == null || pressed == null) return;
            float press = (float)pressed.GetProgramVariable("pressAmount");
            Check(name + ": pressed clone at the bottom", Mathf.Abs(press - travel) < 1e-6f, "press " + press.ToString("F5"));
            Check(name + ": pressed clone moved", !Near(pressed.transform.position, restPositions[pressed]), "didn't move");
            int moved = 0;
            foreach (KeyValuePair<UdonBehaviour, Vector3> entry in restPositions)
            {
                if (entry.Key == pressed) continue;
                if (!Near(entry.Key.transform.position, entry.Value) || (float)entry.Key.GetProgramVariable("pressAmount") != 0f) moved++;
            }
            Check(name + ": no other button moved", moved == 0, moved + " others moved");
            Check(name + ": still 10 ticked while pressing", TickedButtons() == nearCount + 1, TickedButtons() + " ticked");

            // Tear down: the clones (and so the pressed one's debug fingertip) and the far holder.
            UdonBehaviour sceneButton = button;
            button = pressed;
            RemoveDebugTip();
            button = sceneButton;
            foreach (GameObject clone in extraButtons) Object.Destroy(clone);
            extraButtons.Clear();
            if (farHolder != null) Object.Destroy(farHolder);
            restPositions.Clear();
            pressed = null;
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            Check(name + ": only the scene button ticks at the end", TickedButtons() == 1, TickedButtons() + " ticked");
            Check(name + ": scene button at rest at the end", (float)button.GetProgramVariable("pressAmount") == 0f, "pressed");
        });
    }

    // Plan step 8.5: a ticking button only writes its transform when its press changes. With transform.hasChanged
    // cleared, the scene button at rest for 1 s hasn't changed; pressed by the debug fingertip, released and sprung back
    // to rest, it has; and at rest again for 1 s it hasn't. Moving the parent still carries the button along.
    static void AddTransformWriteTest()
    {
        const string name = "transform writes";
        float travel = 0f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            RemoveDebugTip();
            simulatedVRPlayers.Clear();
            travel = (float)button.GetProgramVariable("travelDistance");
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Check(name + ": in range", InRange(button), "out of range");
            Check(name + ": at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "pressed");
            button.transform.hasChanged = false;
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            Check(name + ": at rest for 1 s, unchanged", !button.transform.hasChanged, "transform changed");
            Check(name + ": still ticked", InRange(button) && TickedButtons() >= 1, "not ticked");
            CheckButtonAtPress(name + ": at the rest position", 0f);
            SetLocalPress(0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            SetLocalPress(1f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Check(name + ": pressed to the bottom", Mathf.Abs(press - travel) < 1e-6f, "press " + press.ToString("F5"));
            CheckButtonAtPress(name + ": at the bottom position", travel);
            Check(name + ": pressing changed it", button.transform.hasChanged, "transform unchanged");
            SetLocalPress(0f);
        });
        // Spring-back of 19 mm at 0.1 m/s takes about 0.2 s.
        AddStep(0.6f, () =>
        {
            if (button == null) return;
            Check(name + ": back at rest", (float)button.GetProgramVariable("pressAmount") == 0f, "pressed");
            CheckButtonAtPress(name + ": back at the rest position", 0f);
            Check(name + ": pressed, released and back at rest: changed", button.transform.hasChanged, "transform unchanged");
            RemoveDebugTip();
            button.transform.hasChanged = false;
        });
        AddStep(1f, () =>
        {
            if (button == null) return;
            Check(name + ": at rest again for 1 s, unchanged", !button.transform.hasChanged, "transform changed");
            button.transform.parent.position += new Vector3(0f, 0.1f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckButtonAtPress(name + ": parent moved, at the new rest position", 0f);
            button.transform.parent.position -= new Vector3(0f, 0.1f, 0f);
        });
        AddStep(0.3f, () =>
        {
            if (button == null) return;
            CheckButtonAtPress(name + ": parent moved back, at the rest position", 0f);
        });
    }
}
