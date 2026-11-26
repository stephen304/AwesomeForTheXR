// Returned to the native plugin from both the camera and GameInput paths.
internal static class InputContext
{
    internal const int Inactive = 0, Menus = 1, Levels = 2, MenuAimOnly = 3, Gameplay = 4, PointerOnly = 5, Replay = 6, PointerAndRecenter = 7;
    internal const int GloveUnlocked = 8, SprayCanUnlocked = 16, CaffeineUnlocked = 32;
    internal static LoaderScene splash;

    internal static int Current()
    {
        if (splash != null)
            return splash.hasAnimationBegun && !splash.loading ? PointerOnly : Inactive;

        var input = GameInput.singleton;
        bool acceptsInput = input != null && input.inputEnabled && !input.disabledInputAtStartOfLevel;
        var gui = GUIController.instance;
        var screen = gui != null ? gui.currentScreen : null;
        if (screen != null)
        {
            if (screen is LoadingScreen) return Inactive;
            if (!gui.InputEnabled) return MenuAimOnly;
            var levels = screen as LevelSelectScreen;
            if (levels != null && !levels.inputEnabled) return MenuAimOnly;
            var setup = screen as SetupScreen;
            if (setup != null && !setup.inputEnabled) return MenuAimOnly;
            var graphics = screen as GraphicsScreen;
            if (graphics != null && !graphics.inputEnabled) return MenuAimOnly;
            var choice = screen as ChoiceScreen;
            if (choice != null && !choice.inputEnabled) return MenuAimOnly;
            if (!acceptsInput) return PointerOnly;
            if (screen is MainScreen || screen is GameOverScreen) return PointerAndRecenter;
            return levels != null ? Levels : Menus;
        }

        if (!acceptsInput) return Inactive;
        if (Utilities.IsReplayLevel) return Replay;
        if (GameController.singleton == null || !Utilities.IsMidGame) return Inactive;
        return Gameplay | (PlayerData.GloveEnabled ? GloveUnlocked : 0) |
            (PlayerData.SprayCanEnabled ? SprayCanUnlocked : 0) | (PlayerData.CaffeineEnabled ? CaffeineUnlocked : 0);
    }
}
