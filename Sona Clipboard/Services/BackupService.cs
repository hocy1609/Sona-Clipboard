using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Sona_Clipboard.Services
{
    public class BackupService
    {
        private const string BackupExtension = ".sonabak";
        private const int Iterations = 10000; // For PBKDF2

        public async Task<string> CreateBackupAsync(string dbPath, string targetDir, string? password = null)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
            string backupFileName = $"Sona_Backup_{timestamp}{BackupExtension}";
            string backupPath = Path.Combine(targetDir, backupFileName);

            try
            {
                using (var sourceDb = new SqliteConnection($"Filename={dbPath}"))
                {
                    await sourceDb.OpenAsync();
                    
                    // 1. Generate SQL Dump in memory/temp file
                    // For simplicity and portability, we use a custom export or SQLite's Online Backup
                    // but for "Portable Format" (SQL Dump), we'll stream rows.
                    
                    using (FileStream fs = new FileStream(backupPath, FileMode.Create))
                    using (BrotliStream bs = new BrotliStream(fs, CompressionLevel.Optimal))
                    {
                        if (!string.IsNullOrEmpty(password))
                        {
                            // 2. Encryption Layer
                            await EncryptAndWriteAsync(sourceDb, bs, password);
                        }
                        else
                        {
                            // 2. Plain Compressed Layer
                            await WriteDumpToStreamAsync(sourceDb, bs);
                        }
                    }
                }
                return backupPath;
            }
            catch (Exception ex)
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                throw new Exception($"Backup failed: {ex.Message}");
            }
        }

        private async Task WriteDumpToStreamAsync(SqliteConnection db, Stream targetStream)
        {
            using (var writer = new StreamWriter(targetStream, Encoding.UTF8))
            {
                await writer.WriteLineAsync("-- Sona Clipboard Portable Dump");
                await writer.WriteLineAsync("PRAGMA foreign_keys=OFF;");
                await writer.WriteLineAsync("BEGIN TRANSACTION;");

                string[] tables = { "History" };
                foreach (var table in tables)
                {
                    var cmd = new SqliteCommand($"SELECT * FROM {table}", db);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.Append($"INSERT INTO {table} VALUES(");
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                if (reader.IsDBNull(i)) sb.Append("NULL");
                                else if (reader.GetFieldType(i) == typeof(byte[])) 
                                    sb.Append($"X'{BitConverter.ToString((byte[])reader[i]).Replace("-", "")}'");
                                else 
                                    sb.Append($"'{reader[i].ToString()?.Replace("'", "''")}'");
                                
                                if (i < reader.FieldCount - 1) sb.Append(",");
                            }
                            sb.Append(");");
                            await writer.WriteLineAsync(sb.ToString());
                        }
                    }
                }
                await writer.WriteLineAsync("COMMIT;");
            }
        }

        private async Task EncryptAndWriteAsync(SqliteConnection db, Stream targetStream, string password)
        {
            byte[] salt = new byte[16];
            RandomNumberGenerator.Fill(salt);
            targetStream.Write(salt, 0, 16);

            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
            {
                byte[] key = pbkdf2.GetBytes(32); // AES-256
                byte[] iv = pbkdf2.GetBytes(16);

                using (Aes aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    using (CryptoStream cs = new CryptoStream(targetStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        await WriteDumpToStreamAsync(db, cs);
                    }
                }
            }
        }

        public async Task RestoreAsync(string backupPath, string targetDbPath, string? password = null)
        {
            string tempDb = targetDbPath + ".tmp";
            try
            {
                using (FileStream fs = new FileStream(backupPath, FileMode.Open))
                using (BrotliStream bs = new BrotliStream(fs, CompressionMode.Decompress))
                {
                    // Create empty DB
                    using (var db = new SqliteConnection($"Filename={tempDb}"))
                    {
                        await db.OpenAsync();
                        
                        Stream sourceStream = bs;
                        if (!string.IsNullOrEmpty(password))
                        {
                            sourceStream = await CreateDecryptStreamAsync(bs, password);
                        }

                        using (var reader = new StreamReader(sourceStream))
                        {
                            string sql = await reader.ReadToEndAsync();
                            var cmd = new SqliteCommand(sql, db);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                }
                
                // Swap DBs
                if (File.Exists(targetDbPath)) File.Delete(targetDbPath);
                File.Move(tempDb, targetDbPath);
            }
            catch
            {
                if (File.Exists(tempDb)) File.Delete(tempDb);
                throw;
            }
        }

        private async Task<Stream> CreateDecryptStreamAsync(Stream source, string password)
        {
            byte[] salt = new byte[16];
            await source.ReadAsync(salt, 0, 16);

            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
            {
                byte[] key = pbkdf2.GetBytes(32);
                byte[] iv = pbkdf2.GetBytes(16);

                Aes aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                return new CryptoStream(source, aes.CreateDecryptor(), CryptoStreamMode.Read);
            }
        }
    }
}