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
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Sona_Clipboard
{
    public sealed partial class MainWindow : Window
    {
        private bool _isCopiedByMe = false;
        private string _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SonaClipboard.db");
        private List<ClipboardItem> _fullHistory = new List<ClipboardItem>();

        private IntPtr _hWnd;
        private AppWindow _appWindow;
        private SubclassProc _subclassDelegate;

        private PreviewWindow _previewWindow;
        private int _currentPreviewIndex = 0;

        // Таймер для проверки "Отпустил ли клавиши?"
        private DispatcherTimer _releaseCheckTimer;

        // ID команд
        private const int ID_OPEN = 9000;
        private const int ID_NEXT = 9001; // W
        private const int ID_PREV = 9002; // S

        public MainWindow()
        {
            this.InitializeComponent();

            _hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            CenterWindow();

            InitializeDatabase();
            LoadHistoryFromDb();

            _previewWindow = new PreviewWindow();

            // Настраиваем таймер проверки клавиш
            _releaseCheckTimer = new DispatcherTimer();
            _releaseCheckTimer.Interval = TimeSpan.FromMilliseconds(50); // Проверяем 20 раз в секунду
            _releaseCheckTimer.Tick += ReleaseCheckTimer_Tick;

            Clipboard.ContentChanged += Clipboard_ContentChanged;
            SetupHotKeys();
        }

        // --- ЛОГИКА "ОТПУСТИЛ - СКОПИРОВАЛ" ---
        private void ReleaseCheckTimer_Tick(object sender, object e)
        {
            // Проверяем, нажаты ли модификаторы (Ctrl, Shift, Alt)
            // GetAsyncKeyState возвращает < 0 если клавиша нажата
            bool ctrlPressed = (GetAsyncKeyState(0x11) & 0x8000) != 0;  // VK_CONTROL
            bool shiftPressed = (GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
            bool altPressed = (GetAsyncKeyState(0x12) & 0x8000) != 0;   // VK_MENU

            // Если НИ ОДИН из модификаторов не нажат - значит пользователь отпустил комбинацию
            if (!ctrlPressed && !shiftPressed && !altPressed)
            {
                // Останавливаем таймер
                _releaseCheckTimer.Stop();

                // Копируем то, что сейчас выбрано
                if (_previewWindow.Visible && _fullHistory.Count > _currentPreviewIndex)
                {
                    var item = _fullHistory[_currentPreviewIndex];
                    CopyToClipboard(item);
                    _previewWindow.Hide(); // Прячем окно
                }
            }
        }

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        // --- ЛОГИКА ГОРЯЧИХ КЛАВИШ ---
        private void SetupHotKeys()
        {
            HotKeyHelper.Unregister(_hWnd, ID_OPEN);
            HotKeyHelper.Unregister(_hWnd, ID_NEXT);
            HotKeyHelper.Unregister(_hWnd, ID_PREV);
            RegisterHotKeysFromUI();
            if (_subclassDelegate == null)
            {
                _subclassDelegate = new SubclassProc(WndProc);
                SetWindowSubclass(_hWnd, _subclassDelegate, 0, IntPtr.Zero);
            }
            this.Closed += (s, e) => {
                HotKeyHelper.Unregister(_hWnd, ID_OPEN);
                HotKeyHelper.Unregister(_hWnd, ID_NEXT);
                HotKeyHelper.Unregister(_hWnd, ID_PREV);
                _previewWindow.Close();
            };
        }

        private void RegisterHotKeysFromUI()
        {
            RegisterSingleKey(ID_OPEN, HkOpenCtrl, HkOpenShift, HkOpenAlt, HkOpenKey);
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
                if (id == ID_OPEN)
                {
                    ToggleWindowVisibility();
                }
                else if (id == ID_NEXT)
                { // W (Вверх/Старее)
                    ShowPreview(goDeeper: true);
                }
                else if (id == ID_PREV)
                { // S (Вниз/Новее)
                    ShowPreview(goDeeper: false);
                }
                return IntPtr.Zero;
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void ShowPreview(bool goDeeper)
        {
            if (_fullHistory.Count == 0) return;

            // Если окно только открывается - запускаем слежку за отпусканием клавиш
            if (_previewWindow.Visible == false)
            {
                _currentPreviewIndex = 0;
                _releaseCheckTimer.Start(); // <--- СТАРТ ТАЙМЕРА
            }
            else
            {
                if (goDeeper) _currentPreviewIndex++; else _currentPreviewIndex--;
            }

            if (_currentPreviewIndex < 0) _currentPreviewIndex = 0;
            if (_currentPreviewIndex >= _fullHistory.Count) _currentPreviewIndex = _fullHistory.Count - 1;

            var item = _fullHistory[_currentPreviewIndex];
            _previewWindow.ShowItem(item, _currentPreviewIndex + 1);
        }

        // --- ЛОГИКА ---
        private void ToggleWindowVisibility()
        {
            if (this.Visible) { _appWindow.Hide(); }
            else { _appWindow.Show(); SetForegroundWindow(_hWnd); }
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
                var insertCommand = new SqliteCommand();
                insertCommand.Connection = db;
                insertCommand.CommandText = "INSERT INTO History VALUES (NULL, @Type, @Content, @ImageBytes, @Timestamp)";
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

        private async void Clipboard_ContentChanged(object? sender, object? e)
        {
            if (_isCopiedByMe) { _isCopiedByMe = false; return; }
            try
            {
                DataPackageView view = Clipboard.GetContent();
                if (view.Contains(StandardDataFormats.Text))
                {
                    string text = await view.GetTextAsync();
                    if (_fullHistory.Count > 0 && _fullHistory[0].Content == text) return;
                    var newItem = new ClipboardItem { Type = "Text", Content = text, Timestamp = DateTime.Now.ToString() };
                    AddToHistory(newItem);
                }
                else if (view.Contains(StandardDataFormats.Bitmap))
                {
                    RandomAccessStreamReference imageStreamRef = await view.GetBitmapAsync();
                    using (IRandomAccessStreamWithContentType stream = await imageStreamRef.OpenReadAsync())
                    {
                        byte[] imageBytes = new byte[stream.Size];
                        using (DataReader reader = new DataReader(stream))
                        {
                            await reader.LoadAsync((uint)stream.Size);
                            reader.ReadBytes(imageBytes);
                        }
                        if (_fullHistory.Count > 0 && _fullHistory[0].Type == "Image" && _fullHistory[0].ImageBytes?.Length == imageBytes.Length) return;
                        var newItem = new ClipboardItem { Type = "Image", Content = "Картинка " + DateTime.Now.ToString("HH:mm"), ImageBytes = imageBytes, Timestamp = DateTime.Now.ToString(), Thumbnail = await BytesToImage(imageBytes) };
                        AddToHistory(newItem);
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
            });
        }

        private void UpdateListUI(string query = "")
        {
            ClipboardList.Items.Clear();
            var itemsToShow = string.IsNullOrWhiteSpace(query) ? _fullHistory : _fullHistory.Where(i => i.Content != null && i.Content.ToLower().Contains(query.ToLower())).ToList();
            foreach (var item in itemsToShow) ClipboardList.Items.Add(item);
            EmptyStatusText.Visibility = (ClipboardList.Items.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { UpdateListUI(SearchBox.Text); }

        private void ClipboardList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ClipboardItem;
            CopyToClipboard(item);
        }

        private async void CopyToClipboard(ClipboardItem item)
        {
            if (item == null) return;
            _isCopiedByMe = true;
            DataPackage package = new DataPackage();
            if (item.Type == "Text" && item.Content != null) package.SetText(item.Content);
            else if (item.Type == "Image" && item.ImageBytes != null)
            {
                InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(item.ImageBytes.AsBuffer());
                stream.Seek(0);
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            }
            Clipboard.SetContent(package);
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) { HistoryView.Visibility = Visibility.Collapsed; SettingsView.Visibility = Visibility.Visible; DbPathText.Text = _dbPath; }
        private void BackButton_Click(object sender, RoutedEventArgs e) { SettingsView.Visibility = Visibility.Collapsed; HistoryView.Visibility = Visibility.Visible; }
        private void OpenFolder_Click(object sender, RoutedEventArgs e) { try { System.Diagnostics.Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); } catch { } }
        private void ApplyHotKeys_Click(object sender, RoutedEventArgs e)
        {
            HotKeyHelper.Unregister(_hWnd, ID_OPEN); HotKeyHelper.Unregister(_hWnd, ID_NEXT); HotKeyHelper.Unregister(_hWnd, ID_PREV);
            RegisterHotKeysFromUI(); MaintenanceStatus.Text = "Горячие клавиши обновлены!";
        }
        private void ApplyLimit_Click(object sender, RoutedEventArgs e) { TrimHistory(); MaintenanceStatus.Text = $"Лимит применен"; }

        private void TrimHistory()
        {
            if (!int.TryParse(HistoryLimitBox.Text, out int limit)) limit = 100;
            if (_fullHistory.Count > limit)
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    db.Open();
                    var sql = $"DELETE FROM History WHERE Id NOT IN (SELECT Id FROM History ORDER BY Id DESC LIMIT {limit})";
                    new SqliteCommand(sql, db).ExecuteNonQuery();
                }
                LoadHistoryFromDb();
            }
        }
        private void RemoveDuplicates_Click(object sender, RoutedEventArgs e)
        {
            using (var db = new SqliteConnection($"Filename={_dbPath}"))
            {
                db.Open();
                var sql = "DELETE FROM History WHERE Id NOT IN (SELECT MAX(Id) FROM History GROUP BY Content, Type)";
                new SqliteCommand(sql, db).ExecuteNonQuery();
            }
            LoadHistoryFromDb(); MaintenanceStatus.Text = "Дубликаты удалены";
        }
        private void RemoveImages_Click(object sender, RoutedEventArgs e)
        {
            using (var db = new SqliteConnection($"Filename={_dbPath}"))
            {
                db.Open(); new SqliteCommand("DELETE FROM History WHERE Type = 'Image'", db).ExecuteNonQuery();
            }
            LoadHistoryFromDb(); MaintenanceStatus.Text = "Картинки удалены";
        }
        private void RemoveHeavy_Click(object sender, RoutedEventArgs e)
        {
            using (var db = new SqliteConnection($"Filename={_dbPath}"))
            {
                db.Open(); new SqliteCommand("DELETE FROM History WHERE length(ImageBytes) > 2097152", db).ExecuteNonQuery();
            }
            LoadHistoryFromDb(); MaintenanceStatus.Text = "Тяжелые файлы удалены";
        }
        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            using (var db = new SqliteConnection($"Filename={_dbPath}"))
            {
                db.Open(); new SqliteCommand("DELETE FROM History", db).ExecuteNonQuery();
            }
            _fullHistory.Clear(); UpdateListUI(); MaintenanceStatus.Text = "История очищена";
        }
        private async Task<BitmapImage> BytesToImage(byte[] bytes)
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
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var centeredPosition = appWindow.Position;
                    centeredPosition.X = ((displayArea.WorkArea.Width - appWindow.Size.Width) / 2);
                    centeredPosition.Y = ((displayArea.WorkArea.Height - appWindow.Size.Height) / 2);
                    appWindow.Move(centeredPosition);
                }
            }
        }

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass, IntPtr dwRefData);
        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);
    }
    public class TypeToIconConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string type = value as string;
            // Коды иконок из шрифта Segoe MDL2 Assets
            if (type == "Image") return "\uEB9F"; // Иконка картинки
            return "\uE8C4"; // Иконка документа (текст)
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
