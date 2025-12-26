using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Sona_Clipboard
{
    public sealed partial class PreviewWindow : Window
    {
        private AppWindow _appWindow;

        // Размеры окна
        private const int WIN_WIDTH = 300;
        private const int WIN_HEIGHT = 220;

        public PreviewWindow()
        {
            this.InitializeComponent();

            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            var presenter = OverlappedPresenter.Create();
            // Настраиваем окно без рамок
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
            _appWindow.SetPresenter(presenter);

            _appWindow.Resize(new Windows.Graphics.SizeInt32(WIN_WIDTH, WIN_HEIGHT));
        }

        // Этот метод нужен, чтобы главное окно могло скрывать превью
        public new void Hide()
        {
            _appWindow.Hide();
        }

        public async void ShowItem(ClipboardItem item, int index)
        {
            IndexText.Text = $"Клип #{index} ({item.Timestamp})";

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

        // Умное позиционирование (над/под и слева/справа от курсора)
        private void SmartMoveToCursor()
        {
            GetCursorPos(out POINT lpPoint);

            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            DisplayArea displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(lpPoint.X, lpPoint.Y), DisplayAreaFallback.Nearest);

            // Получаем границы рабочего экрана
            int screenW = displayArea.WorkArea.Width;
            int screenH = displayArea.WorkArea.Height;
            int screenX = displayArea.WorkArea.X;
            int screenY = displayArea.WorkArea.Y;

            // Базовая позиция (справа-снизу от мышки с отступом 20px)
            int targetX = lpPoint.X + 20;
            int targetY = lpPoint.Y + 20;

            // --- ПРОВЕРКА ПО ГОРИЗОНТАЛИ (X) ---
            // Если правый край окна вылезает за экран
            if (targetX + WIN_WIDTH > screenX + screenW)
            {
                // Ставим окно СЛЕВА от курсора
                targetX = lpPoint.X - WIN_WIDTH - 20;
            }

            // --- ПРОВЕРКА ПО ВЕРТИКАЛИ (Y) ---
            // Если нижний край окна вылезает за экран
            if (targetY + WIN_HEIGHT > screenY + screenH)
            {
                // Ставим окно СВЕРХУ от курсора
                targetY = lpPoint.Y - WIN_HEIGHT - 20;
            }

            // Финальная страховка (чтобы не улетело в минус за левый/верхний край)
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