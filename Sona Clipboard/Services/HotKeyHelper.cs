using System;
using System.Runtime.InteropServices;

namespace Sona_Clipboard.Services
{
    public static class HotKeyHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Константы модификаторов
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public static bool Register(IntPtr hWnd, int id, uint modifiers, uint key)
        {
            return RegisterHotKey(hWnd, id, modifiers, key);
        }

        public static bool Unregister(IntPtr hWnd, int id)
        {
            return UnregisterHotKey(hWnd, id);
        }
    }
}