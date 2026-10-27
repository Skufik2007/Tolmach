using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace Tolmach;

// System-wide hotkeys through RegisterHotKey on a message-only window.
internal sealed class Hotkeys : IDisposable
{
    readonly HwndSource _window;
    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    // Hotkeys another program had already taken.
    public List<string> Failed { get; } = new();

    public Hotkeys()
    {
        _window = new HwndSource(new HwndSourceParameters("Tolmach.Hotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
    }

    public bool Register(string spec, Action action, bool repeat)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return true;
        }
        if (!TryParse(spec, out uint modifiers, out uint vk))
        {
            Log.Write($"Hotkey {spec}: can't parse");
            Failed.Add(spec);
            return false;
        }
        if (!repeat)
        {
            modifiers |= Native.MOD_NOREPEAT;
        }
        int id = _nextId++;
        if (!Native.RegisterHotKey(_window.Handle, id, modifiers, vk))
        {
            Log.Write($"Hotkey {spec}: taken (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            Failed.Add(spec);
            return false;
        }
        _actions[id] = action;
        return true;
    }

    // "Ctrl+Alt+S", "Win+Shift+F9", "Ctrl+Alt+Up"…
    public static bool TryParse(string spec, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (string raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= Native.MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= Native.MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= Native.MOD_SHIFT;
                    continue;
                case "win":
                    modifiers |= Native.MOD_WIN;
                    continue;
            }
            string name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            // The key left of 1: Ё on a Russian layout, ` and ~ on an English one.
            if (raw is "Ё" or "ё" or "`" or "~" || raw.Equals("Tilde", StringComparison.OrdinalIgnoreCase))
            {
                name = nameof(Key.OemTilde);
            }
            if (!Enum.TryParse(name, ignoreCase: true, out Key key) || key == Key.None)
            {
                return false;
            }
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        return vk != 0 && modifiers != 0;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Write("Hotkey action failed: " + ex);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys)
        {
            Native.UnregisterHotKey(_window.Handle, id);
        }
        _window.Dispose();
    }
}
