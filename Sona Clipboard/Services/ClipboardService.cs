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

namespace Sona_Clipboard.Services
{
    public class ClipboardService
    {
        private bool _isCopiedByMe = false;
        public event Action<ClipboardItem>? ClipboardChanged;

        public ClipboardService()
        {
            Clipboard.ContentChanged += Clipboard_ContentChanged;
        }

        private async void Clipboard_ContentChanged(object? sender, object? e)
        {
            if (_isCopiedByMe) { _isCopiedByMe = false; return; }

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
                if (view.Contains(StandardDataFormats.Text))
                {
                    string text = await view.GetTextAsync();
                    string? rtf = view.Contains(StandardDataFormats.Rtf) ? await view.GetRtfAsync() : null;
                    string? html = view.Contains(StandardDataFormats.Html) ? await view.GetHtmlFormatAsync() : null;

                    ClipboardChanged?.Invoke(new ClipboardItem 
                    { 
                        Type = "Text", 
                        Content = text, 
                        RtfContent = rtf,
                        HtmlContent = html,
                        Timestamp = DateTime.Now.ToString("HH:mm") 
                    });
                }
                else if (view.Contains(StandardDataFormats.StorageItems))
                {
                    var storageItems = await view.GetStorageItemsAsync();
                    if (storageItems.Count == 0) return;

                    var paths = new List<string>();
                    foreach (var item in storageItems) if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
                    if (paths.Count == 0) return;

                    string content = string.Join(Environment.NewLine, paths);
                    ClipboardChanged?.Invoke(new ClipboardItem { Type = "File", Content = content, Timestamp = DateTime.Now.ToString("HH:mm") });
                }
                else if (view.Contains(StandardDataFormats.Bitmap))
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
                            Thumbnail = await BytesToImage(thumbBytes ?? imageBytes) // Show thumb in UI
                        };
                        
                        // We attach thumbBytes to a temporary field if we want, or just pass it to Invoke
                        // For simplicity, let's assume SaveItemAsync will handle it.
                        ClipboardChanged?.Invoke(item);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard error: {ex.Message}");
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
                        if (System.IO.File.Exists(path) || Directory.Exists(path))
                        {
                            try { storageItems.Add(await StorageFile.GetFileFromPathAsync(path)); } catch { }
                        }
                    }
                    if (storageItems.Count > 0) package.SetStorageItems(storageItems);
                }

                Clipboard.SetContent(package);
                if (item.Type == "Text") await Task.Delay(50); else await Task.Delay(200);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Copy error: {ex.Message}");
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
