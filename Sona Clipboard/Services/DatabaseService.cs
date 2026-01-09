using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml.Media.Imaging;
using Sona_Clipboard.Models;
using Windows.Storage.Streams;

namespace Sona_Clipboard.Services
{
    public class DatabaseService
    {
        private readonly string _dbPath;

        public string DbPath => _dbPath;

        /// <summary>
        /// Инициализирует DatabaseService.
        /// </summary>
        /// <param name="customDbPath">Путь к файлу БД (пустой = папка приложения)</param>
        public DatabaseService(string? customDbPath = null)
        {
            // Если путь указан и это директория — добавляем имя файла
            if (!string.IsNullOrWhiteSpace(customDbPath))
            {
                if (Directory.Exists(customDbPath))
                {
                    _dbPath = Path.Combine(customDbPath, "SonaClipboard.db");
                }
                else if (customDbPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                {
                    _dbPath = customDbPath;
                }
                else
                {
                    // Это директория которая ещё не существует
                    Directory.CreateDirectory(customDbPath);
                    _dbPath = Path.Combine(customDbPath, "SonaClipboard.db");
                }
            }
            else
            {
                _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SonaClipboard.db");
            }

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

                    // 1. Performance Optimization PRAGMAs
                    new SqliteCommand("PRAGMA journal_mode=WAL;", db).ExecuteNonQuery();
                    new SqliteCommand("PRAGMA synchronous=NORMAL;", db).ExecuteNonQuery();
                    new SqliteCommand("PRAGMA temp_store=MEMORY;", db).ExecuteNonQuery();
                    new SqliteCommand("PRAGMA mmap_size=30000000000;", db).ExecuteNonQuery(); // Allow memory mapping for large DBs

                    // 2. Schema Creation
                    string tableCommand = "CREATE TABLE IF NOT EXISTS History (" +
                                          "Id INTEGER PRIMARY KEY, " +
                                          "Type NVARCHAR(50), " +
                                          "Content TEXT NULL, " +
                                          "ImageBytes BLOB NULL, " +
                                          "Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP, " +
                                          "ThumbnailBytes BLOB NULL, " +
                                          "IsPinned INTEGER DEFAULT 0, " +
                                          "RtfContent TEXT NULL, " +
                                          "HtmlContent TEXT NULL, " +
                                          "SourceAppName TEXT NULL, " +
                                          "SourceProcessName TEXT NULL, " +
                                          "ContentHash TEXT NULL)"; // For deduplication
                    new SqliteCommand(tableCommand, db).ExecuteNonQuery();

                    // 3. Automated B-Tree Indexing
                    new SqliteCommand("CREATE INDEX IF NOT EXISTS idx_history_pinned_id ON History (IsPinned DESC, Id DESC);", db).ExecuteNonQueryAsync();
                    new SqliteCommand("CREATE INDEX IF NOT EXISTS idx_history_source ON History (SourceProcessName);", db).ExecuteNonQueryAsync();

                    // 4. FTS5 Virtual Table for Mega-Fast Search
                    new SqliteCommand("CREATE VIRTUAL TABLE IF NOT EXISTS History_FTS USING fts5(" +
                                      "Content, SourceAppName, Type, " +
                                      "content='History', content_rowid='Id');", db).ExecuteNonQuery();

                    // Triggers to keep FTS in sync
                    string[] triggers = {
                        "CREATE TRIGGER IF NOT EXISTS History_ai AFTER INSERT ON History BEGIN " +
                        "INSERT INTO History_FTS(rowid, Content, SourceAppName, Type) VALUES (new.Id, new.Content, new.SourceAppName, new.Type); END;",

                        "CREATE TRIGGER IF NOT EXISTS History_ad AFTER DELETE ON History BEGIN " +
                        "INSERT INTO History_FTS(History_FTS, rowid, Content, SourceAppName, Type) VALUES('delete', old.Id, old.Content, old.SourceAppName, old.Type); END;",

                        "CREATE TRIGGER IF NOT EXISTS History_au AFTER UPDATE ON History BEGIN " +
                        "INSERT INTO History_FTS(History_FTS, rowid, Content, SourceAppName, Type) VALUES('delete', old.Id, old.Content, old.SourceAppName, old.Type); " +
                        "INSERT INTO History_FTS(rowid, Content, SourceAppName, Type) VALUES (new.Id, new.Content, new.SourceAppName, new.Type); END;"
                    };
                    foreach (var trigger in triggers) new SqliteCommand(trigger, db).ExecuteNonQuery();

                    // 5. Migration and Maintenance
                    try { new SqliteCommand("ALTER TABLE History ADD COLUMN SourceAppName TEXT NULL", db).ExecuteNonQuery(); } catch { }
                    try { new SqliteCommand("ALTER TABLE History ADD COLUMN SourceProcessName TEXT NULL", db).ExecuteNonQuery(); } catch { }
                    try { new SqliteCommand("ALTER TABLE History ADD COLUMN ContentHash TEXT NULL", db).ExecuteNonQuery(); } catch { }

                    // Periodic Maintenance
                    if (new Random().Next(0, 100) < 5) // 5% chance on startup
                    {
                        new SqliteCommand("PRAGMA optimize;", db).ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("DB Init Error", ex);
            }
        }

        public async Task SaveItemAsync(ClipboardItem item, byte[]? thumbnailBytes = null)
        {
            try
            {
                string hash = CalculateHash(item);

                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();

                    // Deduplication check
                    if (!string.IsNullOrEmpty(hash))
                    {
                        var checkCmd = new SqliteCommand("SELECT Id FROM History WHERE ContentHash = @Hash LIMIT 1", db);
                        checkCmd.Parameters.AddWithValue("@Hash", hash);
                        var existingId = await checkCmd.ExecuteScalarAsync();

                        if (existingId != null)
                        {
                            // Update timestamp of existing item instead of inserting duplicate
                            var updateCmd = new SqliteCommand("UPDATE History SET Timestamp = CURRENT_TIMESTAMP WHERE Id = @Id", db);
                            updateCmd.Parameters.AddWithValue("@Id", (long)existingId);
                            await updateCmd.ExecuteNonQueryAsync();
                            return;
                        }
                    }

                    var insertCommand = new SqliteCommand("INSERT INTO History (Type, Content, ImageBytes, Timestamp, ThumbnailBytes, IsPinned, RtfContent, HtmlContent, ContentHash, SourceAppName, SourceProcessName) " +
                                                          "VALUES (@Type, @Content, @ImageBytes, CURRENT_TIMESTAMP, @ThumbnailBytes, @IsPinned, @Rtf, @Html, @Hash, @SApp, @SProc)", db);
                    insertCommand.Parameters.AddWithValue("@Type", item.Type);
                    insertCommand.Parameters.AddWithValue("@Content", (object?)item.Content ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@ImageBytes", (object?)item.ImageBytes ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@ThumbnailBytes", (object?)thumbnailBytes ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@IsPinned", item.IsPinned ? 1 : 0);
                    insertCommand.Parameters.AddWithValue("@Rtf", (object?)item.RtfContent ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@Html", (object?)item.HtmlContent ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@Hash", (object?)hash ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@SApp", (object?)item.SourceAppName ?? DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@SProc", (object?)item.SourceProcessName ?? DBNull.Value);

                    await insertCommand.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                LogService.Error("DB Save Error", ex);
            }
        }

        public async Task<Dictionary<string, int>> GetAppUsageStatsAsync()
        {
            var stats = new Dictionary<string, int>();
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand("SELECT IFNULL(SourceAppName, 'Unknown'), COUNT(*) as cnt FROM History GROUP BY SourceAppName ORDER BY cnt DESC", db);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            stats[reader.GetString(0)] = reader.GetInt32(1);
                        }
                    }
                }
            }
            catch (Exception ex) { LogService.Error("GetAppUsageStats Error", ex); }
            return stats;
        }

        public async Task DeleteBySourceAsync(string appName)
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    var cmd = new SqliteCommand("DELETE FROM History WHERE SourceAppName = @AppName AND IsPinned = 0", db);
                    cmd.Parameters.AddWithValue("@AppName", appName);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex) { LogService.Error("DeleteBySource Error", ex); }
        }

        private string CalculateHash(ClipboardItem item)
        {
            try
            {
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] inputBytes;
                    if (item.Type == "Image" && item.ImageBytes != null)
                    {
                        inputBytes = item.ImageBytes;
                    }
                    else if (!string.IsNullOrEmpty(item.Content))
                    {
                        inputBytes = System.Text.Encoding.UTF8.GetBytes(item.Content);
                    }
                    else return "";

                    byte[] hashBytes = sha256.ComputeHash(inputBytes);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
            }
            catch { return ""; }
        }

        public async Task<List<ClipboardItem>> LoadHistoryAsync(string? searchQuery = null, bool includeArchive = false)
        {
            // Performance Optimization:
            // We consciously exclude 'RtfContent' and 'HtmlContent' from this initial fetch.
            // These fields can be very large (megabytes) and are not needed for the list view.
            // They are lazy-loaded only when the user copies/previews an item (see GetFullTextContentAsync).
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var history = new List<ClipboardItem>();
            string archivePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "SonaArchive.db");

            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();

                    // Register REGEXP function for advanced users
                    db.CreateFunction("REGEXP", (string pattern, string input) =>
                        System.Text.RegularExpressions.Regex.IsMatch(input ?? "", pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));

                    string ftsQuery = ParseAdvancedQuery(searchQuery);

                    string sql;
                    if (includeArchive && File.Exists(archivePath))
                    {
                        await new SqliteCommand($"ATTACH DATABASE '{archivePath}' AS Arc", db).ExecuteNonQueryAsync();

                        sql = $@"SELECT h.Id, h.Type, h.Content, h.Timestamp, h.IsPinned, h.ThumbnailBytes, h.SourceAppName
                                 FROM History h
                                 JOIN History_FTS f ON f.rowid = h.Id
                                 WHERE {ftsQuery}
                                 UNION ALL
                                 SELECT h.Id, h.Type, h.Content, h.Timestamp, h.IsPinned, h.ThumbnailBytes, h.SourceAppName
                                 FROM Arc.History h
                                 -- Note: Archive FTS would need separate virtual table, but we'll use LIKE for archive for simplicity or simple FTS if created
                                 WHERE h.Content LIKE @likeQuery
                                 ORDER BY IsPinned DESC, Timestamp DESC LIMIT 100";
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(searchQuery))
                        {
                            sql = "SELECT Id, Type, Content, Timestamp, IsPinned, ThumbnailBytes, SourceAppName FROM History ORDER BY IsPinned DESC, Timestamp DESC LIMIT 100";
                        }
                        else
                        {
                            sql = $@"SELECT h.Id, h.Type, h.Content, h.Timestamp, h.IsPinned, h.ThumbnailBytes, h.SourceAppName
                                     FROM History h
                                     JOIN History_FTS f ON f.rowid = h.Id
                                     WHERE {ftsQuery}
                                     ORDER BY h.IsPinned DESC, h.Timestamp DESC LIMIT 100";
                        }
                    }

                    var selectCommand = new SqliteCommand(sql, db);
                    if (!string.IsNullOrWhiteSpace(searchQuery))
                    {
                        selectCommand.Parameters.AddWithValue("@query", searchQuery);
                        selectCommand.Parameters.AddWithValue("@likeQuery", $"%{searchQuery}%");
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
                                IsPinned = query.GetInt32(4) == 1,
                                SourceAppName = query.IsDBNull(6) ? null : query.GetString(6)
                            };
                            if (!await query.IsDBNullAsync(2)) item.Content = query.GetString(2);

                            if (!await query.IsDBNullAsync(5))
                            {
                                byte[] thumb = (byte[])query["ThumbnailBytes"];
                                item.Thumbnail = await ClipboardService.BytesToImage(thumb);
                            }
                            history.Add(item);
                        }
                    }

                    if (includeArchive && File.Exists(archivePath))
                    {
                        await new SqliteCommand("DETACH DATABASE Arc", db).ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("DB Load Error", ex);
            }
            finally
            {
                stopwatch.Stop();
                // Measure impact: Log time and count
                System.Diagnostics.Debug.WriteLine($"[PERF] LoadHistoryAsync: Loaded {history.Count} items in {stopwatch.ElapsedMilliseconds}ms (Lazy loaded RTF/HTML skipped)");
            }
            return history;
        }

        private string ParseAdvancedQuery(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "1=1";

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ftsParts = new List<string>();
            var sqlFilters = new List<string>();

            foreach (var part in parts)
            {
                if (part.StartsWith("app:")) ftsParts.Add($"SourceAppName:{SanitizeFtsInput(part.Substring(4))}*");
                else if (part.StartsWith("type:")) ftsParts.Add($"Type:{SanitizeFtsInput(part.Substring(5))}*");
                else if (part.StartsWith("regex:"))
                {
                    // Для regex используем параметризованный подход через LIKE вместо REGEXP для безопасности
                    string sanitized = SanitizeSqlInput(part.Substring(6));
                    sqlFilters.Add($"h.Content LIKE '%{sanitized}%'");
                }
                else if (part.StartsWith("ext:")) ftsParts.Add($"Content:*{SanitizeFtsInput(part.Substring(4))}*");
                else ftsParts.Add($"{SanitizeFtsInput(part)}*");
            }

            string result = "";
            if (ftsParts.Count > 0)
            {
                string combinedFts = string.Join(" AND ", ftsParts);
                result = $"History_FTS MATCH '{combinedFts}'";
            }

            if (sqlFilters.Count > 0)
            {
                string combinedSql = string.Join(" AND ", sqlFilters);
                result = string.IsNullOrEmpty(result) ? combinedSql : $"{result} AND {combinedSql}";
            }

            return string.IsNullOrEmpty(result) ? "1=1" : result;
        }

        private static string SanitizeFtsInput(string input)
        {
            // Экранируем специальные символы FTS5
            return input.Replace("'", "''").Replace("\"", "").Replace("*", "").Replace(":", "");
        }

        private static string SanitizeSqlInput(string input)
        {
            // Экранируем SQL injection символы
            return input.Replace("'", "''").Replace(";", "").Replace("--", "").Replace("/*", "").Replace("*/", "");
        }

        public async Task PerformMaintenanceAsync()
        {
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();
                    // Optimization and defragmentation
                    await new SqliteCommand("PRAGMA optimize;", db).ExecuteNonQueryAsync();
                    await new SqliteCommand("PRAGMA incremental_vacuum;", db).ExecuteNonQueryAsync();
                }

                // 1-Hour RPO Backup
                await BackupDatabaseAsync();
            }
            catch { }
        }

        private async Task BackupDatabaseAsync()
        {
            string backupPath = _dbPath + ".bak";
            try
            {
                using (var source = new SqliteConnection($"Filename={_dbPath}"))
                using (var destination = new SqliteConnection($"Filename={backupPath}"))
                {
                    await source.OpenAsync();
                    await destination.OpenAsync();
                    source.BackupDatabase(destination); // SQLite online backup API
                }
            }
            catch { }
        }

        public async Task ArchiveOldItemsAsync(int daysToKeep = 30)
        {
            string archivePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "SonaArchive.db");
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();

                    // ATTACH archive database (Sharding strategy)
                    var attachCmd = new SqliteCommand($"ATTACH DATABASE '{archivePath}' AS Archive", db);
                    await attachCmd.ExecuteNonQueryAsync();

                    // Create table in archive if not exists
                    await new SqliteCommand("CREATE TABLE IF NOT EXISTS Archive.History AS SELECT * FROM History WHERE 1=0", db).ExecuteNonQueryAsync();

                    // Move old unpinned items to archive
                    var moveCmd = new SqliteCommand(
                        "INSERT INTO Archive.History SELECT * FROM History " +
                        "WHERE IsPinned = 0 AND datetime(Timestamp) < datetime('now', @days); " +
                        "DELETE FROM History WHERE IsPinned = 0 AND datetime(Timestamp) < datetime('now', @days);", db);
                    moveCmd.Parameters.AddWithValue("@days", $"-{daysToKeep} days");

                    await moveCmd.ExecuteNonQueryAsync();
                    await new SqliteCommand("DETACH DATABASE Archive", db).ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Archiving Error: {ex.Message}");
            }
        }

        public async Task<byte[]?> GetFullImageBytesAsync(int id)
        {
            string archivePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "SonaArchive.db");
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();

                    // Try Main DB first
                    var cmd = new SqliteCommand("SELECT ImageBytes FROM History WHERE Id = @Id", db);
                    cmd.Parameters.AddWithValue("@Id", id);
                    var result = await cmd.ExecuteScalarAsync();
                    if (result is byte[] bytes) return bytes;

                    // Try Archive DB
                    if (File.Exists(archivePath))
                    {
                        await new SqliteCommand($"ATTACH DATABASE '{archivePath}' AS Arc", db).ExecuteNonQueryAsync();
                        var arcCmd = new SqliteCommand("SELECT ImageBytes FROM Arc.History WHERE Id = @Id", db);
                        arcCmd.Parameters.AddWithValue("@Id", id);
                        var arcResult = await arcCmd.ExecuteScalarAsync();
                        await new SqliteCommand("DETACH DATABASE Arc", db).ExecuteNonQueryAsync();
                        return arcResult as byte[];
                    }
                }
            }
            catch { return null; }
            return null;
        }

        public async Task<(string? Rtf, string? Html)> GetFullTextContentAsync(int id)
        {
            string archivePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "SonaArchive.db");
            try
            {
                using (var db = new SqliteConnection($"Filename={_dbPath}"))
                {
                    await db.OpenAsync();

                    // Try Main DB first
                    var cmd = new SqliteCommand("SELECT RtfContent, HtmlContent FROM History WHERE Id = @Id", db);
                    cmd.Parameters.AddWithValue("@Id", id);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            string? rtf = reader.IsDBNull(0) ? null : reader.GetString(0);
                            string? html = reader.IsDBNull(1) ? null : reader.GetString(1);
                            // Found in main DB, return result (even if nulls) to avoid checking archive
                            return (rtf, html);
                        }
                    }

                    // Try Archive DB
                    if (File.Exists(archivePath))
                    {
                        await new SqliteCommand($"ATTACH DATABASE '{archivePath}' AS Arc", db).ExecuteNonQueryAsync();
                        var arcCmd = new SqliteCommand("SELECT RtfContent, HtmlContent FROM Arc.History WHERE Id = @Id", db);
                        arcCmd.Parameters.AddWithValue("@Id", id);

                        string? rtf = null, html = null;
                        using (var reader = await arcCmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                rtf = reader.IsDBNull(0) ? null : reader.GetString(0);
                                html = reader.IsDBNull(1) ? null : reader.GetString(1);
                            }
                        }

                        await new SqliteCommand("DETACH DATABASE Arc", db).ExecuteNonQueryAsync();
                        return (rtf, html);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("GetFullTextContentAsync Error", ex);
            }
            return (null, null);
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

        public async Task TrimHistoryBySizeAsync(double maxGb)
        {
            try
            {
                long maxBytes = (long)(maxGb * 1024 * 1024 * 1024);
                var fileInfo = new FileInfo(_dbPath);

                if (fileInfo.Length > maxBytes)
                {
                    using (var db = new SqliteConnection($"Filename={_dbPath}"))
                    {
                        await db.OpenAsync();
                        // Delete oldest unpinned items until size is manageable
                        // Since we can't easily shrink file while open, we delete records and depend on Incremental Vacuum
                        var cmd = new SqliteCommand("DELETE FROM History WHERE IsPinned = 0 AND Id IN (SELECT Id FROM History WHERE IsPinned = 0 ORDER BY Id ASC LIMIT 50)", db);
                        await cmd.ExecuteNonQueryAsync();
                    }
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