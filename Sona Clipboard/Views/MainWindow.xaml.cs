using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.Data.Sqlite;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Sona_Clipboard.Models;
using Sona_Clipboard.Services;
using Sona_Clipboard.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Sona_Clipboard.Views
{
    public sealed partial class MainWindow : Window
    {
        public MainWindowViewModel ViewModel { get; private set; }
        public ICommand ShowWindowCommand { get; private set; }
        public ICommand ExitAppCommand { get; private set; }

        private SettingsService _settingsService;
        private DatabaseService _databaseService;
        private HotKeyService _hotKeyService;
        private ClipboardService _clipboardService;
        private KeyboardService _keyboardService;
        private UIService _uiService;
        private BackupService _backupService;

        private List<ClipboardItem> _fullHistory = new List<ClipboardItem>();

        private IntPtr _hWnd;
        private AppWindow? _appWindow;
        private PreviewWindow _previewWindow;
        private ClipboardItem? _selectedPreviewItem;

        private int _currentPreviewIndex = 0;
        private DateTime _lastPasteAt = DateTime.MinValue;
        private DispatcherTimer _releaseCheckTimer;
        private DispatcherTimer _searchDebounceTimer;

        public MainWindow()
        {
            // Get services from DI container
            _settingsService = App.Services.Get<SettingsService>();
            _databaseService = App.Services.Get<DatabaseService>();
            _clipboardService = App.Services.Get<ClipboardService>();
            _keyboardService = App.Services.Get<KeyboardService>();
            _backupService = App.Services.Get<BackupService>();

            _clipboardService.ClipboardChanged += (item) => AddToHistory(item);

            this.InitializeComponent();

            // Initialize ViewModel
            ViewModel = new MainWindowViewModel();

            _uiService = new UIService(this);
            _uiService.InitializeWindow("Sona Clipboard", "favicon.ico");
            _uiService.CenterWindow();

            ShowWindowCommand = new RelayCommand((param) => _uiService.ShowWindow());
            ExitAppCommand = new RelayCommand((param) => ExitApp_Internal());

            // Set DataContext to this Window (for ShowWindowCommand/ExitAppCommand bindings in tray)
            if (this.Content is FrameworkElement root) root.DataContext = this;
            if (TrayIcon?.ContextFlyout is MenuFlyout flyout)
            {
                foreach (var item in flyout.Items) if (item is FrameworkElement fe) fe.DataContext = this;
            }

            _hWnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hWnd));

            _hotKeyService = new HotKeyService(_hWnd);
            _hotKeyService.HotKeyPressed += (id) =>
            {
                if (id == HotKeyService.ID_NEXT) ShowPreview(goDeeper: true);
                else if (id == HotKeyService.ID_PREV) ShowPreview(goDeeper: false);
            };

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            _previewWindow = new PreviewWindow();
            _releaseCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _releaseCheckTimer.Tick += ReleaseCheckTimer_Tick;

            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchDebounceTimer.Tick += (s, e) => { _searchDebounceTimer.Stop(); LoadHistoryFromDb(SearchBox.Text); };

            _settingsService.Load();
            _currentPreviewIndex = _settingsService.CurrentSettings.LastUsedIndex;
            LoadSettingsUI();
            SetupHotKeys();
            LoadHistoryFromDb();

            LogService.Info("Sona Clipboard started");
            LogService.CleanOldLogs(7);

            Task.Run(async () =>
            {
                await Task.Delay(3000);
                await _databaseService.ArchiveOldItemsAsync(30);
                await _databaseService.PerformMaintenanceAsync();
            });

            this.Closed += MainWindow_Closed;
        }

        private async void ShowPreview(bool goDeeper)
        {
            if (_fullHistory.Count == 0) return;

            if (!_previewWindow.Visible)
            {
                // Reset index if idle for too long, but ONLY when opening the window
                bool shouldResetIndex = (DateTime.UtcNow - _lastPasteAt) > TimeSpan.FromSeconds(3);
                if (shouldResetIndex)
                {
                    _currentPreviewIndex = 0;
                    _settingsService.CurrentSettings.LastUsedIndex = 0;
                }
                else
                {
                    _currentPreviewIndex = _settingsService.CurrentSettings.LastUsedIndex;
                }
                _releaseCheckTimer.Start();
            }

            // Move index immediately on every hotkey press
            if (goDeeper) _currentPreviewIndex++; else _currentPreviewIndex--;

            // Clamp (don't wrap around) - UX improvement
            if (_currentPreviewIndex < 0) _currentPreviewIndex = 0;
            if (_currentPreviewIndex >= _fullHistory.Count) _currentPreviewIndex = _fullHistory.Count - 1;

            // Persist the NEW index
            _settingsService.CurrentSettings.LastUsedIndex = _currentPreviewIndex;
            _settingsService.Save();

            _selectedPreviewItem = _fullHistory[_currentPreviewIndex];

            if (_selectedPreviewItem != null)
            {
                if (_selectedPreviewItem.Type == "Image" && _selectedPreviewItem.ImageBytes == null)
                {
                    _selectedPreviewItem.ImageBytes = await _databaseService.GetFullImageBytesAsync(_selectedPreviewItem.Id);
                }
                _previewWindow.ShowItem(_selectedPreviewItem, _currentPreviewIndex + 1);
            }
        }

        private void AddToHistory(ClipboardItem item)
        {
            this.DispatcherQueue.TryEnqueue(async () =>
            {
                await _databaseService.SaveItemAsync(item, item.ThumbnailBytes);
                await TrimHistoryAsync();

                bool inPreview = _previewWindow.Visible;
                LoadHistoryFromDb(SearchBox.Text);

                if (inPreview)
                {
                    // Shift index to keep looking at same physical item
                    _currentPreviewIndex++;
                    if (_currentPreviewIndex >= _fullHistory.Count) _currentPreviewIndex = 0;
                }
                else
                {
                    _currentPreviewIndex = 0; // Newest is 0
                }
            });
        }

        private async Task TrimHistoryAsync()
        {
            if (!double.TryParse(HistoryLimitBox.Text, out double limitGb)) limitGb = 10.0;
            await _databaseService.TrimHistoryBySizeAsync(limitGb);
        }

        private async void LoadHistoryFromDb(string? query = null)
        {
            bool searchArchive = (ArchiveSearchCheck != null) && (ArchiveSearchCheck.IsChecked == true);
            _fullHistory = await _databaseService.LoadHistoryAsync(query, searchArchive);
            ClipboardList.Items.Clear();
            foreach (var item in _fullHistory) ClipboardList.Items.Add(item);
            EmptyStatusText.Visibility = (_fullHistory.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void CreateBackup_Click(object sender, RoutedEventArgs e)
        {
            var savePicker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, _hWnd);
            savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Sona Backup", new List<string>() { ".sonabak" });
            savePicker.SuggestedFileName = $"Sona_Backup_{DateTime.Now:yyyyMMdd}";

            var file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                try
                {
                    MaintenanceStatus.Text = "Бэкап...";
                    await _backupService.CreateBackupAsync(_databaseService.DbPath, Path.GetDirectoryName(file.Path) ?? "", BackupPasswordBox.Password);
                    MaintenanceStatus.Text = "Успех!";
                    LogService.Info($"Backup created: {file.Path}");
                }
                catch (Exception ex) { MaintenanceStatus.Text = "Ошибка: " + ex.Message; LogService.Error("Backup failed", ex); }
            }
        }

        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            var openPicker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, _hWnd);
            openPicker.FileTypeFilter.Add(".sonabak");
            var file = await openPicker.PickSingleFileAsync();
            if (file != null)
            {
                try
                {
                    await _backupService.RestoreAsync(file.Path, _databaseService.DbPath, BackupPasswordBox.Password);
                    LoadHistoryFromDb();
                    MaintenanceStatus.Text = "Восстановлено!";
                    LogService.Info("Backup restored successfully");
                }
                catch (Exception ex) { MaintenanceStatus.Text = "Ошибка пароля!"; LogService.Error("Restore failed", ex); }
            }
        }

        private async void OptimizeDb_Click(object sender, RoutedEventArgs e)
        {
            await _databaseService.PerformMaintenanceAsync();
            UpdateDbHealthUI();
            MaintenanceStatus.Text = "Оптимизировано!";
        }

        private void UpdateDbHealthUI()
        {
            try
            {
                var info = new FileInfo(_databaseService.DbPath);
                DbHealthText.Text = $"Размер БД: {(double)info.Length / (1024 * 1024):F2} MB";
            }
            catch { }
        }

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            HistoryView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Visible;
            DbPathText.Text = _databaseService.DbPath;
            UpdateDbHealthUI();
            await RefreshAppStats();
        }

        private async Task RefreshAppStats()
        {
            var stats = await _databaseService.GetAppUsageStatsAsync();
            AppStatsList.ItemsSource = stats.ToList();
        }

        private async void DeleteByApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string appName)
            {
                await _databaseService.DeleteBySourceAsync(appName);
                await RefreshAppStats();
                LoadHistoryFromDb();
            }
        }

        private void ReleaseCheckTimer_Tick(object? sender, object e)
        {
            // Check if Alt, Ctrl or Shift are still pressed
            bool mods = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0;

            if (!mods)
            {
                _releaseCheckTimer.Stop(); // Stop ASAP to prevent re-entry

                var itemToPaste = _selectedPreviewItem;
                if (_previewWindow.Visible && itemToPaste != null)
                {
                    // 1. Data Prep in background
                    Task.Run(async () =>
                    {
                        await EnsureRichContentAsync(itemToPaste);

                        // 2. Clipboard & Paste on UI Thread
                        this.DispatcherQueue.TryEnqueue(async () =>
                        {
                            try
                            {
                                await _clipboardService.CopyToClipboard(itemToPaste);

                                // Hide window immediately so user sees responsiveness
                                _previewWindow.Hide();

                                // Small sync delay for Windows OS
                                await Task.Delay(100);

                                // Send Ctrl+V
                                await _keyboardService.PasteSelectionAsync();
                                _lastPasteAt = DateTime.UtcNow;
                            }
                            catch { }
                            finally
                            {
                                _selectedPreviewItem = null;
                            }
                        });
                    });
                }
            }
        }

        private void LoadSettingsUI()
        {
            var d = _settingsService.CurrentSettings;
            HkNextCtrl.IsChecked = d.HkNextCtrl; HkNextShift.IsChecked = d.HkNextShift; HkNextAlt.IsChecked = d.HkNextAlt; HkNextKey.SelectedIndex = d.HkNextKey;
            HkPrevCtrl.IsChecked = d.HkPrevCtrl; HkPrevShift.IsChecked = d.HkPrevShift; HkPrevAlt.IsChecked = d.HkPrevAlt; HkPrevKey.SelectedIndex = d.HkPrevKey;
            HistoryLimitBox.Text = d.HistoryLimitGb.ToString("F1");
            AutoStartToggle.IsOn = d.IsAutoStart;
            RunAsAdminToggle.IsOn = IsRunningAsAdmin();
            DbPathBox.Text = d.DatabasePath ?? "";
        }

        private async void BrowseDbPath_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, _hWnd);
            folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            folderPicker.FileTypeFilter.Add("*");

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                DbPathBox.Text = folder.Path;
            }
        }

        private void ApplyDbPath_Click(object sender, RoutedEventArgs e)
        {
            string newPath = DbPathBox.Text.Trim();
            _settingsService.CurrentSettings.DatabasePath = newPath;
            _settingsService.Save();

            DbPathStatusText.Text = "Сохранено! Перезапустите приложение.";
        }

        private void SaveSettingsFromUI()
        {
            var d = _settingsService.CurrentSettings;
            d.HkNextCtrl = HkNextCtrl.IsChecked == true; d.HkNextShift = HkNextShift.IsChecked == true; d.HkNextAlt = HkNextAlt.IsChecked == true; d.HkNextKey = HkNextKey.SelectedIndex;
            d.HkPrevCtrl = HkPrevCtrl.IsChecked == true; d.HkPrevShift = HkPrevShift.IsChecked == true; d.HkPrevAlt = HkPrevAlt.IsChecked == true; d.HkPrevKey = HkPrevKey.SelectedIndex;
            if (double.TryParse(HistoryLimitBox.Text, out double gb)) d.HistoryLimitGb = gb;
            d.IsAutoStart = AutoStartToggle.IsOn;
            _settingsService.Save();
        }

        private readonly struct HotKeySpec
        {
            public HotKeySpec(uint modifiers, uint key, string keyLabel, bool enabled)
            {
                Modifiers = modifiers;
                Key = key;
                KeyLabel = keyLabel;
                Enabled = enabled;
            }

            public uint Modifiers { get; }
            public uint Key { get; }
            public string KeyLabel { get; }
            public bool Enabled { get; }
        }

        private void SetupHotKeys() => RegisterHotKeysFromUI();

        private void RegisterHotKeysFromUI()
        {
            HotKeyStatusText.Text = string.Empty;

            var nextSpec = BuildHotKeySpec(HkNextCtrl, HkNextShift, HkNextAlt, HkNextKey);
            var prevSpec = BuildHotKeySpec(HkPrevCtrl, HkPrevShift, HkPrevAlt, HkPrevKey);

            if (nextSpec.Enabled && prevSpec.Enabled && nextSpec.Modifiers == prevSpec.Modifiers && nextSpec.Key == prevSpec.Key)
            {
                HotKeyStatusText.Text = "Конфликт: одинаковые горячие клавиши. Измените одну из них.";
                LogService.Warning("Hotkey conflict: next and prev are identical.");
                RegisterSingleKey(HotKeyService.ID_NEXT, nextSpec, "Пред. элемент");
                _hotKeyService.Unregister(HotKeyService.ID_PREV);
                return;
            }

            bool nextOk = RegisterSingleKey(HotKeyService.ID_NEXT, nextSpec, "Пред. элемент");
            bool prevOk = RegisterSingleKey(HotKeyService.ID_PREV, prevSpec, "След. элемент");

            if (nextOk && prevOk)
            {
                HotKeyStatusText.Text = "Горячие клавиши активны.";
            }
            else if (!nextOk && !prevOk)
            {
                HotKeyStatusText.Text = "Не удалось зарегистрировать горячие клавиши. Проверьте конфликты/права.";
            }
            else if (!nextOk)
            {
                HotKeyStatusText.Text = "Не удалось зарегистрировать горячую клавишу для \"Пред. элемент\".";
            }
            else
            {
                HotKeyStatusText.Text = "Не удалось зарегистрировать горячую клавишу для \"След. элемент\".";
            }
        }

        private static HotKeySpec BuildHotKeySpec(CheckBox c, CheckBox s, CheckBox a, ComboBox k)
        {
            uint m = 0;
            if (c.IsChecked == true) m |= HotKeyHelper.MOD_CONTROL;
            if (s.IsChecked == true) m |= HotKeyHelper.MOD_SHIFT;
            if (a.IsChecked == true) m |= HotKeyHelper.MOD_ALT;

            int index = k.SelectedIndex;
            uint key = index >= 0 ? (uint)(0x41 + index) : 0;
            string keyLabel = k.SelectedItem?.ToString() ?? string.Empty;

            return new HotKeySpec(m, key, keyLabel, m != 0 && key != 0);
        }

        private bool RegisterSingleKey(int id, HotKeySpec spec, string label)
        {
            if (!spec.Enabled)
            {
                _hotKeyService.Unregister(id);
                return true;
            }

            bool ok = _hotKeyService.Register(id, spec.Modifiers, spec.Key, out int error);
            if (!ok)
            {
                LogService.Warning($"Failed to register hotkey {label} ({FormatHotKeyLabel(spec)}). Win32 error: {error}");
            }
            return ok;
        }

        private static string FormatHotKeyLabel(HotKeySpec spec)
        {
            var parts = new List<string>();
            if ((spec.Modifiers & HotKeyHelper.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((spec.Modifiers & HotKeyHelper.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((spec.Modifiers & HotKeyHelper.MOD_ALT) != 0) parts.Add("Alt");
            if (!string.IsNullOrWhiteSpace(spec.KeyLabel)) parts.Add(spec.KeyLabel);
            return string.Join("+", parts);
        }
        private void MainWindow_Closed(object sender, WindowEventArgs args) { args.Handled = true; this.Hide(); }
        private void ExitApp_Internal() { this.Closed -= MainWindow_Closed; _hotKeyService?.Dispose(); _previewWindow?.Close(); this.Close(); Environment.Exit(0); }
        private void BackButton_Click(object sender, RoutedEventArgs e) { SettingsView.Visibility = Visibility.Collapsed; HistoryView.Visibility = Visibility.Visible; }
        private void OpenFolder_Click(object sender, RoutedEventArgs e) { try { System.Diagnostics.Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); } catch { } }
        private void ApplyHotKeys_Click(object sender, RoutedEventArgs e) { _hotKeyService.Unregister(HotKeyService.ID_NEXT); _hotKeyService.Unregister(HotKeyService.ID_PREV); RegisterHotKeysFromUI(); SaveSettingsFromUI(); }
        private async void ApplyLimit_Click(object sender, RoutedEventArgs e) { await TrimHistoryAsync(); SaveSettingsFromUI(); MaintenanceStatus.Text = "Лимит применен"; }
        private async void PinButton_Click(object sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is ClipboardItem i) { i.IsPinned = !i.IsPinned; await _databaseService.TogglePinAsync(i.Id, i.IsPinned); LoadHistoryFromDb(); } }
        private async void DeleteButton_Click(object sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is ClipboardItem i) { await _databaseService.DeleteItemAsync(i.Id); _fullHistory.Remove(i); LoadHistoryFromDb(); } }
        private async void ClearHistory_Click(object sender, RoutedEventArgs e) { await _databaseService.ClearAllAsync(); LoadHistoryFromDb(); }
        private async void RemoveDuplicates_Click(object sender, RoutedEventArgs e) { await _databaseService.RemoveDuplicatesAsync(); LoadHistoryFromDb(); }
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { _searchDebounceTimer.Stop(); _searchDebounceTimer.Start(); }
        private void ArchiveSearchCheck_Changed(object sender, RoutedEventArgs e) => LoadHistoryFromDb(SearchBox.Text);
        private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e) { if (sender is ToggleSwitch t) _settingsService.SetAutoStart(t.IsOn); }

        private void RunAsAdminToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch t && t.IsOn)
            {
                // Check if already running as admin
                if (IsRunningAsAdmin())
                {
                    return; // Already admin, do nothing
                }

                // Restart with admin rights
                try
                {
                    string exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    System.Diagnostics.Process.Start(psi);
                    ExitApp_Internal();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // User cancelled UAC
                    t.IsOn = false;
                }
            }
        }

        private static bool IsRunningAsAdmin()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private async void ClipboardList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ClipboardItem i)
            {
                _currentPreviewIndex = _fullHistory.IndexOf(i);
                _settingsService.CurrentSettings.LastUsedIndex = _currentPreviewIndex;
                _settingsService.Save();

                await EnsureRichContentAsync(i);

                await _clipboardService.CopyToClipboard(i);
            }
        }

        private async void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ClipboardItem item)
            {
                var originalToolTip = ToolTipService.GetToolTip(button);
                await EnsureRichContentAsync(item);
                await _clipboardService.CopyToClipboard(item);
                _ = ShowCopyFeedbackAsync(button, originalToolTip);
            }
        }

        private async Task ShowCopyFeedbackAsync(Button button, object? originalToolTip)
        {
            ToolTipService.SetToolTip(button, "Скопировано");
            await Task.Delay(1500);
            ToolTipService.SetToolTip(button, originalToolTip);
        }

        private async void Thumbnail_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ClipboardItem item)
            {
                await EnsureRichContentAsync(item);
            }
        }

        private async void ThumbnailToolTip_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is ToolTip toolTip && toolTip.DataContext is ClipboardItem item)
            {
                if (toolTip.Content is Image image)
                {
                    // 1. Reset/Set default immediately to avoid stale image from virtualization
                    image.Source = item.Thumbnail;

                    // 2. Load heavy content
                    await EnsureRichContentAsync(item);

                    // 3. Update to high-res if available
                    if (item.ImageBytes != null)
                    {
                        image.Source = await CreateBitmapImageAsync(item.ImageBytes);
                    }
                    else
                    {
                        // Explicitly keep/set thumbnail (or null) so we don't show previous item's image
                        image.Source = item.Thumbnail;
                    }
                }
            }
        }

        private static async Task<BitmapImage?> CreateBitmapImageAsync(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;

            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }

        private async Task EnsureRichContentAsync(ClipboardItem item)
        {
            if (item.Type == "Image" && item.ImageBytes == null)
            {
                item.ImageBytes = await _databaseService.GetFullImageBytesAsync(item.Id);
            }
            // Lazy load rich text if it's missing (checking both avoids re-fetching if one is naturally null)
            else if (item.Type == "Text" && item.RtfContent == null && item.HtmlContent == null)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var (rtf, html) = await _databaseService.GetFullTextContentAsync(item.Id);
                stopwatch.Stop();

                if (rtf != null) item.RtfContent = rtf;
                if (html != null) item.HtmlContent = html;

                if (rtf != null || html != null)
                {
                    int size = (rtf?.Length ?? 0) + (html?.Length ?? 0);
                    System.Diagnostics.Debug.WriteLine($"[PERF] LazyLoaded {size} chars of RTF/HTML for Item {item.Id} in {stopwatch.ElapsedMilliseconds}ms");
                }
            }
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    }
}
