using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Sona_Clipboard.Services
{
    public class UIService
    {
        private readonly Window _window;
        private readonly IntPtr _hWnd;
        private readonly AppWindow _appWindow;

        public UIService(Window window)
        {
            _window = window;
            _hWnd = WindowNative.GetWindowHandle(_window);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
        }

        public void InitializeWindow(string title, string iconPath)
        {
            _window.Title = title;
            _appWindow.Title = title;

            try
            {
                var fullIconPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, iconPath);
                _appWindow.SetIcon(fullIconPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Icon Error: " + ex.Message);
            }
        }

        public void CenterWindow()
        {
            DisplayArea displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var centeredPosition = _appWindow.Position;
                centeredPosition.X = ((displayArea.WorkArea.Width - _appWindow.Size.Width) / 2);
                centeredPosition.Y = ((displayArea.WorkArea.Height - _appWindow.Size.Height) / 2);
                _appWindow.Move(centeredPosition);
            }
        }

        public void ShowWindow()
        {
            // 1. Win32 Restore
            Win32ShowWindow(_hWnd, SW_RESTORE);

            // 2. AppWindow Show
            _appWindow.Show();

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }
                presenter.SetBorderAndTitleBar(true, true);
            }

            // 3. Fallback and Activate
            SwitchToThisWindow(_hWnd, true);
            _window.Activate();
        }

        [DllImport("user32.dll", EntryPoint = "ShowWindow")]
        private static extern bool Win32ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        private const int SW_RESTORE = 9;
    }
}
