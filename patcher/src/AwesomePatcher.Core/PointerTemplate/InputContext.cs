// Returned to the native plugin from both the camera and GameInput paths.
internal static class InputContext
{
    internal const int Inactive = 0, Menus = 1, Levels = 2, Gameplay = 3;
    internal static LoaderScene splash;

    internal static int Current()
    {
        if (splash != null)
            return splash.hasAnimationBegun && !splash.loading ? Menus : Inactive;

        var gui = GUIController.instance;
        var screen = gui != null ? gui.currentScreen : null;
        if (screen != null)
        {
            if (!gui.InputEnabled || screen is LoadingScreen) return Inactive;
            var levels = screen as LevelSelectScreen;
            if (levels != null) return levels.inputEnabled ? Levels : Inactive;
            var setup = screen as SetupScreen;
            if (setup != null && !setup.inputEnabled) return Inactive;
            var graphics = screen as GraphicsScreen;
            if (graphics != null && !graphics.inputEnabled) return Inactive;
            var choice = screen as ChoiceScreen;
            if (choice != null && !choice.inputEnabled) return Inactive;
            return Menus;
        }

        var input = GameInput.singleton;
        return GameController.singleton != null && input != null && input.inputEnabled &&
            !input.disabledInputAtStartOfLevel && Utilities.IsMidGame ? Gameplay : Inactive;
    }
}
