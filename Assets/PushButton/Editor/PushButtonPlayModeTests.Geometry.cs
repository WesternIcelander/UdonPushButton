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

// Plan phase 2: rest pose, contact box, press amount and gizmos.
public static partial class PushButtonPlayModeTests
{
    // Plan step 2.2: the contact box comes from the BoxCollider, converted to meters.
    static void AddContactBoxTest()
    {
        AddStep(2f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            UdonBehaviour ub = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check("find button for contact box", ub != null, "no UdonBehaviour on " + ButtonPath)) return;
            float face = (float)ub.GetProgramVariable("frontFaceOffset");
            float halfU = (float)ub.GetProgramVariable("footprintHalfU");
            float halfV = (float)ub.GetProgramVariable("footprintHalfV");
            Vector3 axis = (Vector3)ub.GetProgramVariable("pressAxis");
            Debug.Log("[PushButtonTest] contact box: face " + face.ToString("F4") + ", footprint " + halfU.ToString("F4")
                + " x " + halfV.ToString("F4") + ", axis " + axis.ToString("F3"));
            Check("front face offset", Mathf.Abs(face - 0.01f) < Tolerance, "got " + face.ToString("F5"));
            Check("footprint half U", Mathf.Abs(halfU - 0.025f) < Tolerance, "got " + halfU.ToString("F5"));
            Check("footprint half V", Mathf.Abs(halfV - 0.025f) < Tolerance, "got " + halfV.ToString("F5"));
            Check("press axis normalized", Near(axis, new Vector3(0f, 0f, -1f)), "got " + axis.ToString("F4"));
        });
    }

    // Plan step 2.4: the gizmo's rest box matches the button, and with travel 0.019 the full-depth box's
    // front face sits 1 mm in front of the plate face, at any parent rotation.
    static void AddGizmoTest(string name)
    {
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            PushButton proxy = go != null ? go.GetComponent<PushButton>() : null;
            if (!Check(name + ": find PushButton component", proxy != null, "no PushButton on " + ButtonPath)) return;
            Transform plate = go.transform.parent != null ? go.transform.parent.Find("ButtonPlate") : null;
            if (!Check(name + ": find plate", plate != null, "no ButtonPlate next to " + ButtonPath)) return;

            Vector3 restPos;
            Quaternion restRot;
            Vector3 boxCenter;
            Vector3 boxSize;
            Vector3 fullCenter;
            bool ok = proxy.GetGizmoGeometry(out restPos, out restRot, out boxCenter, out boxSize, out fullCenter);
            if (!Check(name + ": has geometry", ok, "GetGizmoGeometry returned false")) return;

            UdonBehaviour ub = go.GetComponent<UdonBehaviour>();
            Vector3 udonRestPos = (Vector3)ub.GetProgramVariable("restWorldPos");
            Quaternion udonRestRot = (Quaternion)ub.GetProgramVariable("restWorldRot");
            Check(name + " rest pose from Udon", Near(restPos, udonRestPos) && Quaternion.Angle(restRot, udonRestRot) < 0.01f,
                "got " + restPos.ToString("F4") + ", Udon has " + udonRestPos.ToString("F4"));
            Check(name + " rest box size", Near(boxSize, new Vector3(0.05f, 0.05f, 0.02f)), "got " + boxSize.ToString("F4"));
            Check(name + " rest box centre", Near(boxCenter, Vector3.zero), "got " + boxCenter.ToString("F4"));

            // The press axis is local -Z, so the front face of each box is its +Z face.
            Vector3 fullFront = restPos + restRot * (fullCenter + new Vector3(0f, 0f, boxSize.z * 0.5f));
            Vector3 plateFace = plate.TransformPoint(new Vector3(0f, 0f, 0.5f));
            float gap = Vector3.Dot(fullFront - plateFace, plate.forward);
            Debug.Log("[PushButtonTest] " + name + ": full-depth front face " + (gap * 1000f).ToString("F2") + " mm in front of the plate");
            Check(name + " full depth 1 mm in front of plate", Mathf.Abs(gap - 0.001f) < Tolerance, "gap " + gap.ToString("F5"));
        });
    }

    // Plan step 2.1: the rest pose is cached in Start and the world rest pose follows a moving parent.
    static void AddRestPoseTest()
    {
        AddStep(2f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check("find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            buttonParent = go.transform.parent;

            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            Check("rest local position", Near(restLocal, new Vector3(0f, 0f, 0.03f)), "got " + restLocal.ToString("F4"));
            CheckWorldRestPose("rest world pose at start", restLocal);
            firstRestWorldPos = (Vector3)button.GetProgramVariable("restWorldPos");

            buttonParent.position += new Vector3(1f, 0.5f, -2f);
            buttonParent.rotation = Quaternion.Euler(35f, 120f, -70f);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            Vector3 restLocal = (Vector3)button.GetProgramVariable("restLocalPosition");
            CheckWorldRestPose("rest world pose after moving parent", restLocal);
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Check("rest world pose changed", !Near(restWorld, firstRestWorldPos), "still " + restWorld.ToString("F4"));
        });
    }

    // Plan steps 2.3 and 3.1: the button moves along the press axis from the world rest pose, jumps straight to a
    // deeper press, never goes past travelDistance, and springs back at returnSpeed. returnSpeed is lowered to
    // 0.01 m/s during the test so the spring-back is slow enough to catch half-way.
    // With the scene's parent (turned only on Y) and travel 0.019, full travel puts the button 1 mm in front of the plate,
    // at (0, 0, 0.011) in the parent.
    // The press comes from the debug fingertip, pushed from in front of the face to 0.03 m past it in one frame.
    static void AddPressTest(string name, bool checkAbsolute)
    {
        const float overPress = 0.03f;
        const float slowReturnSpeed = 0.01f;
        AddStep(0.5f, () =>
        {
            GameObject go = GameObject.Find(ButtonPath);
            button = go != null ? go.GetComponent<UdonBehaviour>() : null;
            if (!Check(name + ": find button", button != null, "no UdonBehaviour on " + ButtonPath)) return;
            buttonParent = go.transform.parent;
            savedReturnSpeed = (float)button.GetProgramVariable("returnSpeed");
            button.SetProgramVariable("returnSpeed", slowReturnSpeed);
            SetLocalPress(0f);
        });
        AddStep(0.2f, () =>
        {
            if (button == null) return;
            PlaceDebugTip(overPress, 0f, 0f);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Quaternion restRot = (Quaternion)button.GetProgramVariable("restWorldRot");
            Vector3 axis = (Vector3)button.GetProgramVariable("pressAxis");
            Vector3 expected = restWorld + restRot * (axis * travel);
            Vector3 actual = button.transform.position;
            Vector3 offsetInParent = buttonParent.InverseTransformVector(actual - restWorld);
            Debug.Log("[PushButtonTest] " + name + ": press " + press.ToString("F4") + " at " + actual.ToString("F4")
                + ", offset in parent " + offsetInParent.ToString("F4"));
            // At 0.01 m/s, easing in would have reached only 0.005 m by now.
            Check(name + " jumps in and stops at travel", Mathf.Abs(press - travel) < 1e-6f, "press " + press.ToString("F5") + ", travel " + travel.ToString("F5"));
            Check(name + " position", Near(actual, expected), "got " + actual.ToString("F4") + ", expected " + expected.ToString("F4"));
            Check(name + " along parent -Z", Near(offsetInParent, new Vector3(0f, 0f, -travel)), "offset " + offsetInParent.ToString("F4"));
            if (checkAbsolute)
                Check(name + " 1 mm in front of plate", Near(actual, buttonParent.TransformPoint(new Vector3(0f, 0f, 0.011f))), "got " + actual.ToString("F4"));
            SetLocalPress(0f);
            releaseTime = Time.time;
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float travel = (float)button.GetProgramVariable("travelDistance");
            float press = (float)button.GetProgramVariable("pressAmount");
            float expectedPress = travel - slowReturnSpeed * (Time.time - releaseTime);
            Debug.Log("[PushButtonTest] " + name + ": spring-back press " + press.ToString("F4") + ", expected about " + expectedPress.ToString("F4"));
            Check(name + " springs back gradually", press > 0f && press < travel, "press " + press.ToString("F5"));
            // Allow a couple of frames of slack between the editor's and Udon's view of the release.
            Check(name + " springs back at returnSpeed", Mathf.Abs(press - expectedPress) < 0.002f,
                "press " + press.ToString("F5") + ", expected " + expectedPress.ToString("F5"));
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Quaternion restRot = (Quaternion)button.GetProgramVariable("restWorldRot");
            Vector3 axis = (Vector3)button.GetProgramVariable("pressAxis");
            Check(name + " spring-back position", Near(button.transform.position, restWorld + restRot * (axis * press)),
                "got " + button.transform.position.ToString("F4"));
            button.SetProgramVariable("returnSpeed", savedReturnSpeed);
        });
        AddStep(0.5f, () =>
        {
            if (button == null) return;
            float press = (float)button.GetProgramVariable("pressAmount");
            Vector3 restWorld = (Vector3)button.GetProgramVariable("restWorldPos");
            Check(name + " back at rest", press == 0f && Near(button.transform.position, restWorld),
                "press " + press.ToString("F5") + ", at " + button.transform.position.ToString("F4"));
        });
    }
}
