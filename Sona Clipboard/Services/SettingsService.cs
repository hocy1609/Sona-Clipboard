using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

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

        public string HistoryLimit { get; set; } = "20";
        public bool IsFirstRun { get; set; } = true;
        public bool IsAutoStart { get; set; } = false;
    }

    public class SettingsService
    {
        private readonly string _settingsPath;

        public AppSettingsData CurrentSettings { get; private set; }

        public SettingsService()
        {
            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SonaClipboard",
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
                string taskName = "SonaClipboard";
                string exePath = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SonaClipboard.exe");
                string command = $"'{exePath}'";

                var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "schtasks";
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.Verb = "runas";
                process.StartInfo.CreateNoWindow = true;

                if (enable)
                {
                    process.StartInfo.Arguments = $"/Create /SC ONLOGON /TN \"{taskName}\" /TR \"{command}\" /RL HIGHEST /F";
                }
                else
                {
                    process.StartInfo.Arguments = $"/Delete /TN \"{taskName}\" /F";
                }
                
                process.Start();
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
