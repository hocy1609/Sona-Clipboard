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
                    // Update schema: add ThumbnailBytes and IsPinned
                    String tableCommand = "CREATE TABLE IF NOT EXISTS History (" +
                                          "Id INTEGER PRIMARY KEY, " + 
                                          "Type NVARCHAR(50), " +
                                          "Content NVARCHAR(2048) NULL, " + 
                                          "ImageBytes BLOB NULL, " + 
                                          "Timestamp NVARCHAR(50), " +
                                          "ThumbnailBytes BLOB NULL, " +
                                          "IsPinned INTEGER DEFAULT 0, " +
                                          "RtfContent TEXT NULL, " +
                                          "HtmlContent TEXT NULL)";
                    new SqliteCommand(tableCommand, db).ExecuteNonQuery();

                    // Migration for existing databases
                    try { new SqliteCommand("ALTER TABLE History ADD COLUMN ThumbnailBytes BLOB NULL", db).ExecuteNonQuery(); } catch { }
                    try { new SqliteCommand("ALTER TABLE History ADD COLUMN IsPinned INTEGER DEFAULT 0", db).ExecuteNonQuery(); } catch { }
                    try { new SqliteCommand("ALTER TABLE History ADD COLUMN RtfContent TEXT NULL", db).ExecuteNonQuery(); } catch { }
                    try { new SqliteCommand("ALTER TABLE History ADD COLUMN HtmlContent TEXT NULL", db).ExecuteNonQuery(); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DB Init Error: {ex.Message}");
            }
        }

        public async Task SaveItemAsync(ClipboardItem item, byte[]? thumbnailBytes = null)
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var insertCommand = new SqliteCommand("INSERT INTO History (Type, Content, ImageBytes, Timestamp, ThumbnailBytes, IsPinned, RtfContent, HtmlContent) " +
                                                          "VALUES (@Type, @Content, @ImageBytes, @Timestamp, @ThumbnailBytes, @IsPinned, @Rtf, @Html)", db);
                    insertCommand.Parameters.AddWithValue("@Type", item.Type);
                    insertCommand.Parameters.AddWithValue("@Content", (object?)item.Content ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@ImageBytes", (object?)item.ImageBytes ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@Timestamp", item.Timestamp);
                    insertCommand.Parameters.AddWithValue("@ThumbnailBytes", (object?)thumbnailBytes ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@IsPinned", item.IsPinned ? 1 : 0);
                    insertCommand.Parameters.AddWithValue("@Rtf", (object?)item.RtfContent ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@Html", (object?)item.HtmlContent ?? DBNull.Value);
                    await insertCommand.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DB Save Error: {ex.Message}");
            }
        }

        public async Task<List<ClipboardItem>> LoadHistoryAsync(string? searchQuery = null)
        {
            var history = new List<ClipboardItem>();
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    
                    string sql = "SELECT Id, Type, Content, Timestamp, IsPinned, ThumbnailBytes, RtfContent, HtmlContent FROM History ";
                    if (!string.IsNullOrWhiteSpace(searchQuery))
                    {
                        sql += "WHERE Content LIKE @query ";
                    }
                    sql += "ORDER BY IsPinned DESC, Id DESC LIMIT 100";

                    var selectCommand = new SqliteCommand(sql, db);
                    if (!string.IsNullOrWhiteSpace(searchQuery))
                    {
                        selectCommand.Parameters.AddWithValue("@query", $"%{searchQuery}%");
                    }

                    using (var query = await selectCommand.ExecuteReaderAsync())
                    {
                        while (await query.ReadAsync())
                        {
                            var item = new ClipboardItem
                            {
                                Id = query.GetInt32(0),
                                Type = query.GetString(1),
                                Timestamp = query.GetString(3),
                                IsPinned = query.GetInt32(4) == 1
                            };
                            if (!await query.IsDBNullAsync(2)) item.Content = query.GetString(2);
                            if (!await query.IsDBNullAsync(6)) item.RtfContent = query.GetString(6);
                            if (!await query.IsDBNullAsync(7)) item.HtmlContent = query.GetString(7);
                            
                            if (!await query.IsDBNullAsync(5))
                            {
                                byte[] thumb = (byte[])query["ThumbnailBytes"];
                                item.Thumbnail = await ClipboardService.BytesToImage(thumb);
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

        public async Task<byte[]?> GetFullImageBytesAsync(int id)
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand("SELECT ImageBytes FROM History WHERE Id = @Id", db);
                    cmd.Parameters.AddWithValue("@Id", id);
                    var result = await cmd.ExecuteScalarAsync();
                    return result as byte[];
                }
            }
            catch { return null; }
        }

        public async Task TogglePinAsync(int id, bool isPinned)
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand("UPDATE History SET IsPinned = @IsPinned WHERE Id = @Id", db);
                    cmd.Parameters.AddWithValue("@IsPinned", isPinned ? 1 : 0);
                    cmd.Parameters.AddWithValue("@Id", id);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { }
        }

        public async Task DeleteItemAsync(int id)
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand("DELETE FROM History WHERE Id = @Id AND IsPinned = 0", db);
                    cmd.Parameters.AddWithValue("@Id", id);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { }
        }

        public async Task TrimHistoryAsync(int limit)
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    // Only delete unpinned items
                    var cmd = new SqliteCommand($"DELETE FROM History WHERE IsPinned = 0 AND Id NOT IN (SELECT Id FROM History ORDER BY Id DESC LIMIT {limit})", db);
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