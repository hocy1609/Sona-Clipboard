using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Linq;

namespace Sona_Clipboard.Models
{
    public class ClipboardItem
    {
        public int Id { get; set; }
        public string Type { get; set; } = "Text";
        public string? Content { get; set; }
        public string? RtfContent { get; set; }
        public string? HtmlContent { get; set; }
        public byte[]? ImageBytes { get; set; }
        public string Timestamp { get; set; } = "";
        public BitmapImage? Thumbnail { get; set; }
        public byte[]? ThumbnailBytes { get; set; }
        public bool IsPinned { get; set; } = false;
        public string? SourceAppName { get; set; }
        public string? SourceProcessName { get; set; }

        // --- НОВОЕ: Умное свойство для отображения в списке ---
        public string DisplayText
        {
            get
            {
                if (Type == "Image") return "[Изображение]";
                if (Type == "File" && !string.IsNullOrEmpty(Content))
                {
                    int count = Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    return count > 1 ? $"📂 Файлов: {count}" : Content;
                }
                return string.IsNullOrWhiteSpace(Content) ? "[Пустой клип]" : Content;
            }
        }
    }
}