// Returned to the native plugin from both the camera and GameInput paths.
internal static class InputContext
{
    internal const int Inactive = 0, Menus = 1, Levels = 2, MenuAimOnly = 3, Gameplay = 4;
    internal const int GloveUnlocked = 8, SprayCanUnlocked = 16;
    internal static LoaderScene splash;

    internal static int Current()
    {
        if (splash != null)
            return splash.hasAnimationBegun && !splash.loading ? Menus : Inactive;

        var gui = GUIController.instance;
        var screen = gui != null ? gui.currentScreen : null;
        if (screen != null)
        {
            if (screen is LoadingScreen) return Inactive;
            if (!gui.InputEnabled) return MenuAimOnly;
            var levels = screen as LevelSelectScreen;
            if (levels != null) return levels.inputEnabled ? Levels : MenuAimOnly;
            var setup = screen as SetupScreen;
            if (setup != null && !setup.inputEnabled) return MenuAimOnly;
            var graphics = screen as GraphicsScreen;
            if (graphics != null && !graphics.inputEnabled) return MenuAimOnly;
            var choice = screen as ChoiceScreen;
            if (choice != null && !choice.inputEnabled) return MenuAimOnly;
            return Menus;
        }

        var input = GameInput.singleton;
        if (GameController.singleton == null || input == null || !input.inputEnabled ||
            input.disabledInputAtStartOfLevel || !Utilities.IsMidGame) return Inactive;
        return Gameplay | (PlayerData.GloveEnabled ? GloveUnlocked : 0) |
            (PlayerData.SprayCanEnabled ? SprayCanUnlocked : 0);
    }
}
