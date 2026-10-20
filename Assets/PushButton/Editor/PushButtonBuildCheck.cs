using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

/// <summary>
/// Checks the buttons' manager wiring when a VRChat build is requested (Build &amp; Test and Build &amp; Upload), and
/// offers to fix it through <see cref="PushButtonWiring.Fix"/>. It only asks when something is wrong, and it never
/// blocks the build. Tools/PushButton/Check Manager Wiring runs the same check; Tools/PushButton/Fix Manager Wiring
/// fixes without asking (for automated builds).
/// </summary>
public class PushButtonBuildCheck : IVRCSDKBuildRequestedCallback
{
    const string DialogTitle = "PushButton";

    public int callbackOrder => 0;

    // Answers the build check's question instead of the dialog while set (tests only).
    internal static Func<string, bool> testAnswer;

    public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
    {
        if (requestedBuildType == VRCSDKRequestedBuildType.Scene) Run(testAnswer ?? AskWithDialog);
        return true;
    }

    [MenuItem("Tools/PushButton/Check Manager Wiring")]
    static void CheckMenu()
    {
        if (Run(AskWithDialog) == Outcome.NothingToFix) Debug.Log("[PushButton] manager wiring is fine");
    }

    // Choosing the menu item is the consent, so it doesn't ask.
    [MenuItem("Tools/PushButton/Fix Manager Wiring")]
    static void FixMenu()
    {
        if (Application.isPlaying) return;
        WarnAboutExtraManagers(PushButtonWiring.AllManagers());
        PushButtonWiring.Fix();
    }

    public enum Outcome { NothingToFix, Fixed, Declined }

    /// <summary>
    /// The check, with <paramref name="ask"/> answering its yes/no question (the dialog, or a test's answer). Asks at
    /// most once, and only when there are buttons and either no manager or unwired buttons. Logs a warning on every
    /// check when there are several managers. Does nothing in Play Mode.
    /// </summary>
    public static Outcome Run(Func<string, bool> ask)
    {
        if (Application.isPlaying) return Outcome.NothingToFix;
        List<PushButton> buttons = PushButtonWiring.AllButtons();
        if (buttons.Count == 0) return Outcome.NothingToFix;
        List<PushButtonManager> managers = PushButtonWiring.AllManagers();
        WarnAboutExtraManagers(managers);

        string question;
        if (managers.Count == 0)
        {
            question = "The scene has " + Count(buttons.Count) + " but no PushButtonManager, so the buttons do nothing.\n\n"
                + "Add a " + PushButtonWiring.ManagerName + " at the scene root, wire every button to it and save the scene?";
        }
        else
        {
            PushButtonManager first = managers[0];
            int unwired = 0;
            foreach (PushButton button in buttons)
                if (PushButtonWiring.Problem(button, first) != null) unwired++;
            if (unwired == 0) return Outcome.NothingToFix;
            question = unwired + " of " + Count(buttons.Count) + (unwired == 1 ? " is" : " are") + " not wired to the scene's PushButtonManager (" + first.name
                + "), so " + (unwired == 1 ? "it does" : "they do") + " nothing.\n\nWire " + (unwired == 1 ? "it" : "them") + " and save the scene?";
        }

        if (!ask(question)) return Outcome.Declined;
        PushButtonWiring.Fix();
        return Outcome.Fixed;
    }

    static bool AskWithDialog(string question)
    {
        return EditorUtility.DisplayDialog(DialogTitle, question, "Fix", "Not Now");
    }

    static void WarnAboutExtraManagers(List<PushButtonManager> managers)
    {
        if (managers.Count < 2) return;
        List<string> ignored = new List<string>();
        for (int i = 1; i < managers.Count; i++) ignored.Add(PathOf(managers[i].transform));
        Debug.LogWarning("[PushButton] " + managers.Count + " PushButtonManagers in the scene. Using the first, "
            + PathOf(managers[0].transform) + "; ignoring " + string.Join(", ", ignored) + ".", managers[0]);
    }

    static string PathOf(Transform t)
    {
        string path = t.name;
        for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }

    static string Count(int buttons)
    {
        return buttons == 1 ? "1 PushButton" : buttons + " PushButtons";
    }
}
