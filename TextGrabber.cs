using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Tolmach;

// Gets the selected text of whatever program is in front: presses Ctrl+C
// for the user and puts their clipboard back afterwards.
internal static class TextGrabber
{
    public static async Task<string?> GrabSelectionAsync()
    {
        // The hotkey's own keys are still held: Ctrl+Alt+C would reach the
        // program instead of Ctrl+C. Wait for them to come up, then let go of
        // whatever is still down ourselves.
        for (int i = 0; i < 25 && AnyModifierDown(); i++)
        {
            await Task.Delay(20);
        }
        ReleaseModifiers();

        IDataObject? saved = SaveClipboard();
        uint before = Native.GetClipboardSequenceNumber();
        Native.SendKeys((Native.VK_CONTROL, false), (Native.VK_C, false), (Native.VK_C, true), (Native.VK_CONTROL, true));

        // Programs take a moment to answer; some never do (games).
        bool changed = false;
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(20);
            if (Native.GetClipboardSequenceNumber() != before)
            {
                changed = true;
                break;
            }
        }
        if (!changed)
        {
            return null;
        }

        // Some programs fill the clipboard in several steps.
        await Task.Delay(40);
        string? text = ReadText();
        if (saved != null)
        {
            RestoreClipboard(saved);
        }
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static bool AnyModifierDown() =>
        Native.IsDown(Native.VK_MENU) || Native.IsDown(Native.VK_SHIFT) ||
        Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN) || Native.IsDown(Native.VK_CONTROL);

    static void ReleaseModifiers()
    {
        var keys = new List<(ushort, bool)>();
        // A lone Alt or Win release would open the menu bar or Start: an
        // unassigned key in between makes it a combination.
        if (Native.IsDown(Native.VK_MENU) || Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN))
        {
            keys.Add((0xE8, false));
            keys.Add((0xE8, true));
        }
        foreach (ushort vk in new[] { Native.VK_MENU, Native.VK_SHIFT, Native.VK_LWIN, Native.VK_RWIN, Native.VK_CONTROL })
        {
            if (Native.IsDown(vk))
            {
                keys.Add((vk, true));
            }
        }
        if (keys.Count > 0)
        {
            Native.SendKeys(keys.ToArray());
        }
    }

    public static string? ReadText()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                // Another program holds the clipboard open for a moment.
                Thread.Sleep(30);
            }
        }
        return null;
    }

    // Everything that can be read back: text, pictures, files, rich text.
    static IDataObject? SaveClipboard()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current == null)
            {
                return null;
            }
            var copy = new DataObject();
            foreach (string format in current.GetFormats(false))
            {
                try
                {
                    object? data = current.GetData(format, false);
                    if (data != null)
                    {
                        copy.SetData(format, data, false);
                    }
                }
                catch
                {
                }
            }
            return copy.GetFormats().Length > 0 ? copy : null;
        }
        catch (Exception ex)
        {
            Log.Write("Saving clipboard failed: " + ex.Message);
            return null;
        }
    }

    static void RestoreClipboard(IDataObject saved)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(saved, true);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
        }
    }
}

// Ctrl+C pressed twice in a row, system-wide. A keyboard hook on its own
// thread, like any such hook should be: a busy UI never holds up typing.
internal sealed unsafe class DoubleCopyWatcher : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const uint WM_QUIT = 0x0012;
    const uint Window = 450; // ms between the two presses

    static DoubleCopyWatcher? _current;

    readonly Dispatcher _ui;
    Thread? _thread;
    uint _threadId;
    bool _cHeld;
    uint _lastCopy;

    public event Action? DoubleCopy;

    public DoubleCopyWatcher(Dispatcher ui)
    {
        _ui = ui;
    }

    public void Start()
    {
        _current = this;
        var started = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            IntPtr hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL,
                (IntPtr)(delegate* unmanaged<int, IntPtr, IntPtr, IntPtr>)&Hook, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                Log.Write("Keyboard hook failed: " + Marshal.GetLastWin32Error());
            }
            started.Set();
            while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
            Native.UnhookWindowsHookEx(hook);
        })
        {
            IsBackground = true,
            Name = "Tolmach keyboard hook",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        started.Wait(TimeSpan.FromSeconds(5));
    }

    [UnmanagedCallersOnly]
    static IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _current is { } watcher)
            {
                watcher.OnKey((int)wParam, (Native.KBDLLHOOKSTRUCT*)lParam);
            }
        }
        catch
        {
            // Nothing may leave a function called from native code.
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    void OnKey(int message, Native.KBDLLHOOKSTRUCT* data)
    {
        if (data->vkCode != Native.VK_C || data->dwExtraInfo == Native.OwnInputTag)
        {
            return;
        }
        if (message is WM_KEYUP or WM_SYSKEYUP)
        {
            _cHeld = false;
            return;
        }
        if (message is not (WM_KEYDOWN or WM_SYSKEYDOWN) || _cHeld)
        {
            return; // auto-repeat of a held key
        }
        _cHeld = true;
        bool plainCtrl = Native.IsDown(Native.VK_CONTROL) && !Native.IsDown(Native.VK_MENU) &&
                         !Native.IsDown(Native.VK_SHIFT) && !Native.IsDown(Native.VK_LWIN) && !Native.IsDown(Native.VK_RWIN);
        if (!plainCtrl)
        {
            _lastCopy = 0;
            return;
        }
        if (_lastCopy != 0 && data->time - _lastCopy <= Window)
        {
            _lastCopy = 0;
            _ui.BeginInvoke(() => DoubleCopy?.Invoke());
        }
        else
        {
            _lastCopy = data->time;
        }
    }

    public void Dispose()
    {
        if (_thread != null)
        {
            Native.PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
            _thread = null;
        }
    }
}
