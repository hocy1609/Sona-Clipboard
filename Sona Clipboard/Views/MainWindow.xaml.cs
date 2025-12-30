using H.NotifyIcon; //  
using Microsoft.Data.Sqlite;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input; //   StandardUICommand
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;
using System.Windows.Input; //   ICommand
using System.Runtime.InteropServices.WindowsRuntime; //   AsBuffer

using Sona_Clipboard.Services; // For RelayCommand
using Sona_Clipboard.Models;

namespace Sona_Clipboard.Views
{
    public sealed partial class MainWindow : Window
    {
        public ICommand ShowWindowCommand { get; }
        public ICommand ExitAppCommand { get; }

        private SettingsService _settingsService;
        private DatabaseService _databaseService;
        private HotKeyService _hotKeyService;
        private ClipboardService _clipboardService;
        private KeyboardService _keyboardService;
        private UIService _uiService;

        private List<ClipboardItem> _fullHistory = new List<ClipboardItem>();

        private IntPtr _hWnd;
        private AppWindow? _appWindow;

        private PreviewWindow _previewWindow;

        private int _currentPreviewIndex = 0;
        private DispatcherTimer _releaseCheckTimer;

        public MainWindow()
        {
            _settingsService = new SettingsService();
            _databaseService = new DatabaseService();
            _clipboardService = new ClipboardService();
            _keyboardService = new KeyboardService();
            
            _clipboardService.ClipboardChanged += (item) =>
            {
                if (_fullHistory.Count > 0 && _fullHistory[0].Type == item.Type && _fullHistory[0].Content == item.Content) return;
                AddToHistory(item);
            };

            // 1. Initialize commands using RelayCommand (logic-only, works when hidden)
            ShowWindowCommand = new RelayCommand((param) => _uiService.ShowWindow());
            ExitAppCommand = new RelayCommand((param) => ExitApp_Internal());

            this.InitializeComponent();

            _uiService = new UIService(this);
            _uiService.InitializeWindow("Sona Clipboard", "favicon.ico");
            _uiService.CenterWindow();

            // Ensure bindings/commands work for the tray icon
            if (this.Content is FrameworkElement root) 
            {
                 root.DataContext = this;
            }

            // FIX: Explicitly set DataContext for the ContextFlyout items to ensure menu items work
            if (TrayIcon?.ContextFlyout is MenuFlyout flyout)
            {
                foreach (var item in flyout.Items)
                {
                    if (item is FrameworkElement fe)
                    {
                        fe.DataContext = this;
                    }
                }
            }

            _hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            _hotKeyService = new HotKeyService(_hWnd);
            _hotKeyService.HotKeyPressed += (id) =>
            {
                if (id == HotKeyService.ID_NEXT) ShowPreview(goDeeper: true);
                else if (id == HotKeyService.ID_PREV) ShowPreview(goDeeper: false);
            };

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            LoadHistoryFromDb();


            _previewWindow = new PreviewWindow();

            _releaseCheckTimer = new DispatcherTimer();
            _releaseCheckTimer.Interval = TimeSpan.FromMilliseconds(50);
            _releaseCheckTimer.Tick += ReleaseCheckTimer_Tick;

            LoadSettingsUI();
            SetupHotKeys();

            // --- :    (  ) --- 
            this.Closed += MainWindow_Closed;

            // ---    --- 
            if (this.Content is FrameworkElement content)
            {
                content.Loaded += (s, e) => CheckFirstRun();
            }

            //   .   ,    .
            CheckFirstRun();
        }
        
                // ========================================== 
                //    
                // ========================================== 
        
                private async void CheckFirstRun()
                {
                    //  ,   
                    _settingsService.Load();
                    LoadSettingsUI();
        
                    if (_settingsService.CurrentSettings.IsFirstRun)
                    {
                        // 1.   ,     ""
                        this.Activate();
        
                        // 2. ,     XamlRoot ( )
                        if (this.Content is FrameworkElement content)
                        {
                            //  XamlRoot  ,   Loaded
                            if (content.XamlRoot == null)
                            {
                                var tcs = new TaskCompletionSource<bool>();
                                RoutedEventHandler handler = (s, e) => tcs.TrySetResult(true);
                                content.Loaded += handler;
                                await tcs.Task;
                                content.Loaded -= handler;
                            }
                        }
        
                        // 3.    
                        ContentDialog dialog = new ContentDialog
                        {
                            Title = "Sona Clipboard !",
                            Content = "      ( ).\n\n       (Alt+A  ..),   .",
                            CloseButtonText = "",
                            XamlRoot = this.Content.XamlRoot
                        };
        
                        await dialog.ShowAsync();
        
                        // 4.    ""      
                        _settingsService.CurrentSettings.IsFirstRun = false;
                        _settingsService.Save();
                        this.Hide();
                    }
                    else
                    {
                        //   
                        //    this.Activate(),   , 
                        //    ,        .
                    }
                }
        
                // ... existing methods ...
        
        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            //  
            args.Handled = true;
            //   
            this.Hide();
        }

        //     ""
        private void Open_Click(object sender, RoutedEventArgs e)
        {
            _uiService.ShowWindow();
        }

        //     ""
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            ExitApp_Internal();
        }

        private void ExitApp_Internal()
        {
            //   ,    
            this.Closed -= MainWindow_Closed;

            //  ,  
            _previewWindow?.Close();

            //   
            this.Close();

            //   
            Environment.Exit(0);
        }

        // ========================================== 
        //  
        // ========================================== 

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
                        await _clipboardService.CopyToClipboard(item);
                        _previewWindow.Hide();
                        await _keyboardService.PasteSelectionAsync();
                    }
                }
            }
            catch { }
        }

        private void SetupHotKeys()
        {
            RegisterHotKeysFromUI();
        }

        private void RegisterHotKeysFromUI()
        {
            RegisterSingleKey(HotKeyService.ID_NEXT, HkNextCtrl, HkNextShift, HkNextAlt, HkNextKey);
            RegisterSingleKey(HotKeyService.ID_PREV, HkPrevCtrl, HkPrevShift, HkPrevAlt, HkPrevKey);
        }

        private void RegisterSingleKey(int id, CheckBox ctrl, CheckBox shift, CheckBox alt, ComboBox keyBox)
        {
            uint mods = 0;
            if (ctrl.IsChecked == true) mods |= HotKeyHelper.MOD_CONTROL;
            if (shift.IsChecked == true) mods |= HotKeyHelper.MOD_SHIFT;
            if (alt.IsChecked == true) mods |= HotKeyHelper.MOD_ALT;
            
            // SAFETY: Don't register if no modifiers are selected to avoid global key stealing
            if (mods == 0)
            {
                _hotKeyService.Unregister(id);
                return;
            }

            uint vk = (uint)(0x41 + keyBox.SelectedIndex);
            _hotKeyService.Register(id, mods, vk);
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

            

        private void AddToHistory(ClipboardItem item)
        {
            this.DispatcherQueue.TryEnqueue(async () =>
            {
                _fullHistory.Insert(0, item);
                await _databaseService.SaveItemAsync(item);
                await TrimHistoryAsync();
                UpdateListUI(SearchBox.Text);
                _currentPreviewIndex = 0;
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

        private async void ClipboardList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ClipboardItem item)
            {
                _currentPreviewIndex = _fullHistory.IndexOf(item);
                await _clipboardService.CopyToClipboard(item);
            }
        }

        private async void LoadHistoryFromDb()
        {
            _fullHistory = await _databaseService.LoadHistoryAsync();
            UpdateListUI();
        }

        private void LoadSettingsUI()
        {
            var data = _settingsService.CurrentSettings;
            HkNextCtrl.IsChecked = data.HkNextCtrl;
            HkNextShift.IsChecked = data.HkNextShift;
            HkNextAlt.IsChecked = data.HkNextAlt;
            HkNextKey.SelectedIndex = data.HkNextKey;
            HkPrevCtrl.IsChecked = data.HkPrevCtrl;
            HkPrevShift.IsChecked = data.HkPrevShift;
            HkPrevAlt.IsChecked = data.HkPrevAlt;
            HkPrevKey.SelectedIndex = data.HkPrevKey;
            HistoryLimitBox.Text = data.HistoryLimit;
            AutoStartToggle.IsOn = data.IsAutoStart;
        }

        private void SaveSettingsFromUI()
        {
            var data = _settingsService.CurrentSettings;
            data.HkNextCtrl = HkNextCtrl.IsChecked == true;
            data.HkNextShift = HkNextShift.IsChecked == true;
            data.HkNextAlt = HkNextAlt.IsChecked == true;
            data.HkNextKey = HkNextKey.SelectedIndex;
            data.HkPrevCtrl = HkPrevCtrl.IsChecked == true;
            data.HkPrevShift = HkPrevShift.IsChecked == true;
            data.HkPrevAlt = HkPrevAlt.IsChecked == true;
            data.HkPrevKey = HkPrevKey.SelectedIndex;
            data.HistoryLimit = HistoryLimitBox.Text;
            data.IsAutoStart = AutoStartToggle.IsOn;
            _settingsService.Save();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) { HistoryView.Visibility = Visibility.Collapsed; SettingsView.Visibility = Visibility.Visible; DbPathText.Text = _databaseService.DbPath; }
        private void BackButton_Click(object sender, RoutedEventArgs e) { SettingsView.Visibility = Visibility.Collapsed; HistoryView.Visibility = Visibility.Visible; }
        private void OpenFolder_Click(object sender, RoutedEventArgs e) { try { System.Diagnostics.Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); } catch { } }
        private void ApplyHotKeys_Click(object sender, RoutedEventArgs e) { _hotKeyService.Unregister(HotKeyService.ID_NEXT); _hotKeyService.Unregister(HotKeyService.ID_PREV); RegisterHotKeysFromUI(); SaveSettingsFromUI(); MaintenanceStatus.Text = "Готово!"; }
        private async void ApplyLimit_Click(object sender, RoutedEventArgs e) { await TrimHistoryAsync(); SaveSettingsFromUI(); MaintenanceStatus.Text = "Лимит применен"; }
        private async Task TrimHistoryAsync() { if (!int.TryParse(HistoryLimitBox.Text, out int limit)) limit = 100; await _databaseService.TrimHistoryAsync(limit); LoadHistoryFromDb(); }
        private async void RemoveDuplicates_Click(object sender, RoutedEventArgs e) { await _databaseService.RemoveDuplicatesAsync(); LoadHistoryFromDb(); MaintenanceStatus.Text = "Дубликаты удалены"; }
        private async void RemoveImages_Click(object sender, RoutedEventArgs e) { await _databaseService.RemoveImagesAsync(); LoadHistoryFromDb(); MaintenanceStatus.Text = "Картинки удалены"; }
        private async void RemoveHeavy_Click(object sender, RoutedEventArgs e) { await _databaseService.RemoveHeavyAsync(); LoadHistoryFromDb(); MaintenanceStatus.Text = "Тяжелые файлы удалены"; }
        private async void ClearHistory_Click(object sender, RoutedEventArgs e) { await _databaseService.ClearAllAsync(); _fullHistory.Clear(); UpdateListUI(); MaintenanceStatus.Text = "История очищена"; }

        private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                _settingsService.SetAutoStart(toggle.IsOn);
            }
        }

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);
    }
}
