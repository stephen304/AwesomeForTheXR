#pragma once

// Values returned by the injected InputContext.Current method.
enum class InputContext : int { Inactive, Menus, Levels, MenuAimOnly, Gameplay = 4, GloveUnlocked = 8, SprayCanUnlocked = 16, CaffeineUnlocked = 32 };

constexpr InputContext operator|(InputContext a, InputContext b) {
    return static_cast<InputContext>(static_cast<int>(a) | static_cast<int>(b));
}

constexpr bool hasFlag(InputContext context, InputContext flag) {
    return (static_cast<int>(context) & static_cast<int>(flag)) != 0;
}

constexpr bool usesMenuAim(InputContext context) {
    return context == InputContext::Menus || context == InputContext::Levels || context == InputContext::MenuAimOnly;
}

constexpr bool isGameplay(InputContext context) {
    return hasFlag(context, InputContext::Gameplay);
}
