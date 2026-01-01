using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Sona_Clipboard.Models;

using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Sona_Clipboard.Services
{
    public class ClipboardService
    {
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private volatile bool _isCopiedByMe = false;
        public event Action<ClipboardItem>? ClipboardChanged;

        public ClipboardService()
        {
            Clipboard.ContentChanged += Clipboard_ContentChanged;
        }

        private (string AppName, string ProcessName) GetActiveProcessInfo()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return ("Unknown", "Unknown");

                uint processId;
                GetWindowThreadProcessId(hWnd, out processId);

                using (var process = Process.GetProcessById((int)processId))
                {
                    string processName = process.ProcessName;
                    // Try to get a more friendly name (Main window title)
                    string appName = string.IsNullOrWhiteSpace(process.MainWindowTitle)
                        ? processName
                        : process.MainWindowTitle;

                    // Cleanup title (usually apps have 'Title - AppName' or just 'AppName')
                    if (appName.Contains(" - "))
                        appName = appName.Split(" - ").Last();

                    return (appName, processName);
                }
            }
            catch { return ("Unknown", "Unknown"); }
        }

        private async void Clipboard_ContentChanged(object? sender, object? e)
        {
            if (_isCopiedByMe) { _isCopiedByMe = false; return; }

            var (appName, processName) = GetActiveProcessInfo();

            DataPackageView? view = null;
            // Retry with exponential backoff
            for (int i = 0; i < 5; i++)
            {
                try { view = Clipboard.GetContent(); break; }
                catch { await Task.Delay(10 * (int)Math.Pow(2, i)); }
            }
            if (view == null) return;

            try
            {
                if (view.Contains(StandardDataFormats.StorageItems))
                {
                    var storageItems = await view.GetStorageItemsAsync();
                    if (storageItems.Count > 0)
                    {
                        var paths = new List<string>();
                        long totalSize = 0;
                        string extensions = "";

                        foreach (var item in storageItems)
                        {
                            if (!string.IsNullOrEmpty(item.Path))
                            {
                                paths.Add(item.Path);
                                try
                                {
                                    var info = new FileInfo(item.Path);
                                    if (info.Exists)
                                    {
                                        totalSize += info.Length;
                                        extensions += info.Extension.ToLower() + " ";
                                    }
                                }
                                catch { }
                            }
                        }
                        
                        if (paths.Count > 0)
                        {
                            string content = string.Join(Environment.NewLine, paths);
                            ClipboardChanged?.Invoke(new ClipboardItem
                            {
                                Type = "File",
                                Content = content,
                                Timestamp = DateTime.Now.ToString("HH:mm"),
                                SourceAppName = appName,
                                SourceProcessName = processName,
                                // Store metadata in a way FTS can index it
                                RtfContent = $"Size:{totalSize} Ext:{extensions.Trim()}"
                            });
                            return; // Success
                        }
                    }
                    // Fallthrough if no valid paths found
                }
                
                if (view.Contains(StandardDataFormats.Bitmap))
                {
                    RandomAccessStreamReference imageStreamRef = await view.GetBitmapAsync();
                    using (IRandomAccessStreamWithContentType stream = await imageStreamRef.OpenReadAsync())
                    {
                        byte[] imageBytes = new byte[stream.Size];
                        using (DataReader reader = new DataReader(stream)) { await reader.LoadAsync((uint)stream.Size); reader.ReadBytes(imageBytes); }

                        // Generate Thumbnail (max 100px)
                        byte[]? thumbBytes = await CreateThumbnailAsync(imageStreamRef, 100);

                        var item = new ClipboardItem
                        {
                            Type = "Image",
                            Content = "Image " + DateTime.Now.ToString("HH:mm"),
                            ImageBytes = imageBytes,
                            ThumbnailBytes = thumbBytes,
                            Timestamp = DateTime.Now.ToString("HH:mm"),
                            Thumbnail = await BytesToImage(thumbBytes ?? imageBytes), // Show thumb in UI
                            SourceAppName = appName,
                            SourceProcessName = processName
                        };

                        ClipboardChanged?.Invoke(item);
                        return; // Success
                    }
                }
                
                if (view.Contains(StandardDataFormats.Text))
                {
                    string text = await view.GetTextAsync();
                    if (string.IsNullOrWhiteSpace(text)) return;

                    string? rtf = view.Contains(StandardDataFormats.Rtf) ? await view.GetRtfAsync() : null;
                    string? html = view.Contains(StandardDataFormats.Html) ? await view.GetHtmlFormatAsync() : null;

                    ClipboardChanged?.Invoke(new ClipboardItem
                    {
                        Type = "Text",
                        Content = text,
                        RtfContent = rtf,
                        HtmlContent = html,
                        Timestamp = DateTime.Now.ToString("HH:mm"),
                        SourceAppName = appName,
                        SourceProcessName = processName
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Clipboard error", ex);
            }
        }

        private async Task<byte[]?> CreateThumbnailAsync(RandomAccessStreamReference imageRef, uint pixelSize)
        {
            try
            {
                using (var stream = await imageRef.OpenReadAsync())
                {
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                    using (var memoryStream = new InMemoryRandomAccessStream())
                    {
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, memoryStream);
                        encoder.SetSoftwareBitmap(softwareBitmap);
                        encoder.BitmapTransform.ScaledWidth = pixelSize;
                        encoder.BitmapTransform.ScaledHeight = (uint)(decoder.OrientedPixelHeight * pixelSize / decoder.OrientedPixelWidth);
                        encoder.BitmapTransform.InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
                        await encoder.FlushAsync();

                        byte[] bytes = new byte[memoryStream.Size];
                        using (var reader = new DataReader(memoryStream))
                        {
                            await reader.LoadAsync((uint)memoryStream.Size);
                            reader.ReadBytes(bytes);
                        }
                        return bytes;
                    }
                }
            }
            catch { return null; }
        }

        public async Task CopyToClipboard(ClipboardItem item)
        {
            if (item == null) return;
            _isCopiedByMe = true;
            DataPackage package = new DataPackage();
            package.RequestedOperation = DataPackageOperation.Copy;

            try
            {
                if (item.Type == "Text" && item.Content != null)
                {
                    package.SetText(item.Content);
                    if (!string.IsNullOrEmpty(item.RtfContent)) package.SetRtf(item.RtfContent);
                    if (!string.IsNullOrEmpty(item.HtmlContent)) package.SetHtmlFormat(item.HtmlContent);
                }
                else if (item.Type == "Image" && item.ImageBytes != null)
                {
                    InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(item.ImageBytes.AsBuffer());
                    stream.Seek(0);
                    package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                }
                else if (item.Type == "File" && item.Content != null)
                {
                    string[] paths = item.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    List<IStorageItem> storageItems = new List<IStorageItem>();
                    foreach (string path in paths)
                    {
                        try
                        {
                            if (System.IO.File.Exists(path))
                                storageItems.Add(await StorageFile.GetFileFromPathAsync(path));
                            else if (System.IO.Directory.Exists(path))
                                storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                        }
                        catch { }
                    }
                    if (storageItems.Count > 0) package.SetStorageItems(storageItems);
                }

                Clipboard.SetContent(package);
                Clipboard.Flush(); // Ensure data persists
            }
            catch (Exception ex)
            {
                LogService.Error("Copy error", ex);
            }
        }

        public static async Task<BitmapImage?> BytesToImage(byte[]? bytes)
        {
            if (bytes == null) return null;
            try
            {
                using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    BitmapImage image = new BitmapImage();
                    await image.SetSourceAsync(stream);
                    return image;
                }
            }
            catch { return null; }
        }
    }
}