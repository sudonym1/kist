namespace SharedInventory;

internal sealed class WindowInput
{
    internal enum Action { None, Toggle, Close }

    private bool toggleWasHeld;
    private bool escapeWasHeld;
    private bool escapeConsumed;
    private int consumedEscapeFrame = -1;

    internal Action Update(bool visible, bool toggleHeld, bool escapeHeld, int frame)
    {
        var togglePressed = toggleHeld && !toggleWasHeld;
        var escapePressed = escapeHeld && !escapeWasHeld;
        toggleWasHeld = toggleHeld;
        escapeWasHeld = escapeHeld;
        if (!escapeHeld) escapeConsumed = false;

        if (visible && escapePressed)
        {
            consumedEscapeFrame = frame;
            escapeConsumed = true;
            return Action.Close;
        }
        return togglePressed ? Action.Toggle : Action.None;
    }

    internal bool BlockEscape(bool visible, int frame) => visible || escapeConsumed || consumedEscapeFrame == frame;

    internal bool BlockKey(bool visible, bool escape, int frame) =>
        escape ? BlockEscape(visible, frame) : visible;
}
