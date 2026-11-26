#pragma once

// The low three bits select the screen context. Higher bits carry equipment unlocks.
enum class InputContext : int { Inactive, Menus, Levels, MenuAimOnly, Gameplay = 4, PointerOnly, Replay, PointerAndRecenter, GloveUnlocked = 8, SprayCanUnlocked = 16, CaffeineUnlocked = 32 };

constexpr InputContext operator|(InputContext a, InputContext b) {
    return static_cast<InputContext>(static_cast<int>(a) | static_cast<int>(b));
}

constexpr bool hasFlag(InputContext context, InputContext flag) {
    return (static_cast<int>(context) & static_cast<int>(flag)) != 0;
}

constexpr bool pointerWithoutEscape(InputContext context) {
    return context == InputContext::PointerOnly || context == InputContext::PointerAndRecenter;
}

constexpr bool usesMenuAim(InputContext context) {
    return pointerWithoutEscape(context) || context == InputContext::Menus || context == InputContext::Levels || context == InputContext::MenuAimOnly;
}

constexpr bool isGameplay(InputContext context) {
    return (static_cast<int>(context) & 7) == static_cast<int>(InputContext::Gameplay);
}

constexpr bool usesEscape(InputContext context) {
    return isGameplay(context) || context == InputContext::Menus ||
           context == InputContext::Levels || context == InputContext::Replay;
}

constexpr bool usesRecenter(InputContext context) {
    return usesEscape(context) || context == InputContext::PointerAndRecenter;
}
