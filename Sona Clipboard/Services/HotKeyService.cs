using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using WinRT.Interop;

namespace Sona_Clipboard.Services
{
    public class HotKeyService : IDisposable
    {
        private readonly IntPtr _hWnd;
        private SubclassProc _subclassDelegate;
        private bool _disposed = false;

        private const int WM_HOTKEY = 0x0312;
        public const int ID_NEXT = 9001;
        public const int ID_PREV = 9002;

        public event Action<int>? HotKeyPressed;

        public HotKeyService(IntPtr hWnd)
        {
            _hWnd = hWnd;
            _subclassDelegate = new SubclassProc(WndProc);
            SetWindowSubclass(_hWnd, _subclassDelegate, 0, IntPtr.Zero);
        }

        public bool Register(int id, uint modifiers, uint key, out int error)
        {
            HotKeyHelper.Unregister(_hWnd, id);
            return HotKeyHelper.Register(_hWnd, id, modifiers, key, out error);
        }

        public void Unregister(int id)
        {
            HotKeyHelper.Unregister(_hWnd, id);
        }

        private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
        {
            if (uMsg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                HotKeyPressed?.Invoke(id);
                return IntPtr.Zero;
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            Unregister(ID_NEXT);
            Unregister(ID_PREV);
            RemoveWindowSubclass(_hWnd, _subclassDelegate, 0);
        }

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);
    }
}
