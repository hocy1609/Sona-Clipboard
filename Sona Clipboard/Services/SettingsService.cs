using System;
using System.IO;
using System.Text.Json;

namespace Sona_Clipboard.Services
{
    public class AppSettingsData
    {
        public bool HkNextCtrl { get; set; }
        public bool HkNextShift { get; set; }
        public bool HkNextAlt { get; set; } = true;
        public int HkNextKey { get; set; } = 22;

        public bool HkPrevCtrl { get; set; }
        public bool HkPrevShift { get; set; }
        public bool HkPrevAlt { get; set; } = true;
        public int HkPrevKey { get; set; } = 18;

        public double HistoryLimitGb { get; set; } = 10.0;
        public int LastUsedIndex { get; set; } = 0;
        public bool IsFirstRun { get; set; } = true;
        public bool IsAutoStart { get; set; } = false;

        // Путь к базе данных (пустой = папка приложения)

        public string DatabasePath { get; set; } = "";
    }

    public class SettingsService
    {
        private readonly string _settingsPath;

        public AppSettingsData CurrentSettings { get; private set; }

        public SettingsService()
        {
            // Портабельная версия — всё храним в папке программы
            _settingsPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "settings.json");

            CurrentSettings = new AppSettingsData();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var data = JsonSerializer.Deserialize<AppSettingsData>(json);
                    if (data != null)
                    {
                        CurrentSettings = data;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Settings load error: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                string? folder = Path.GetDirectoryName(_settingsPath);
                if (folder != null && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string json = JsonSerializer.Serialize(CurrentSettings);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Settings save error: {ex.Message}");
            }
        }

        public void SetAutoStart(bool enable)
        {
            try
            {
                string appName = "SonaClipboard";
                string exePath = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SonaClipboard.exe");


                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);


                if (key != null)
                {
                    if (enable)
                    {
                        key.SetValue(appName, $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue(appName, false);
                    }
                }

                CurrentSettings.IsAutoStart = enable;
                Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("AutoStart Error: " + ex.Message);
            }
        }
    }
}
