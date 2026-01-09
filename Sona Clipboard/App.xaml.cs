using System;
using Microsoft.UI.Xaml;
using Sona_Clipboard.Services;
using Sona_Clipboard.Views;

namespace Sona_Clipboard
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        public static ServiceContainer Services { get; } = new ServiceContainer();

        /// <summary>
        /// Initializes the singleton application object.
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            ConfigureServices();
        }

        private void ConfigureServices()
        {
            // Register SettingsService first and load settings
            var settingsService = new SettingsService();
            settingsService.Load();
            Services.RegisterSingleton(settingsService);

            // Pass database path from settings to DatabaseService
            string dbPath = settingsService.CurrentSettings.DatabasePath;
            Services.RegisterSingleton(new DatabaseService(string.IsNullOrWhiteSpace(dbPath) ? null : dbPath));

            Services.RegisterSingleton(new ClipboardService());
            Services.RegisterSingleton(new KeyboardService());
            Services.RegisterSingleton(new BackupService());
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            // ВАЖНО: Мы убрали Activate(), чтобы окно не появлялось само.
            // Теперь MainWindow сам решит, показаться (первый запуск) или сидеть в трее.
        }
    }
}
