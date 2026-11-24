using System;

class GameScreen { }
class LoadingScreen : GameScreen { }
class MainScreen : GameScreen { }
class GameOverScreen : GameScreen { }
class GamePauseScreen : GameScreen { }
class LevelSelectScreen : GameScreen { public bool inputEnabled; }
class SetupScreen : GameScreen { public bool inputEnabled; }
class GraphicsScreen : GameScreen { public bool inputEnabled; }
class ChoiceScreen : GameScreen { public bool inputEnabled; }
class GUIController { public static GUIController instance; public bool InputEnabled; public GameScreen currentScreen; }
class LoaderScene { public bool loading, hasAnimationBegun; }
class GameInput { public static GameInput singleton; public bool inputEnabled, disabledInputAtStartOfLevel; }
class GameController { public static GameController singleton; }
static class PlayerData { public static bool GloveEnabled, SprayCanEnabled, CaffeineEnabled; }
static class Utilities { public static bool IsMidGame, IsReplayLevel; }

namespace AwesomePatcher.IntegrationTests
{
    internal static partial class Program
    {
        private static void TestInputContexts()
        {
            void Expect(int expected, string state)
            {
                if (InputContext.Current() != expected) throw new InvalidOperationException("Wrong XR input context for " + state);
            }
            var gui = GUIController.instance = new GUIController { InputEnabled = true };
            GameController.singleton = new GameController();
            var input = GameInput.singleton = new GameInput { inputEnabled = true };
            Utilities.IsMidGame = true;
            InputContext.splash = new LoaderScene();
            Expect(InputContext.Inactive, "splash before animation");
            InputContext.splash.hasAnimationBegun = true;
            Expect(InputContext.PointerOnly, "interactive splash");
            InputContext.splash.loading = true;
            Expect(InputContext.Inactive, "departing splash");
            InputContext.splash = null;
            foreach (var screen in new GameScreen[] { new MainScreen(), new GameOverScreen() })
            {
                gui.currentScreen = screen;
                Expect(InputContext.PointerOnly, screen.GetType().Name);
            }
            gui.currentScreen = new GamePauseScreen();
            Expect(InputContext.Menus, "pause over a game");
            gui.InputEnabled = false;
            Expect(InputContext.MenuAimOnly, "pause with locked GUI");
            gui.currentScreen = new LoadingScreen();
            Expect(InputContext.Inactive, "loading with locked GUI");
            gui.InputEnabled = true;
            Expect(InputContext.Inactive, "loading with enabled GUI");
            foreach (var locked in new GameScreen[] { new SetupScreen(), new GraphicsScreen(),
                new ChoiceScreen(), new LevelSelectScreen() })
            {
                gui.currentScreen = locked;
                Expect(InputContext.MenuAimOnly, locked.GetType().Name);
            }
            foreach (var menu in new GameScreen[] { new SetupScreen { inputEnabled = true },
                new GraphicsScreen { inputEnabled = true }, new ChoiceScreen { inputEnabled = true } })
            {
                gui.currentScreen = menu;
                Expect(InputContext.Menus, menu.GetType().Name);
            }
            gui.currentScreen = new LevelSelectScreen { inputEnabled = true };
            Expect(InputContext.Levels, "level browser");
            input.inputEnabled = false;
            Expect(InputContext.PointerOnly, "gallery while GameInput ignores presses");
            input.inputEnabled = true;
            gui.currentScreen = null;
            input.disabledInputAtStartOfLevel = true;
            Expect(InputContext.Inactive, "level entry");
            input.disabledInputAtStartOfLevel = false;
            input.inputEnabled = false;
            Expect(InputContext.Inactive, "game input lock");
            input.inputEnabled = true;
            Expect(InputContext.Gameplay, "live play with locked equipment");
            PlayerData.CaffeineEnabled = true;
            Expect(InputContext.Gameplay | InputContext.CaffeineUnlocked, "caffeine only");
            PlayerData.CaffeineEnabled = false;
            PlayerData.GloveEnabled = true;
            Expect(InputContext.Gameplay | InputContext.GloveUnlocked, "live play with glove only");
            PlayerData.SprayCanEnabled = true;
            Expect(InputContext.Gameplay | InputContext.GloveUnlocked | InputContext.SprayCanUnlocked, "both items");
            PlayerData.GloveEnabled = false;
            Expect(InputContext.Gameplay | InputContext.SprayCanUnlocked, "spray can only");
            PlayerData.CaffeineEnabled = true;
            Expect(InputContext.Gameplay | InputContext.SprayCanUnlocked | InputContext.CaffeineUnlocked, "spray can and caffeine");
            gui.currentScreen = new GamePauseScreen();
            Expect(InputContext.Menus, "pause with unlocked equipment");
            gui.currentScreen = null;
            Utilities.IsMidGame = false;
            Expect(InputContext.Inactive, "scene transition");
            Utilities.IsReplayLevel = true;
            GameController.singleton = null;
            Expect(InputContext.Replay, "standalone replay without a gameplay controller");
            gui.currentScreen = new MainScreen();
            Expect(InputContext.PointerOnly, "title over replay");
            gui.currentScreen = null;
            input.disabledInputAtStartOfLevel = true;
            Expect(InputContext.Inactive, "replay entry lock");
            input.disabledInputAtStartOfLevel = false;
            input.inputEnabled = false;
            Expect(InputContext.Inactive, "replay input lock");
            Utilities.IsReplayLevel = false;
            GameInput.singleton = null;
            GameController.singleton = null;
            GUIController.instance = null;
            Expect(InputContext.Inactive, "startup without controllers");
            PlayerData.SprayCanEnabled = PlayerData.CaffeineEnabled = false;
        }
    }
}
