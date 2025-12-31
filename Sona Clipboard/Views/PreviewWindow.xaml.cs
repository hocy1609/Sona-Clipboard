using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using WinRT.Interop;
using System.Threading.Tasks;

using Sona_Clipboard.Models;

namespace Sona_Clipboard.Views
{
    public sealed partial class PreviewWindow : Window
    {
        private AppWindow _appWindow;
        private int _currentWidth = 400;
        private int _currentHeight = 300;

        public new bool Visible => _appWindow != null && _appWindow.IsVisible;

        public PreviewWindow()
        {
            this.InitializeComponent();

            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
            _appWindow.SetPresenter(presenter);

            _appWindow.Resize(new Windows.Graphics.SizeInt32(_currentWidth, _currentHeight));
        }

        public void Hide()
        {
            _appWindow.Hide();
        }

        public async void ShowItem(ClipboardItem item, int index)
        {
            IndexText.Text = $"Clip #{index} ({item.Timestamp})";
            
            // Default sizes
            int targetWidth = 400;
            int targetHeight = 300;

            try
            {
                if (item.Type == "Image" && item.ImageBytes != null)
                {
                    TextScroll.Visibility = Visibility.Collapsed;
                    SvgViewbox.Visibility = Visibility.Collapsed;
                    ImageViewbox.Visibility = Visibility.Visible;

                    using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
                    {
                        await stream.WriteAsync(item.ImageBytes.AsBuffer());
                        stream.Seek(0);
                        
                        BitmapImage bitmap = new BitmapImage();
                        bitmap.AutoPlay = true; 
                        await bitmap.SetSourceAsync(stream);
                        PreviewImage.Source = bitmap;

                        // Adaptive aspect ratio
                        double aspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
                        if (aspect > 1.5) { targetWidth = 500; targetHeight = (int)(500 / aspect); }
                        else if (aspect < 0.7) { targetHeight = 500; targetWidth = (int)(500 * aspect); }
                        else { targetWidth = 450; targetHeight = (int)(450 / aspect); }
                    }
                }
                else
                {
                    ImageViewbox.Visibility = Visibility.Collapsed;
                    SvgViewbox.Visibility = Visibility.Collapsed;
                    TextScroll.Visibility = Visibility.Visible;
                    PreviewText.Text = item.Content ?? "";
                    
                    // Adaptive height for text
                    int lineCount = (item.Content ?? "").Split('\n').Length;
                    targetHeight = Math.Clamp(120 + (lineCount * 22), 180, 500);
                    targetWidth = 450;
                }
            }
            catch { }

            _currentWidth = targetWidth;
            _currentHeight = targetHeight;
            _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));

            SmartMoveToCursor();
            _appWindow.Show();
        }

        private void SmartMoveToCursor()
        {
            GetCursorPos(out POINT lpPoint);

            DisplayArea displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(lpPoint.X, lpPoint.Y), DisplayAreaFallback.Nearest);

            int screenW = displayArea.WorkArea.Width;
            int screenH = displayArea.WorkArea.Height;
            int screenX = displayArea.WorkArea.X;
            int screenY = displayArea.WorkArea.Y;

            int targetX = lpPoint.X + 20;
            int targetY = lpPoint.Y + 20;

            if (targetX + _currentWidth > screenX + screenW) targetX = lpPoint.X - _currentWidth - 20;
            if (targetY + _currentHeight > screenY + screenH) targetY = lpPoint.Y - _currentHeight - 20;

            if (targetX < screenX) targetX = screenX;
            if (targetY < screenY) targetY = screenY;

            _appWindow.Move(new Windows.Graphics.PointInt32(targetX, targetY));
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }
    }
}
