using SharedInventory;

internal static partial class Program
{
    private static void WindowInputTests()
    {
        Test("Toggle opens and closes once per press, even when held across input updates", () =>
        {
            var input = new WindowInput();
            Equal(WindowInput.Action.Toggle, input.Update(false, true, false, 1));
            Equal(WindowInput.Action.None, input.Update(true, true, false, 1));
            Equal(WindowInput.Action.None, input.Update(true, true, false, 2));
            Equal(WindowInput.Action.None, input.Update(true, false, false, 3));
            Equal(WindowInput.Action.Toggle, input.Update(true, true, false, 4));
            Equal(WindowInput.Action.None, input.Update(false, true, false, 5));
        });
        Test("Escape is consumed before and after closing, then the next press reaches the game", () =>
        {
            var input = new WindowInput();
            Equal(true, input.BlockEscape(true, 10)); // Menu.Update before Plugin.Update.
            Equal(WindowInput.Action.Close, input.Update(true, false, true, 10));
            Equal(true, input.BlockEscape(false, 10)); // Menu.Update after Plugin.Update.
            Equal(WindowInput.Action.None, input.Update(false, false, true, 11));
            Equal(true, input.BlockEscape(false, 11)); // Keep consuming until release.
            input.Update(false, false, false, 12);
            Equal(false, input.BlockEscape(false, 12));
            Equal(WindowInput.Action.None, input.Update(false, false, true, 13));
            Equal(false, input.BlockEscape(false, 13));
        });
        Test("Simultaneous toggle and Escape close without reopening the window", () =>
        {
            var input = new WindowInput();
            Equal(WindowInput.Action.Close, input.Update(true, true, true, 20));
            Equal(true, input.BlockEscape(false, 20));
            Equal(WindowInput.Action.None, input.Update(false, true, true, 21));
        });
    }
}
