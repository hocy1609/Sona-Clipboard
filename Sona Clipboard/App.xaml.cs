using System;
using Microsoft.UI.Xaml;
using Sona_Clipboard.Views;
using Sona_Clipboard.Services;

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
            // Register all services as singletons
            Services.RegisterSingleton(new SettingsService());
            Services.RegisterSingleton(new DatabaseService());
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
