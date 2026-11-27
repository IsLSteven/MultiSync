namespace MultiSync.Linux.Input;

/// <summary>
/// Bidirectional translation table between Windows Virtual-Key (VK_*) codes and Linux evdev/uinput KEY_* codes
/// plus X11 KeySym values for Debian, Mint, Ubuntu, and Arch Linux (including ABNT2 / international layouts).
/// </summary>
public static class LinuxKeyMap
{
    // Linux evdev KEY_* constants from <linux/input-event-codes.h>
    private static readonly Dictionary<ushort, ushort> VkToEvdev = new()
    {
        [0x08] = 14,  // VK_BACK -> KEY_BACKSPACE
        [0x09] = 15,  // VK_TAB -> KEY_TAB
        [0x0D] = 28,  // VK_RETURN -> KEY_ENTER
        [0x10] = 42,  // VK_SHIFT -> KEY_LEFTSHIFT
        [0x11] = 29,  // VK_CONTROL -> KEY_LEFTCTRL
        [0x12] = 56,  // VK_MENU -> KEY_LEFTALT
        [0x13] = 119, // VK_PAUSE -> KEY_PAUSE
        [0x14] = 58,  // VK_CAPITAL -> KEY_CAPSLOCK
        [0x1B] = 1,   // VK_ESCAPE -> KEY_ESC
        [0x20] = 57,  // VK_SPACE -> KEY_SPACE
        [0x21] = 104, // VK_PRIOR -> KEY_PAGEUP
        [0x22] = 109, // VK_NEXT -> KEY_PAGEDOWN
        [0x23] = 107, // VK_END -> KEY_END
        [0x24] = 102, // VK_HOME -> KEY_HOME
        [0x25] = 105, // VK_LEFT -> KEY_LEFT
        [0x26] = 103, // VK_UP -> KEY_UP
        [0x27] = 106, // VK_RIGHT -> KEY_RIGHT
        [0x28] = 108, // VK_DOWN -> KEY_DOWN
        [0x2C] = 99,  // VK_SNAPSHOT -> KEY_SYSRQ
        [0x2D] = 110, // VK_INSERT -> KEY_INSERT
        [0x2E] = 111, // VK_DELETE -> KEY_DELETE

        // Digits 0..9
        [0x30] = 11, [0x31] = 2, [0x32] = 3, [0x33] = 4, [0x34] = 5,
        [0x35] = 6, [0x36] = 7, [0x37] = 8, [0x38] = 9, [0x39] = 10,

        // Letters A..Z
        [0x41] = 30, [0x42] = 48, [0x43] = 46, [0x44] = 32, [0x45] = 18,
        [0x46] = 33, [0x47] = 34, [0x48] = 35, [0x49] = 23, [0x4A] = 36,
        [0x4B] = 37, [0x4C] = 38, [0x4D] = 50, [0x4E] = 49, [0x4F] = 24,
        [0x50] = 25, [0x51] = 16, [0x52] = 19, [0x53] = 31, [0x54] = 20,
        [0x55] = 22, [0x56] = 47, [0x57] = 17, [0x58] = 45, [0x59] = 21,
        [0x5A] = 44,

        // Super / Meta / Menu
        [0x5B] = 125, // VK_LWIN -> KEY_LEFTMETA
        [0x5C] = 126, // VK_RWIN -> KEY_RIGHTMETA
        [0x5D] = 127, // VK_APPS -> KEY_COMPOSE

        // Numpad
        [0x60] = 82, [0x61] = 79, [0x62] = 80, [0x63] = 81, [0x64] = 75,
        [0x65] = 76, [0x66] = 77, [0x67] = 71, [0x68] = 72, [0x69] = 73,
        [0x6A] = 55, [0x6B] = 78, [0x6D] = 74, [0x6E] = 83, [0x6F] = 98,

        // Function keys F1..F12
        [0x70] = 59, [0x71] = 60, [0x72] = 61, [0x73] = 62, [0x74] = 63, [0x75] = 64,
        [0x76] = 65, [0x77] = 66, [0x78] = 67, [0x79] = 68, [0x7A] = 87, [0x7B] = 88,

        [0x90] = 69, // VK_NUMLOCK
        [0x91] = 70, // VK_SCROLL

        // Left / Right Modifiers
        [0xA0] = 42,  // VK_LSHIFT -> KEY_LEFTSHIFT
        [0xA1] = 54,  // VK_RSHIFT -> KEY_RIGHTSHIFT
        [0xA2] = 29,  // VK_LCONTROL -> KEY_LEFTCTRL
        [0xA3] = 97,  // VK_RCONTROL -> KEY_RIGHTCTRL
        [0xA4] = 56,  // VK_LMENU -> KEY_LEFTALT
        [0xA5] = 100, // VK_RMENU -> KEY_RIGHTALT (AltGr)

        // OEM / Punctuation (including ABNT2 ç, /, ~, ´, \)
        [0xBA] = 39,  // VK_OEM_1 (ç on ABNT2 / ; on US) -> KEY_SEMICOLON
        [0xBB] = 13,  // VK_OEM_PLUS -> KEY_EQUAL
        [0xBC] = 51,  // VK_OEM_COMMA -> KEY_COMMA
        [0xBD] = 12,  // VK_OEM_MINUS -> KEY_MINUS
        [0xBE] = 52,  // VK_OEM_PERIOD -> KEY_DOT
        [0xBF] = 53,  // VK_OEM_2 -> KEY_SLASH
        [0xC0] = 41,  // VK_OEM_3 -> KEY_GRAVE
        [0xC1] = 89,  // VK_ABNT_C1 (/ ? on ABNT2) -> KEY_RO
        [0xC2] = 121, // VK_ABNT_C2 -> KEY_KPCOMMA
        [0xDB] = 26,  // VK_OEM_4 -> KEY_LEFTBRACE
        [0xDC] = 43,  // VK_OEM_5 -> KEY_BACKSLASH
        [0xDD] = 27,  // VK_OEM_6 -> KEY_RIGHTBRACE
        [0xDE] = 40,  // VK_OEM_7 -> KEY_APOSTROPHE
        [0xE2] = 86,  // VK_OEM_102 (\ | on ABNT2) -> KEY_102ND

        // Media keys
        [0xAD] = 113, // VK_VOLUME_MUTE
        [0xAE] = 114, // VK_VOLUME_DOWN
        [0xAF] = 115, // VK_VOLUME_UP
        [0xB0] = 163, // VK_MEDIA_NEXT_TRACK
        [0xB1] = 165, // VK_MEDIA_PREV_TRACK
        [0xB3] = 164, // VK_MEDIA_PLAY_PAUSE
    };

    private static readonly Dictionary<ushort, ushort> EvdevToVk;

    static LinuxKeyMap()
    {
        EvdevToVk = [];
        foreach (var kv in VkToEvdev)
            EvdevToVk.TryAdd(kv.Value, kv.Key);
    }

    public static ushort ToEvdevCode(ushort windowsVk) =>
        VkToEvdev.TryGetValue(windowsVk, out var code) ? code : (ushort)0;

    public static ushort FromEvdevCode(ushort evdevCode) =>
        EvdevToVk.TryGetValue(evdevCode, out var vk) ? vk : (ushort)0;

    /// <summary>
    /// Converts a Windows VK code to X11 hardware keycode (Xorg evdev rule: x11_keycode = evdev_code + 8).
    /// </summary>
    public static uint ToX11KeyCode(ushort windowsVk)
    {
        var ev = ToEvdevCode(windowsVk);
        return ev > 0 ? (uint)(ev + 8) : 0u;
    }

    public static ushort FromX11KeyCode(uint x11KeyCode) =>
        x11KeyCode >= 8 ? FromEvdevCode((ushort)(x11KeyCode - 8)) : (ushort)0;

    public static IEnumerable<ushort> AllEvdevKeyCodes => VkToEvdev.Values.Distinct();
}
