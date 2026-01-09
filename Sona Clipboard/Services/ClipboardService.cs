using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Sona_Clipboard.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Sona_Clipboard.Services
{
    public class ClipboardService : IDisposable
    {
        #region Win32 API Declarations


        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern uint GetClipboardSequenceNumber();

        // Win32 Clipboard Listener API (recommended by Microsoft, used by CopyQ)
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        // Hidden window creation
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetModuleHandle(string? lpModuleName);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASS
        {
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        private const uint WM_CLIPBOARDUPDATE = 0x031D;
        private const int HWND_MESSAGE = -3; // Message-only window parent

        #endregion


        private volatile bool _isCopiedByMe = false;
        private uint _lastSequenceNumber = 0;
        private Timer? _pollingTimer;
        private volatile bool _isProcessing = false;
        private DateTime _lastProcessedTime = DateTime.MinValue;

        private IntPtr _listenerHwnd = IntPtr.Zero;
        private WndProcDelegate? _wndProcDelegate; // Must keep reference to prevent GC
        private bool _disposed = false;

        // DispatcherQueue for marshaling WinRT calls to UI thread

        private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        public event Action<ClipboardItem>? ClipboardChanged;

        public ClipboardService()
        {
            _lastSequenceNumber = GetClipboardSequenceNumber();

            // Capture the current DispatcherQueue (must be called from UI thread during init)

            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // Primary: Win32 Clipboard Listener (most reliable, like CopyQ)
            try
            {
                CreateClipboardListenerWindow();
                LogService.Info("Win32 clipboard listener registered successfully");
            }
            catch (Exception ex)
            {
                LogService.Error("Failed to create Win32 clipboard listener, falling back to polling only", ex);
            }

            // Backup: Polling timer every 2 seconds (catches anything Win32 might miss)
            _pollingTimer = new Timer(CheckClipboardByPolling, null, 2000, 2000);
        }

        private void CreateClipboardListenerWindow()
        {
            _wndProcDelegate = WndProc;

            var wc = new WNDCLASS
            {
                lpfnWndProc = _wndProcDelegate,
                hInstance = GetModuleHandle(null),
                lpszClassName = "SonaClipboardListener_" + Guid.NewGuid().ToString("N")
            };

            ushort atom = RegisterClass(ref wc);
            if (atom == 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"RegisterClass failed with error {error}");
            }

            // Create message-only window (invisible, doesn't appear in taskbar)
            _listenerHwnd = CreateWindowEx(
                0, wc.lpszClassName, "SonaClipboardListener",
                0, 0, 0, 0, 0,
                new IntPtr(HWND_MESSAGE), IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (_listenerHwnd == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CreateWindowEx failed with error {error}");
            }

            // Register as clipboard listener
            if (!AddClipboardFormatListener(_listenerHwnd))
            {
                int error = Marshal.GetLastWin32Error();
                DestroyWindow(_listenerHwnd);
                _listenerHwnd = IntPtr.Zero;
                throw new InvalidOperationException($"AddClipboardFormatListener failed with error {error}");
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                LogService.Debug("WM_CLIPBOARDUPDATE received");

                if (!_isCopiedByMe && !_isProcessing)
                {
                    _lastSequenceNumber = GetClipboardSequenceNumber();

                    // Marshal to UI thread - WinRT Clipboard API requires UI thread context

                    if (_dispatcherQueue != null)
                    {
                        _dispatcherQueue.TryEnqueue(async () =>

                        {
                            await Task.Delay(30); // Small delay for clipboard to be ready
                            await ProcessClipboardContentAsync();
                        });
                    }
                    else
                    {
                        // Fallback if no dispatcher (shouldn't happen)
                        _ = ProcessClipboardContentAsync();
                    }
                }
                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }


        /// <summary>
        /// Polling механизм: проверяем GetClipboardSequenceNumber каждые 400мс
        /// </summary>
        private void CheckClipboardByPolling(object? state)
        {
            try
            {
                uint currentSeq = GetClipboardSequenceNumber();

                // Если sequence number изменился — буфер точно обновился

                if (currentSeq != _lastSequenceNumber && !_isCopiedByMe && !_isProcessing)
                {
                    _lastSequenceNumber = currentSeq;
                    _ = ProcessClipboardContentAsync();
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Clipboard polling error", ex);
            }
        }

        private (string AppName, string ProcessName) GetActiveProcessInfo()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return ("Unknown", "Unknown");

                uint processId;
                GetWindowThreadProcessId(hWnd, out processId);

                using (var process = Process.GetProcessById((int)processId))
                {
                    string processName = process.ProcessName;
                    // Try to get a more friendly name (Main window title)
                    string appName = string.IsNullOrWhiteSpace(process.MainWindowTitle)
                        ? processName
                        : process.MainWindowTitle;

                    // Cleanup title (usually apps have 'Title - AppName' or just 'AppName')
                    if (appName.Contains(" - "))
                        appName = appName.Split(" - ").Last();

                    return (appName, processName);
                }
            }
            catch { return ("Unknown", "Unknown"); }
        }

        #region Security Filters (CopyQ-inspired)

        // Максимальный размер текста (1MB)

        private const int MAX_TEXT_SIZE = 1_048_576;

        // Процессы менеджеров паролей — не записывать их копирования

        private static readonly string[] PasswordManagerProcesses =
        {

            "1password", "bitwarden", "keepass", "keepassxc", "lastpass",

            "dashlane", "roboform", "enpass", "nordpass", "passwarden"
        };

        /// <summary>
        /// Проверяет, помечено ли содержимое буфера как "скрытое" (пароли, конфиденциальные данные).
        /// Приложения (например, менеджеры паролей) могут устанавливать специальные флаги.
        /// </summary>
        private bool IsHiddenClipboard(DataPackageView view)
        {
            try
            {
                var formats = view.AvailableFormats;

                // Windows standard flags for hidden/secret clipboard content

                return formats.Any(f =>

                    f.Contains("Clipboard Viewer Ignore") ||
                    f.Contains("ExcludeClipboardContentFromMonitorProcessing") ||
                    f.Contains("CanIncludeInClipboardHistory") || // When set to 0
                    f.Contains("CanUploadToCloudClipboard"));     // When set to 0
            }
            catch { return false; }
        }

        /// <summary>
        /// Проверяет, скопировано ли из менеджера паролей.
        /// </summary>
        private bool IsFromPasswordManager(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            string lower = processName.ToLowerInvariant();
            return PasswordManagerProcesses.Any(pm => lower.Contains(pm));
        }

        /// <summary>
        /// Проверяет валидность текстового контента (минимум 2 непробельных символа).
        /// </summary>
        private bool IsValidTextContent(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            // Минимум 2 непробельных символа
            return text.Count(c => !char.IsWhiteSpace(c)) >= 2;
        }

        /// <summary>
        /// Обрезает текст если превышает лимит.
        /// </summary>
        private string TruncateIfNeeded(string text)
        {
            if (text.Length > MAX_TEXT_SIZE)
            {
                return text.Substring(0, MAX_TEXT_SIZE) + "\n\n... [обрезано — превышен лимит 1MB]";
            }
            return text;
        }

        #endregion


        /// <summary>
        /// Общий метод обработки содержимого буфера обмена.
        /// Вызывается либо из события ContentChanged, либо из polling.
        /// </summary>
        private async Task ProcessClipboardContentAsync()
        {
            if (_isProcessing)

            {
                LogService.Debug("Skipping: already processing");
                return;
            }

            // Debounce: пропускаем если обработка была менее 500мс назад
            var timeSinceLastProcess = (DateTime.Now - _lastProcessedTime).TotalMilliseconds;
            if (timeSinceLastProcess < 500)

            {
                LogService.Debug($"Skipping: debounce ({timeSinceLastProcess:F0}ms since last)");
                return;
            }

            LogService.Debug("Processing clipboard content...");
            _isProcessing = true;
            _lastProcessedTime = DateTime.Now;

            try
            {
                var (appName, processName) = GetActiveProcessInfo();
                LogService.Debug($"Processing from app: {appName} ({processName})");

                // Security Filter 1: Блокируем копирования из менеджеров паролей
                if (IsFromPasswordManager(processName))

                {
                    LogService.Debug("Skipping: password manager");
                    return;
                }

                DataPackageView? view = null;
                // Retry with exponential backoff
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        view = Clipboard.GetContent();
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogService.Debug($"Clipboard.GetContent attempt {i + 1} failed: {ex.Message}");
                        await Task.Delay(10 * (int)Math.Pow(2, i));
                    }
                }

                if (view == null)

                {
                    LogService.Debug("Failed to get clipboard content after retries");
                    return;
                }

                // Log available formats for debugging
                var formats = view.AvailableFormats.ToList();
                LogService.Debug($"Available formats: {string.Join(", ", formats)}");

                // Security Filter 2: Проверяем флаги "скрытого" контента
                if (IsHiddenClipboard(view))

                {
                    LogService.Debug("Skipping: hidden clipboard content");
                    return;
                }


                if (view.Contains(StandardDataFormats.StorageItems))
                {
                    var storageItems = await view.GetStorageItemsAsync();
                    if (storageItems.Count > 0)
                    {
                        var paths = new List<string>();
                        long totalSize = 0;
                        string extensions = "";

                        foreach (var item in storageItems)
                        {
                            if (!string.IsNullOrEmpty(item.Path))
                            {
                                paths.Add(item.Path);
                                try
                                {
                                    var info = new FileInfo(item.Path);
                                    if (info.Exists)
                                    {
                                        totalSize += info.Length;
                                        extensions += info.Extension.ToLower() + " ";
                                    }
                                }
                                catch { }
                            }
                        }


                        if (paths.Count > 0)
                        {
                            string content = string.Join(Environment.NewLine, paths);
                            ClipboardChanged?.Invoke(new ClipboardItem
                            {
                                Type = "File",
                                Content = content,
                                Timestamp = DateTime.Now.ToString("HH:mm"),
                                SourceAppName = appName,
                                SourceProcessName = processName,
                                RtfContent = $"Size:{totalSize} Ext:{extensions.Trim()}"
                            });
                            return;
                        }
                    }
                }


                if (view.Contains(StandardDataFormats.Bitmap))
                {
                    LogService.Debug($"Bitmap detected from {processName}, processing image...");
                    RandomAccessStreamReference imageStreamRef = await view.GetBitmapAsync();
                    using (IRandomAccessStreamWithContentType stream = await imageStreamRef.OpenReadAsync())
                    {
                        byte[] imageBytes = new byte[stream.Size];
                        using (DataReader reader = new DataReader(stream)) { await reader.LoadAsync((uint)stream.Size); reader.ReadBytes(imageBytes); }

                        byte[]? thumbBytes = await CreateThumbnailAsync(imageStreamRef, 100);

                        var item = new ClipboardItem
                        {
                            Type = "Image",
                            Content = "Image " + DateTime.Now.ToString("HH:mm"),
                            ImageBytes = imageBytes,
                            ThumbnailBytes = thumbBytes,
                            Timestamp = DateTime.Now.ToString("HH:mm"),
                            Thumbnail = await BytesToImage(thumbBytes ?? imageBytes),
                            SourceAppName = appName,
                            SourceProcessName = processName
                        };

                        ClipboardChanged?.Invoke(item);
                        return;
                    }
                }


                if (view.Contains(StandardDataFormats.Text))
                {
                    string text = await view.GetTextAsync();

                    // Security Filter 3: Проверка на валидность контента
                    if (!IsValidTextContent(text)) return;

                    // Security Filter 4: Обрезаем слишком длинный текст

                    text = TruncateIfNeeded(text);

                    string? rtf = view.Contains(StandardDataFormats.Rtf) ? await view.GetRtfAsync() : null;
                    string? html = view.Contains(StandardDataFormats.Html) ? await view.GetHtmlFormatAsync() : null;

                    LogService.Info($"Clipboard: invoking ClipboardChanged for text ({text.Length} chars)");


                    ClipboardChanged?.Invoke(new ClipboardItem
                    {
                        Type = "Text",
                        Content = text,
                        RtfContent = rtf,
                        HtmlContent = html,
                        Timestamp = DateTime.Now.ToString("HH:mm"),
                        SourceAppName = appName,
                        SourceProcessName = processName
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Clipboard processing error", ex);
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private async Task<byte[]?> CreateThumbnailAsync(RandomAccessStreamReference imageRef, uint pixelSize)
        {
            try
            {
                using (var stream = await imageRef.OpenReadAsync())
                {
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                    using (var memoryStream = new InMemoryRandomAccessStream())
                    {
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, memoryStream);
                        encoder.SetSoftwareBitmap(softwareBitmap);
                        encoder.BitmapTransform.ScaledWidth = pixelSize;
                        encoder.BitmapTransform.ScaledHeight = (uint)(decoder.OrientedPixelHeight * pixelSize / decoder.OrientedPixelWidth);
                        encoder.BitmapTransform.InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
                        await encoder.FlushAsync();

                        byte[] bytes = new byte[memoryStream.Size];
                        using (var reader = new DataReader(memoryStream))
                        {
                            await reader.LoadAsync((uint)memoryStream.Size);
                            reader.ReadBytes(bytes);
                        }
                        return bytes;
                    }
                }
            }
            catch { return null; }
        }

        public async Task CopyToClipboard(ClipboardItem item)
        {
            if (item == null) return;
            _isCopiedByMe = true;


            try
            {
                DataPackage package = new DataPackage();
                package.RequestedOperation = DataPackageOperation.Copy;

                if (item.Type == "Text" && item.Content != null)
                {
                    package.SetText(item.Content);
                    if (!string.IsNullOrEmpty(item.RtfContent)) package.SetRtf(item.RtfContent);
                    if (!string.IsNullOrEmpty(item.HtmlContent)) package.SetHtmlFormat(item.HtmlContent);
                }
                else if (item.Type == "Image" && item.ImageBytes != null)
                {
                    InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(item.ImageBytes.AsBuffer());
                    stream.Seek(0);
                    package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                }
                else if (item.Type == "File" && item.Content != null)
                {
                    string[] paths = item.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    List<IStorageItem> storageItems = new List<IStorageItem>();
                    foreach (string path in paths)
                    {
                        try
                        {
                            if (System.IO.File.Exists(path))
                                storageItems.Add(await StorageFile.GetFileFromPathAsync(path));
                            else if (System.IO.Directory.Exists(path))
                                storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                        }
                        catch { }
                    }
                    if (storageItems.Count > 0) package.SetStorageItems(storageItems);
                }

                Clipboard.SetContent(package);

                // Flush с ретраем — может падать если буфер занят

                for (int i = 0; i < 3; i++)
                {
                    try
                    {

                        Clipboard.Flush();
                        break;
                    }
                    catch
                    {

                        if (i < 2) await Task.Delay(50 * (i + 1));

                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Copy error", ex);
            }
            finally
            {
                // Сбрасываем флаг через небольшую задержку чтобы polling не подхватил наше же копирование
                _ = Task.Delay(100).ContinueWith(_ => _isCopiedByMe = false);
            }
        }

        public static async Task<BitmapImage?> BytesToImage(byte[]? bytes)
        {
            if (bytes == null) return null;
            try
            {
                using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    BitmapImage image = new BitmapImage();
                    await image.SetSourceAsync(stream);
                    return image;
                }
            }
            catch { return null; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pollingTimer?.Dispose();
            _pollingTimer = null;

            if (_listenerHwnd != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_listenerHwnd);
                DestroyWindow(_listenerHwnd);
                _listenerHwnd = IntPtr.Zero;
            }

            LogService.Info("ClipboardService disposed");
        }
    }
}