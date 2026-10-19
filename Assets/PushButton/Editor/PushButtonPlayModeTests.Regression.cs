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

// Plan step 9.1: the regression pass with the parent rotated and without the plate.
public static partial class PushButtonPlayModeTests
{
    // Plan step 8.1: the click (4.2), engagement (5.3) and range gate (6.1) checks again, with the parent moved
    // 1 m aside and rotated to (35, 120, -70), and the ButtonPlate either active or disabled. `setup` names the setup in
    // every check's name.
    static void AddRegressionPass(string setup, bool plateActive)
    {
        string name = "regression" + setup;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            buttonParent = go.transform.parent;
            Transform plate = buttonParent.Find("ButtonPlate");
            if (!Check(name + ": find plate", plate != null, "no ButtonPlate next to " + ButtonPath)) { button = null; return; }
            RemoveDebugTip();
            SetLocalPress(0f);
            buttonParent.position = sceneParentPos + new Vector3(1f, 0.5f, -2f);
            buttonParent.rotation = Quaternion.Euler(35f, 120f, -70f);
            plate.gameObject.SetActive(plateActive);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            CheckWorldRestPose(name + " rest pose", restLocal);
            Transform plate = buttonParent.Find("ButtonPlate");
            Check(name + " plate " + (plateActive ? "active" : "disabled"), plate.gameObject.activeInHierarchy == plateActive,
                "activeInHierarchy " + plate.gameObject.activeInHierarchy);
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Debug.Log("[PushButtonTest] " + name + ": parent at " + buttonParent.position.ToString("F2") + " " + buttonParent.eulerAngles.ToString("F2")
                + ", plate " + (plateActive ? "active" : "disabled") + ", local player "
                + (Networking.LocalPlayer.GetPosition() - restWorld).magnitude.ToString("F2") + " m away");
            Check(name + " at rest and armed", (float)button.GetProgramVariable("pressAmount") == 0f && (bool)button.GetProgramVariable("armed"), "not ready");
        });
        AddClickTest(setup);
        AddEngagementTest(setup);
        AddRangeGateTest(setup);
    }

    // Puts the parent back where the scene has it and turns the plate back on.
    static void AddRegressionCleanup()
    {
        AddStep(0.3f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            if (go == null) return;
            button = go.GetComponent<UdonBehaviour>();
            RemoveDebugTip();
            Transform parent = go.transform.parent;
            parent.position = sceneParentPos;
            parent.rotation = sceneParentRot;
            Transform plate = parent.Find("ButtonPlate");
            if (plate != null) plate.gameObject.SetActive(true);
        });
    }
}
