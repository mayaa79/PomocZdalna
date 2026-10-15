using System.Runtime.InteropServices;
using PomocZdalna.Shared;

namespace PomocZdalna.App.Host;

/// <summary>Wstrzykuje zdarzenia myszy i klawiatury (SendInput) na komputerze klienta.</summary>
public static class InputInjector
{
    private static readonly HashSet<int> Extended = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home strzałki
        0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x6F, 0x90,       // PrtSc Ins Del LWin RWin Num/ NumLock
        0xA3, 0xA5                                      // prawy Ctrl, prawy Alt
    };

    private static readonly HashSet<int> Pressed = new();
    private static readonly object Gate = new();

    public static void Mouse(Msg m)
    {
        double x = Math.Clamp(m.X ?? 0, 0, 1);
        double y = Math.Clamp(m.Y ?? 0, 0, 1);
        int ax = (int)(x * 65535), ay = (int)(y * 65535);
        int btn = m.Btn ?? 0;

        switch (m.Kind)
        {
            case "move":
                Send(MouseInput(ax, ay, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE, 0));
                break;
            case "down":
                Send(MouseInput(ax, ay, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE, 0));
                Send(MouseInput(0, 0, btn switch { 1 => Native.MOUSEEVENTF_RIGHTDOWN, 2 => Native.MOUSEEVENTF_MIDDLEDOWN, _ => Native.MOUSEEVENTF_LEFTDOWN }, 0));
                break;
            case "up":
                Send(MouseInput(ax, ay, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE, 0));
                Send(MouseInput(0, 0, btn switch { 1 => Native.MOUSEEVENTF_RIGHTUP, 2 => Native.MOUSEEVENTF_MIDDLEUP, _ => Native.MOUSEEVENTF_LEFTUP }, 0));
                break;
            case "wheel":
                Send(MouseInput(0, 0, Native.MOUSEEVENTF_WHEEL, unchecked((uint)(m.Delta ?? 0))));
                break;
        }
    }

    public static void Key(Msg m)
    {
        int vk = m.Vk ?? 0;
        if (vk <= 0 || vk > 0xFE) return;
        bool down = m.Down ?? true;

        lock (Gate) { if (down) Pressed.Add(vk); else Pressed.Remove(vk); }
        Send(KeyInput(vk, down));
    }

    /// <summary>Zwalnia wciśnięte klawisze – na wypadek zerwania sesji z przytrzymanym Ctrl/Alt/Shift.</summary>
    public static void ReleaseAll()
    {
        int[] keys;
        lock (Gate) { keys = Pressed.ToArray(); Pressed.Clear(); }
        foreach (var vk in keys) Send(KeyInput(vk, false));
    }

    private static Native.INPUT MouseInput(int dx, int dy, uint flags, uint data) => new()
    {
        type = Native.INPUT_MOUSE,
        U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = data } }
    };

    private static Native.INPUT KeyInput(int vk, bool down)
    {
        uint flags = down ? 0u : Native.KEYEVENTF_KEYUP;
        if (Extended.Contains(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        return new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)Native.MapVirtualKey((uint)vk, 0), dwFlags = flags }
            }
        };
    }

    private static void Send(Native.INPUT input) =>
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
}
