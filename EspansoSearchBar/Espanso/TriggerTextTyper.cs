// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;

namespace EspansoSearchBar.Espanso;

/// <summary>
/// Types text into the currently focused window using SendInput with KEYEVENTF_UNICODE events.
///
/// Why this exists: "espanso match exec -t &lt;trigger&gt;" makes the espanso engine emit a
/// TriggerCompensationEvent (espanso-engine/src/process/middleware/cause.rs), which sends one
/// Backspace per trigger character (action.rs: trigger.chars().count()) before injecting the
/// replacement - espanso assumes the trigger was physically typed in the target window. When
/// invoked from this extension nothing was typed, so those backspaces would eat the user's
/// existing text. (Espanso's own search bar avoids this internally by selecting matches with
/// trigger: None - search.rs - but that path is not reachable via CLI/IPC.)
///
/// The fix: right before calling "match exec" we type one space per trigger character into
/// the focused window, so the backspace compensation deletes exactly that padding and the
/// user's text stays intact. Spaces (rather than the trigger text itself) are used so that
/// nothing we type can ever be matched as a trigger by espanso - not even on setups with
/// win32_exclude_orphan_events: false, where espanso's detector would otherwise see our
/// SendInput-generated keystrokes and expand the match a second time.
/// </summary>
internal static partial class TriggerTextTyper
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventFUnicode = 0x0004;
    private const uint KeyEventFKeyUp = 0x0002;

    /// <summary>
    /// Types one space per trigger character into the focused window, matching the number of
    /// Backspace events espanso's trigger compensation will send for this trigger. Espanso
    /// counts Unicode scalar values (Rust's trigger.chars().count()), which is what
    /// <see cref="System.Globalization.StringInfo"/>-independent rune enumeration gives us.
    /// Returns false if SendInput reported that not all events were injected (e.g. blocked
    /// by UIPI when an elevated window has focus).
    /// </summary>
    internal static bool TypePaddingFor(string trigger)
    {
        var scalarCount = 0;
        foreach (var _ in trigger.EnumerateRunes())
        {
            scalarCount++;
        }

        return TypeText(new string(' ', scalarCount));
    }

    /// <summary>
    /// Sends the given text as a sequence of Unicode key-down/key-up pairs to the focused
    /// window. Returns false if SendInput reported that not all events were injected
    /// (e.g. blocked by UIPI when an elevated window has focus).
    /// </summary>
    internal static bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        // One key-down + one key-up per UTF-16 code unit; KEYEVENTF_UNICODE places the code
        // unit in wScan. Surrogate pairs are sent as two consecutive unit pairs, which is the
        // documented way to inject non-BMP characters (triggers are ASCII in practice anyway).
        var inputs = new Input[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = MakeUnicodeInput(text[i], keyUp: false);
            inputs[(i * 2) + 1] = MakeUnicodeInput(text[i], keyUp: true);
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        return sent == (uint)inputs.Length;
    }

    private static Input MakeUnicodeInput(char codeUnit, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = 0,
                ScanCode = codeUnit,
                Flags = keyUp ? KeyEventFUnicode | KeyEventFKeyUp : KeyEventFUnicode,
                Time = 0,
                ExtraInfo = 0,
            },
        },
    };

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    // The union must include the largest member (MOUSEINPUT) so Marshal.SizeOf reports the
    // exact native sizeof(INPUT); SendInput rejects calls with a wrong cbSize.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
