// Shared by the injected menu pointer and its controller-free regression test.
internal static partial class MenuPointer
{
    internal static int owner = -1;
    private static int previousLeft, previousRight;
    private static uint lastLeftGeneration, lastRightGeneration;
    internal static bool down, held, up, splashDown, cancelHold;

    internal static void ResetButtons()
    {
        previousLeft = previousRight = 0;
        down = held = up = splashDown = cancelHold = false;
    }

    // 0 = unavailable, 1 = tracked and released, 3 = tracked and pressed.
    // A held trigger on reconnection must first be released.
    internal static int SelectHand(int left, int right, uint leftGeneration, uint rightGeneration)
    {
        bool leftInterrupted = leftGeneration != lastLeftGeneration;
        bool rightInterrupted = rightGeneration != lastRightGeneration;
        lastLeftGeneration = leftGeneration;
        lastRightGeneration = rightGeneration;
        bool leftDown = !leftInterrupted && left == 3 && previousLeft == 1;
        bool rightDown = !rightInterrupted && right == 3 && previousRight == 1;
        previousLeft = left;
        previousRight = right;
        if (owner < 0 && (right != 0 || left != 0)) owner = right != 0 ? 1 : 0;
        bool switched = owner == 1 ? leftDown : rightDown;
        cancelHold = switched || (owner == 0 ? leftInterrupted : rightInterrupted);
        if (switched) owner = 1 - owner;
        splashDown = !switched && (owner == 0 ? leftDown : rightDown);
        return owner == 0 ? left : right;
    }

    internal static void UpdateButtons(bool overPanel, int state)
    {
        if (!overPanel || cancelHold) held = false;
        down = overPanel && splashDown;
        up = held && state == 1;
        held = down || (held && state == 3);
    }
}
