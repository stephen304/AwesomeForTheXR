using System;

class GameScreen { }
class LoadingScreen : GameScreen { }
class LevelSelectScreen : GameScreen { public bool inputEnabled; }
class SetupScreen : GameScreen { public bool inputEnabled; }
class GraphicsScreen : GameScreen { public bool inputEnabled; }
class ChoiceScreen : GameScreen { public bool inputEnabled; }
class GUIController { public static GUIController instance; public bool InputEnabled; public GameScreen currentScreen; }
class LoaderScene { public bool loading, hasAnimationBegun; }
class GameInput { public static GameInput singleton; public bool inputEnabled, disabledInputAtStartOfLevel; }
class GameController { public static GameController singleton; }
static class Utilities { public static bool IsMidGame; }

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
            Expect(InputContext.Menus, "interactive splash");
            InputContext.splash.loading = true;
            Expect(InputContext.Inactive, "departing splash");
            InputContext.splash = null;
            gui.currentScreen = new GameScreen();
            Expect(InputContext.Menus, "title, pause or results over a game");
            gui.InputEnabled = false;
            Expect(InputContext.Inactive, "locked GUI");
            gui.InputEnabled = true;
            foreach (var locked in new GameScreen[] { new LoadingScreen(), new SetupScreen(), new GraphicsScreen(),
                new ChoiceScreen(), new LevelSelectScreen() })
            {
                gui.currentScreen = locked;
                Expect(InputContext.Inactive, locked.GetType().Name);
            }
            foreach (var menu in new GameScreen[] { new SetupScreen { inputEnabled = true },
                new GraphicsScreen { inputEnabled = true }, new ChoiceScreen { inputEnabled = true } })
            {
                gui.currentScreen = menu;
                Expect(InputContext.Menus, menu.GetType().Name);
            }
            gui.currentScreen = new LevelSelectScreen { inputEnabled = true };
            Expect(InputContext.Levels, "level browser");
            gui.currentScreen = null;
            input.disabledInputAtStartOfLevel = true;
            Expect(InputContext.Inactive, "level entry");
            input.disabledInputAtStartOfLevel = false;
            input.inputEnabled = false;
            Expect(InputContext.Inactive, "game input lock");
            input.inputEnabled = true;
            Expect(InputContext.Gameplay, "live play and tutorial HUD");
            Utilities.IsMidGame = false;
            Expect(InputContext.Inactive, "replay or scene transition");
            GameInput.singleton = null;
            GameController.singleton = null;
            GUIController.instance = null;
            Expect(InputContext.Inactive, "startup without controllers");
        }
    }
}
