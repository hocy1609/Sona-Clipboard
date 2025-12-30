using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using Sona_Clipboard.Models;

namespace Sona_Clipboard.Services
{
    public class DatabaseService
    {
        private readonly string _dbPath;

        public string DbPath => _dbPath;

        public DatabaseService()
        {
            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SonaClipboard.db");
            // Initialize synchronously during startup to ensure DB exists
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    db.Open();
                    String tableCommand = "CREATE TABLE IF NOT EXISTS History (" +
                                          "Id INTEGER PRIMARY KEY, " + "Type NVARCHAR(50), " +
                                          "Content NVARCHAR(2048) NULL, " + "ImageBytes BLOB NULL, " + "Timestamp NVARCHAR(50))";
                    new SqliteCommand(tableCommand, db).ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DB Init Error: {ex.Message}");
            }
        }

        public async Task SaveItemAsync(ClipboardItem item)
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var insertCommand = new SqliteCommand("INSERT INTO History VALUES (NULL, @Type, @Content, @ImageBytes, @Timestamp)", db);
                    insertCommand.Parameters.AddWithValue("@Type", item.Type);
                    insertCommand.Parameters.AddWithValue("@Content", (object?)item.Content ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@ImageBytes", (object?)item.ImageBytes ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@Timestamp", item.Timestamp);
                    await insertCommand.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DB Save Error: {ex.Message}");
            }
        }

        public async Task<List<ClipboardItem>> LoadHistoryAsync()
        {
            var history = new List<ClipboardItem>();
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var selectCommand = new SqliteCommand("SELECT * FROM History ORDER BY Id DESC LIMIT 50", db);
                    using (var query = await selectCommand.ExecuteReaderAsync())
                    {
                        while (await query.ReadAsync())
                        {
                            var item = new ClipboardItem
                            {
                                Id = query.GetInt32(0),
                                Type = query.GetString(1),
                                Timestamp = query.GetString(4)
                            };
                            if (!await query.IsDBNullAsync(2)) item.Content = query.GetString(2);
                            if (!await query.IsDBNullAsync(3))
                            {
                                item.ImageBytes = (byte[])query["ImageBytes"];
                                item.Thumbnail = await ClipboardService.BytesToImage(item.ImageBytes);
                                if (item.Content == null) item.Content = "";
                            }
                            history.Add(item);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DB Load Error: {ex.Message}");
            }
            return history;
        }

        public async Task TrimHistoryAsync(int limit)
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand($"DELETE FROM History WHERE Id NOT IN (SELECT Id FROM History ORDER BY Id DESC LIMIT {limit})", db);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { }
        }

        public async Task RemoveDuplicatesAsync()
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand("DELETE FROM History WHERE Id NOT IN (SELECT MAX(Id) FROM History GROUP BY Content, Type)", db);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { }
        }

        public async Task RemoveImagesAsync()
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand("DELETE FROM History WHERE Type = 'Image'", db);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { }
        }

        public async Task RemoveHeavyAsync()
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand("DELETE FROM History WHERE length(ImageBytes) > 2097152", db);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { }
        }

        public async Task ClearAllAsync()
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand("DELETE FROM History", db);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { }
        }
    }
}