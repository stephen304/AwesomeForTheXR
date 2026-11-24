#pragma once

// Values returned by the injected InputContext.Current method.
enum class InputContext : int { Inactive, Menus, Levels, Gameplay, GameplayWithGlove };

constexpr bool isGameplay(InputContext context) {
    return context == InputContext::Gameplay || context == InputContext::GameplayWithGlove;
}
