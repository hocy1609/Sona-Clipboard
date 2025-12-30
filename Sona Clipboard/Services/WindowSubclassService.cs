using System;
using System.Runtime.InteropServices;
using Sona_Clipboard.Services.Interfaces;

namespace Sona_Clipboard.Services
{
    public class WindowSubclassService : IWindowSubclassService
    {
        private readonly ILogger _logger;
        private readonly IHotKeyManager _hotKeyManager;
        private IntPtr _hWnd;
        private SubclassProc? _subclassProc;
        private bool _isHooked;

        public WindowSubclassService(ILogger logger, IHotKeyManager hotKeyManager)
        {
            _logger = logger;
            _hotKeyManager = hotKeyManager;
        }

        public void Hook(IntPtr hWnd)
        {
            if (_isHooked)
            {
                _logger.LogWarning("WindowSubclassService is already hooked.");
                return;
            }

            _hWnd = hWnd;
            _subclassProc = new SubclassProc(WndProc);

            // Subclass ID can be 0 or any unique ID. Using 101 just to be specific.
            if (SetWindowSubclass(_hWnd, _subclassProc, 101, IntPtr.Zero))
            {
                _isHooked = true;
                _logger.LogInfo($"Window subclass hooked for hWnd: {_hWnd}");
            }
            else
            {
                _logger.LogError($"Failed to hook window subclass for hWnd: {_hWnd}");
            }
        }

        public void Unhook()
        {
            if (!_isHooked || _hWnd == IntPtr.Zero) return;

            if (RemoveWindowSubclass(_hWnd, _subclassProc, 101))
            {
                _logger.LogInfo($"Window subclass unhooked for hWnd: {_hWnd}");
            }
            else
            {
                _logger.LogWarning($"Failed to unhook window subclass for hWnd: {_hWnd}");
            }

            _isHooked = false;
            _subclassProc = null;
        }

        private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
        {
            const uint WM_HOTKEY = 0x0312;

            if (uMsg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                try
                {
                    _hotKeyManager.RaiseHotKeyPressed(hotkeyId);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error raising hotkey event for ID {hotkeyId}", ex);
                }
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        public void Dispose()
        {
            Unhook();
            GC.SuppressFinalize(this);
        }

        // P/Invoke Definitions

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc? pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc? pfnSubclass, uint uIdSubclass);
    }
}
