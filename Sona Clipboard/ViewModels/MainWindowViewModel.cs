using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Sona_Clipboard.Models;
using Sona_Clipboard.Services;

namespace Sona_Clipboard.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService;
        private readonly DatabaseService _databaseService;
        private readonly ClipboardService _clipboardService;
        private readonly BackupService _backupService;

        private string _searchQuery = "";
        private bool _isSettingsVisible = false;
        private bool _isHistoryEmpty = true;
        private string _maintenanceStatus = "";
        private string _dbHealthText = "Размер БД: 0 MB";
        private bool _searchInArchive = false;

        public ObservableCollection<ClipboardItem> History { get; } = new();

        #region Properties

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                {
                    // Debounce is handled in View
                }
            }
        }

        public bool IsSettingsVisible
        {
            get => _isSettingsVisible;
            set => SetProperty(ref _isSettingsVisible, value);
        }

        public bool IsHistoryEmpty
        {
            get => _isHistoryEmpty;
            set => SetProperty(ref _isHistoryEmpty, value);
        }

        public string MaintenanceStatus
        {
            get => _maintenanceStatus;
            set => SetProperty(ref _maintenanceStatus, value);
        }

        public string DbHealthText
        {
            get => _dbHealthText;
            set => SetProperty(ref _dbHealthText, value);
        }

        public bool SearchInArchive
        {
            get => _searchInArchive;
            set
            {
                if (SetProperty(ref _searchInArchive, value))
                    _ = LoadHistoryAsync();
            }
        }

        public string DbPath => _databaseService.DbPath;

        #endregion

        #region Commands

        public ICommand ShowSettingsCommand { get; }
        public ICommand HideSettingsCommand { get; }
        public ICommand ClearHistoryCommand { get; }
        public ICommand RemoveDuplicatesCommand { get; }
        public ICommand OptimizeDbCommand { get; }
        public ICommand OpenFolderCommand { get; }

        #endregion

        public MainWindowViewModel()
        {
            _settingsService = App.Services.Get<SettingsService>();
            _databaseService = App.Services.Get<DatabaseService>();
            _clipboardService = App.Services.Get<ClipboardService>();
            _backupService = App.Services.Get<BackupService>();

            _clipboardService.ClipboardChanged += OnClipboardChanged;

            // Commands
            ShowSettingsCommand = new RelayCommand(_ => IsSettingsVisible = true);
            HideSettingsCommand = new RelayCommand(_ => IsSettingsVisible = false);
            ClearHistoryCommand = new RelayCommand(async _ => await ClearHistoryAsync());
            RemoveDuplicatesCommand = new RelayCommand(async _ => await RemoveDuplicatesAsync());
            OptimizeDbCommand = new RelayCommand(async _ => await OptimizeDbAsync());
            OpenFolderCommand = new RelayCommand(_ => OpenFolder());

            _ = LoadHistoryAsync();
            UpdateDbHealth();
        }

        private void OnClipboardChanged(ClipboardItem item)
        {
            _ = AddToHistoryAsync(item);
        }

        public async Task AddToHistoryAsync(ClipboardItem item)
        {
            await _databaseService.SaveItemAsync(item, item.ThumbnailBytes);
            await LoadHistoryAsync();
        }

        public async Task LoadHistoryAsync()
        {
            var items = await _databaseService.LoadHistoryAsync(
                string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery, 
                SearchInArchive);

            History.Clear();
            foreach (var item in items)
                History.Add(item);

            IsHistoryEmpty = History.Count == 0;
        }

        public async Task TogglePinAsync(ClipboardItem item)
        {
            item.IsPinned = !item.IsPinned;
            await _databaseService.TogglePinAsync(item.Id, item.IsPinned);
            await LoadHistoryAsync();
        }

        public async Task DeleteItemAsync(ClipboardItem item)
        {
            await _databaseService.DeleteItemAsync(item.Id);
            History.Remove(item);
            IsHistoryEmpty = History.Count == 0;
        }

        public async Task CopyToClipboardAsync(ClipboardItem item)
        {
            if (item.Type == "Image" && item.ImageBytes == null)
                item.ImageBytes = await _databaseService.GetFullImageBytesAsync(item.Id);
            
            await _clipboardService.CopyToClipboard(item);
        }

        private async Task ClearHistoryAsync()
        {
            await _databaseService.ClearAllAsync();
            await LoadHistoryAsync();
            MaintenanceStatus = "История очищена";
        }

        private async Task RemoveDuplicatesAsync()
        {
            await _databaseService.RemoveDuplicatesAsync();
            await LoadHistoryAsync();
            MaintenanceStatus = "Дубликаты удалены";
        }

        private async Task OptimizeDbAsync()
        {
            await _databaseService.PerformMaintenanceAsync();
            UpdateDbHealth();
            MaintenanceStatus = "Оптимизировано!";
        }

        private void UpdateDbHealth()
        {
            try
            {
                var info = new System.IO.FileInfo(_databaseService.DbPath);
                DbHealthText = $"Размер БД: {(double)info.Length / (1024 * 1024):F2} MB";
            }
            catch { }
        }

        private void OpenFolder()
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory);
            }
            catch { }
        }
    }
}
