using UdonSharp;

/// <summary>
/// Test only: calls PushButton.SetInteractText from compiled U#. UdonSharp fills in an omitted optional argument where
/// the call is compiled, so only a real caller can test calling it without the second argument.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PushButtonTestCaller : UdonSharpBehaviour
{
    public PushButton button;
    public string text;

    public void CallOneArgument()
    {
        button.SetInteractText(text);
    }

    public void CallLiteral()
    {
        button.SetInteractText(text, false);
    }

    public void CallKey()
    {
        button.SetInteractText(text, true);
    }
}
