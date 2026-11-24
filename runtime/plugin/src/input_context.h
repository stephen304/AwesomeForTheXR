#pragma once

// Values returned by the injected InputContext.Current method.
enum class InputContext : int { Inactive, Menus, Levels, Gameplay, GameplayWithGlove, MenuAimOnly };

constexpr bool usesMenuAim(InputContext context) {
    return context == InputContext::Menus || context == InputContext::Levels || context == InputContext::MenuAimOnly;
}

constexpr bool isGameplay(InputContext context) {
    return context == InputContext::Gameplay || context == InputContext::GameplayWithGlove;
}
