using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using WinRT.Interop;

using Sona_Clipboard.Models;

namespace Sona_Clipboard.Views
{
    public sealed partial class PreviewWindow : Window
    {
        private AppWindow _appWindow;

        // ������� ����
        private const int WIN_WIDTH = 300;
        private const int WIN_HEIGHT = 220;

        public PreviewWindow()
        {
            this.InitializeComponent();

            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            var presenter = OverlappedPresenter.Create();
            // ����������� ���� ��� �����
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
            _appWindow.SetPresenter(presenter);

            _appWindow.Resize(new Windows.Graphics.SizeInt32(WIN_WIDTH, WIN_HEIGHT));
        }

        // ���� ����� �����, ����� ������� ���� ����� �������� ������
        public void Hide()
        {
            _appWindow.Hide();
        }

        public async void ShowItem(ClipboardItem item, int index)
        {
            IndexText.Text = $"���� #{index} ({item.Timestamp})";

            if (item.Type == "Image" && item.ImageBytes != null)
            {
                PreviewText.Visibility = Visibility.Collapsed;
                PreviewImage.Visibility = Visibility.Visible;

                using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(item.ImageBytes.AsBuffer());
                    stream.Seek(0);
                    BitmapImage bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    PreviewImage.Source = bitmap;
                }
            }
            else
            {
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewText.Visibility = Visibility.Visible;
                PreviewText.Text = item.Content ?? "";
            }

            SmartMoveToCursor();
            this.Activate();
        }

        // ����� ���������������� (���/��� � �����/������ �� �������)
        private void SmartMoveToCursor()
        {
            GetCursorPos(out POINT lpPoint);

            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            DisplayArea displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(lpPoint.X, lpPoint.Y), DisplayAreaFallback.Nearest);

            // �������� ������� �������� ������
            int screenW = displayArea.WorkArea.Width;
            int screenH = displayArea.WorkArea.Height;
            int screenX = displayArea.WorkArea.X;
            int screenY = displayArea.WorkArea.Y;

            // ������� ������� (������-����� �� ����� � �������� 20px)
            int targetX = lpPoint.X + 20;
            int targetY = lpPoint.Y + 20;

            // --- �������� �� ����������� (X) ---
            // ���� ������ ���� ���� �������� �� �����
            if (targetX + WIN_WIDTH > screenX + screenW)
            {
                // ������ ���� ����� �� �������
                targetX = lpPoint.X - WIN_WIDTH - 20;
            }

            // --- �������� �� ��������� (Y) ---
            // ���� ������ ���� ���� �������� �� �����
            if (targetY + WIN_HEIGHT > screenY + screenH)
            {
                // ������ ���� ������ �� �������
                targetY = lpPoint.Y - WIN_HEIGHT - 20;
            }

            // ��������� ��������� (����� �� ������� � ����� �� �����/������� ����)
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