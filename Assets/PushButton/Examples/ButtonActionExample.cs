using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// Example action target for <see cref="PushButton"/>: logs who pressed it and toggles an optional object, and logs
/// the release (point the button's releaseEventName at OnButtonReleased).
/// Runs on the presser's client only; sync any shared state here if everyone should see it.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class ButtonActionExample : UdonSharpBehaviour
{
    [Tooltip("Optional object whose active state is toggled on each press.")]
    public GameObject toggleTarget;

    public void OnButtonPressed()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        string playerName = localPlayer != null ? localPlayer.displayName : "<no local player>";
        Debug.Log("[ButtonActionExample] pressed by " + playerName);

        if (toggleTarget != null)
        {
            toggleTarget.SetActive(!toggleTarget.activeSelf);
        }
    }

    public void OnButtonReleased()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        string playerName = localPlayer != null ? localPlayer.displayName : "<no local player>";
        Debug.Log("[ButtonActionExample] released by " + playerName);
    }
}
