using Microsoft.Data.Sqlite;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;
using System.Text.Json;

namespace Sona_Clipboard
{
    public sealed partial class MainWindow : Window
    {
        private bool _isCopiedByMe = false;
        private string _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SonaClipboard.db");
        private List<ClipboardItem> _fullHistory = new List<ClipboardItem>();

        private IntPtr _hWnd;
        private AppWindow? _appWindow;

        private SubclassProc? _subclassDelegate;

        private PreviewWindow _previewWindow;

        // Индекс текущего элемента
        private int _currentPreviewIndex = 0;

        private DispatcherTimer _releaseCheckTimer;

        private const int ID_NEXT = 9001;
        private const int ID_PREV = 9002;

        public MainWindow()
        {
            this.InitializeComponent();

            _hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // --- НАЧАЛО: Установка иконки ---
            try
            {
                // Строим путь к файлу app.ico в папке Assets рядом с exe
                var iconPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");

                // Устанавливаем иконку для окна
                _appWindow.SetIcon(iconPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Не удалось загрузить иконку: " + ex.Message);
            }
            // --- КОНЕЦ: Установка иконки ---

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            CenterWindow();

            InitializeDatabase();
            LoadHistoryFromDb();

            _previewWindow = new PreviewWindow();

            _releaseCheckTimer = new DispatcherTimer();
            _releaseCheckTimer.Interval = TimeSpan.FromMilliseconds(50);
            _releaseCheckTimer.Tick += ReleaseCheckTimer_Tick;

            Clipboard.ContentChanged += Clipboard_ContentChanged;

            LoadSettings();
            SetupHotKeys();
        }

        private async void ReleaseCheckTimer_Tick(object? sender, object e)
        {
            try
            {
                bool ctrlPressed = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                bool shiftPressed = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                bool altPressed = (GetAsyncKeyState(0x12) & 0x8000) != 0;

                if (!ctrlPressed && !shiftPressed && !altPressed)
                {
                    _releaseCheckTimer.Stop();
                    if (_previewWindow.Visible && _fullHistory.Count > _currentPreviewIndex)
                    {
                        var item = _fullHistory[_currentPreviewIndex];
                        await CopyToClipboard(item);
                        _previewWindow.Hide();
                        await PasteSelection();
                    }
                }
            }
            catch { }
        }

        private async Task PasteSelection()
        {
            await Task.Delay(150);
            INPUT[] inputs = new INPUT[4];
            inputs[0] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } } };
            inputs[1] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = 0 } } };
            inputs[2] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } };
            inputs[3] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } };
            SendInput((uint)inputs.Length, inputs, INPUT.Size);
        }

        private void SetupHotKeys()
        {
            HotKeyHelper.Unregister(_hWnd, ID_NEXT);
            HotKeyHelper.Unregister(_hWnd, ID_PREV);
            RegisterHotKeysFromUI();

            if (_subclassDelegate == null)
            {
                _subclassDelegate = new SubclassProc(WndProc);
                SetWindowSubclass(_hWnd, _subclassDelegate, 0, IntPtr.Zero);
            }

            this.Closed += (s, e) => {
                HotKeyHelper.Unregister(_hWnd, ID_NEXT);
                HotKeyHelper.Unregister(_hWnd, ID_PREV);
                _previewWindow.Close();
            };
        }

        private void RegisterHotKeysFromUI()
        {
            RegisterSingleKey(ID_NEXT, HkNextCtrl, HkNextShift, HkNextAlt, HkNextKey);
            RegisterSingleKey(ID_PREV, HkPrevCtrl, HkPrevShift, HkPrevAlt, HkPrevKey);
        }

        private void RegisterSingleKey(int id, CheckBox ctrl, CheckBox shift, CheckBox alt, ComboBox keyBox)
        {
            uint mods = 0;
            if (ctrl.IsChecked == true) mods |= HotKeyHelper.MOD_CONTROL;
            if (shift.IsChecked == true) mods |= HotKeyHelper.MOD_SHIFT;
            if (alt.IsChecked == true) mods |= HotKeyHelper.MOD_ALT;
            uint vk = (uint)(0x41 + keyBox.SelectedIndex);
            HotKeyHelper.Register(_hWnd, id, mods, vk);
        }

        private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
        {
            const int WM_HOTKEY = 0x0312;
            if (uMsg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == ID_NEXT) ShowPreview(goDeeper: true);
                else if (id == ID_PREV) ShowPreview(goDeeper: false);
                return IntPtr.Zero;
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void ShowPreview(bool goDeeper)
        {
            if (_fullHistory.Count == 0) return;

            if (_previewWindow.Visible == false)
            {
                _releaseCheckTimer.Start();
            }
            else
            {
                if (goDeeper) _currentPreviewIndex++;
                else _currentPreviewIndex--;
            }

            if (_currentPreviewIndex < 0) _currentPreviewIndex = 0;
            if (_currentPreviewIndex >= _fullHistory.Count) _currentPreviewIndex = _fullHistory.Count - 1;

            var item = _fullHistory[_currentPreviewIndex];
            _previewWindow.ShowItem(item, _currentPreviewIndex + 1);
        }

        private async void Clipboard_ContentChanged(object? sender, object? e)
        {
            if (_isCopiedByMe) { _isCopiedByMe = false; return; }

            DataPackageView? view = null;
            for (int i = 0; i < 5; i++) { try { view = Clipboard.GetContent(); break; } catch { await Task.Delay(100); } }
            if (view == null) return;

            try
            {
                if (view.Contains(StandardDataFormats.Text))
                {
                    string text = await view.GetTextAsync();
                    if (_fullHistory.Count > 0 && _fullHistory[0].Content == text) return;
                    AddToHistory(new ClipboardItem { Type = "Text", Content = text, Timestamp = DateTime.Now.ToString("HH:mm") });
                }
                else if (view.Contains(StandardDataFormats.StorageItems))
                {
                    var storageItems = await view.GetStorageItemsAsync();
                    if (storageItems.Count == 0) return;

                    var paths = new List<string>();
                    foreach (var item in storageItems) if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
                    if (paths.Count == 0) return;

                    string content = string.Join(Environment.NewLine, paths);
                    if (_fullHistory.Count > 0 && _fullHistory[0].Content == content) return;

                    AddToHistory(new ClipboardItem { Type = "File", Content = content, Timestamp = DateTime.Now.ToString("HH:mm") });
                }
                else if (view.Contains(StandardDataFormats.Bitmap))
                {
                    RandomAccessStreamReference imageStreamRef = await view.GetBitmapAsync();
                    using (IRandomAccessStreamWithContentType stream = await imageStreamRef.OpenReadAsync())
                    {
                        byte[] imageBytes = new byte[stream.Size];
                        using (DataReader reader = new DataReader(stream)) { await reader.LoadAsync((uint)stream.Size); reader.ReadBytes(imageBytes); }

                        if (_fullHistory.Count > 0 && _fullHistory[0].Type == "Image" && _fullHistory[0].ImageBytes?.Length == imageBytes.Length) return;

                        AddToHistory(new ClipboardItem
                        {
                            Type = "Image",
                            Content = "Картинка " + DateTime.Now.ToString("HH:mm"),
                            ImageBytes = imageBytes,
                            Timestamp = DateTime.Now.ToString("HH:mm"),
                            Thumbnail = await BytesToImage(imageBytes)
                        });
                    }
                }
            }
            catch { }
        }

        private void AddToHistory(ClipboardItem item)
        {
            this.DispatcherQueue.TryEnqueue(() => {
                _fullHistory.Insert(0, item);
                SaveToDb(item);
                TrimHistory();
                UpdateListUI(SearchBox.Text);
                _currentPreviewIndex = 0;
            });
        }

        private async Task CopyToClipboard(ClipboardItem item)
        {
            if (item == null) return;
            _isCopiedByMe = true;
            DataPackage package = new DataPackage();
            package.RequestedOperation = DataPackageOperation.Copy;

            try
            {
                if (item.Type == "Text" && item.Content != null) package.SetText(item.Content);
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
                        if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                        {
                            try { storageItems.Add(await StorageFile.GetFileFromPathAsync(path)); } catch { }
                        }
                    }
                    if (storageItems.Count > 0) package.SetStorageItems(storageItems);
                }

                Clipboard.SetContent(package);
                if (item.Type == "Text") await Task.Delay(50); else await Task.Delay(200);
            }
            catch { }
        }

        private void UpdateListUI(string query = "")
        {
            ClipboardList.Items.Clear();
            var itemsToShow = string.IsNullOrWhiteSpace(query) ? _fullHistory : _fullHistory.Where(i => i.Content != null && i.Content.ToLower().Contains(query.ToLower())).ToList();
            foreach (var item in itemsToShow) ClipboardList.Items.Add(item);
            EmptyStatusText.Visibility = (ClipboardList.Items.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { UpdateListUI(SearchBox.Text); }

        private async void ClipboardList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ClipboardItem item)
            {
                _currentPreviewIndex = _fullHistory.IndexOf(item);
                await CopyToClipboard(item);
            }
        }

        private void InitializeDatabase()
        {
            using (var db = new SqliteConnection($"Filename={_dbPath}"))
            {
                db.Open();
                String tableCommand = "CREATE TABLE IF NOT EXISTS History (" +
                                      "Id INTEGER PRIMARY KEY, " + "Type NVARCHAR(50), " +
                                      "Content NVARCHAR(2048) NULL, " + "ImageBytes BLOB NULL, " + "Timestamp NVARCHAR(50))";
                new SqliteCommand(tableCommand, db).ExecuteReader();
            }
        }

        private void SaveToDb(ClipboardItem item)
        {
            using (var db = new SqliteConnection($"Filename={_dbPath}"))
            {
                db.Open();
                var insertCommand = new SqliteCommand("INSERT INTO History VALUES (NULL, @Type, @Content, @ImageBytes, @Timestamp)", db);
                insertCommand.Parameters.AddWithValue("@Type", item.Type);
                insertCommand.Parameters.AddWithValue("@Content", (object?)item.Content ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@ImageBytes", (object?)item.ImageBytes ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@Timestamp", item.Timestamp);
                insertCommand.ExecuteReader();
            }
        }

        private async void LoadHistoryFromDb()
        {
            _fullHistory.Clear();
            using (var db = new SqliteConnection($"Filename={_dbPath}"))
            {
                db.Open();
                var selectCommand = new SqliteCommand("SELECT * FROM History ORDER BY Id DESC LIMIT 50", db);
                var query = selectCommand.ExecuteReader();
                while (query.Read())
                {
                    var item = new ClipboardItem
                    {
                        Id = query.GetInt32(0),
                        Type = query.GetString(1),
                        Timestamp = query.GetString(4)
                    };
                    if (!query.IsDBNull(2)) item.Content = query.GetString(2);
                    if (!query.IsDBNull(3))
                    {
                        item.ImageBytes = (byte[])query["ImageBytes"];
                        item.Thumbnail = await BytesToImage(item.ImageBytes);
                        if (item.Content == null) item.Content = "Изображение";
                    }
                    _fullHistory.Add(item);
                }
            }
            UpdateListUI();
        }

        private void SaveSettings()
        {
            try
            {
                var data = new AppSettingsData
                {
                    HkNextCtrl = HkNextCtrl.IsChecked == true,
                    HkNextShift = HkNextShift.IsChecked == true,
                    HkNextAlt = HkNextAlt.IsChecked == true,
                    HkNextKey = HkNextKey.SelectedIndex,

                    HkPrevCtrl = HkPrevCtrl.IsChecked == true,
                    HkPrevShift = HkPrevShift.IsChecked == true,
                    HkPrevAlt = HkPrevAlt.IsChecked == true,
                    HkPrevKey = HkPrevKey.SelectedIndex,

                    HistoryLimit = HistoryLimitBox.Text
                };

                // Убедимся, что папка существует (с защитой от null)
                string? folder = Path.GetDirectoryName(SettingsPath);
                if (folder != null && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                // Сохраняем в JSON
                string json = JsonSerializer.Serialize(data);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Ошибка сохранения: " + ex.Message);
            }
        }

        // 1. Путь к файлу настроек
        private string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonaClipboard",
            "settings.json");

        // 2. Класс для хранения данных (простая структура)
        public class AppSettingsData
        {
            public bool HkNextCtrl { get; set; }
            public bool HkNextShift { get; set; }
            public bool HkNextAlt { get; set; }
            public int HkNextKey { get; set; } = 22;

            public bool HkPrevCtrl { get; set; }
            public bool HkPrevShift { get; set; }
            public bool HkPrevAlt { get; set; }
            public int HkPrevKey { get; set; } = 18;

            public string HistoryLimit { get; set; } = "20"; // Дефолтное значение
        }

        // 3. Новый метод загрузки
        private void LoadSettings()
        {
            try
            {
                // Если файла нет — просто выходим (будут настройки по умолчанию из XAML)
                if (!File.Exists(SettingsPath)) return;

                string json = File.ReadAllText(SettingsPath);
                var data = JsonSerializer.Deserialize<AppSettingsData>(json);

                if (data != null)
                {
                    HkNextCtrl.IsChecked = data.HkNextCtrl;
                    HkNextShift.IsChecked = data.HkNextShift;
                    HkNextAlt.IsChecked = data.HkNextAlt;
                    HkNextKey.SelectedIndex = data.HkNextKey;

                    HkPrevCtrl.IsChecked = data.HkPrevCtrl;
                    HkPrevShift.IsChecked = data.HkPrevShift;
                    HkPrevAlt.IsChecked = data.HkPrevAlt;
                    HkPrevKey.SelectedIndex = data.HkPrevKey;

                    HistoryLimitBox.Text = data.HistoryLimit;
                }
            }
            catch (Exception ex)
            {
                // Если файл битый, просто игнорируем ошибку, чтобы прога открылась
                System.Diagnostics.Debug.WriteLine("Ошибка чтения настроек: " + ex.Message);
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) { HistoryView.Visibility = Visibility.Collapsed; SettingsView.Visibility = Visibility.Visible; DbPathText.Text = _dbPath; }
        private void BackButton_Click(object sender, RoutedEventArgs e) { SettingsView.Visibility = Visibility.Collapsed; HistoryView.Visibility = Visibility.Visible; }
        private void OpenFolder_Click(object sender, RoutedEventArgs e) { try { System.Diagnostics.Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); } catch { } }
        private void ApplyHotKeys_Click(object sender, RoutedEventArgs e) { HotKeyHelper.Unregister(_hWnd, ID_NEXT); HotKeyHelper.Unregister(_hWnd, ID_PREV); RegisterHotKeysFromUI(); SaveSettings(); MaintenanceStatus.Text = "Настройки сохранены!"; }
        private void ApplyLimit_Click(object sender, RoutedEventArgs e) { TrimHistory(); SaveSettings(); MaintenanceStatus.Text = "Лимит применен"; }
        private void TrimHistory() { if (!int.TryParse(HistoryLimitBox.Text, out int limit)) limit = 100; if (_fullHistory.Count > limit) { using (var db = new SqliteConnection($"Filename={_dbPath}")) { db.Open(); new SqliteCommand($"DELETE FROM History WHERE Id NOT IN (SELECT Id FROM History ORDER BY Id DESC LIMIT {limit})", db).ExecuteNonQuery(); } LoadHistoryFromDb(); } }
        private void RemoveDuplicates_Click(object sender, RoutedEventArgs e) { using (var db = new SqliteConnection($"Filename={_dbPath}")) { db.Open(); new SqliteCommand("DELETE FROM History WHERE Id NOT IN (SELECT MAX(Id) FROM History GROUP BY Content, Type)", db).ExecuteNonQuery(); } LoadHistoryFromDb(); MaintenanceStatus.Text = "Дубликаты удалены"; }
        private void RemoveImages_Click(object sender, RoutedEventArgs e) { using (var db = new SqliteConnection($"Filename={_dbPath}")) { db.Open(); new SqliteCommand("DELETE FROM History WHERE Type = 'Image'", db).ExecuteNonQuery(); } LoadHistoryFromDb(); MaintenanceStatus.Text = "Картинки удалены"; }
        private void RemoveHeavy_Click(object sender, RoutedEventArgs e) { using (var db = new SqliteConnection($"Filename={_dbPath}")) { db.Open(); new SqliteCommand("DELETE FROM History WHERE length(ImageBytes) > 2097152", db).ExecuteNonQuery(); } LoadHistoryFromDb(); MaintenanceStatus.Text = "Тяжелые файлы удалены"; }
        private void ClearHistory_Click(object sender, RoutedEventArgs e) { using (var db = new SqliteConnection($"Filename={_dbPath}")) { db.Open(); new SqliteCommand("DELETE FROM History", db).ExecuteNonQuery(); } _fullHistory.Clear(); UpdateListUI(); MaintenanceStatus.Text = "История очищена"; }

        private async Task<BitmapImage?> BytesToImage(byte[]? bytes)
        {
            if (bytes == null) return null;
            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
            {
                await stream.WriteAsync(bytes.AsBuffer()); stream.Seek(0);
                BitmapImage image = new BitmapImage(); await image.SetSourceAsync(stream); return image;
            }
        }

        private void CenterWindow()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);

            // --- ИСПРАВЛЕНИЕ ЗДЕСЬ (AppWindow может быть null) ---
            AppWindow? appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                DisplayArea? displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var centeredPosition = appWindow.Position;
                    centeredPosition.X = ((displayArea.WorkArea.Width - appWindow.Size.Width) / 2);
                    centeredPosition.Y = ((displayArea.WorkArea.Height - appWindow.Size.Height) / 2);
                    appWindow.Move(centeredPosition);
                }
            }
        }

        [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc? pfnSubclass, uint uIdSubclass, IntPtr dwRefData);
        [DllImport("comctl32.dll", SetLastError = true)] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion U; public static int Size => Marshal.SizeOf(typeof(INPUT)); }
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
        const int INPUT_KEYBOARD = 1; const uint KEYEVENTF_KEYUP = 0x0002; const ushort VK_CONTROL = 0x11; const ushort VK_V = 0x56;
    }

    public class TypeToIconConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string? type = value as string;
            if (type == "Image") return "\uEB9F";
            if (type == "File") return "\uE8B7";
            return "\uE8C4";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) { throw new NotImplementedException(); }
    }
}