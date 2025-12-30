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

        // --- НОВОЕ: Умное свойство для отображения в списке ---
        public string DisplayText
        {
            get
            {
                if (Type == "File" && !string.IsNullOrEmpty(Content))
                {
                    // Считаем количество строк (путей к файлам)
                    int count = Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    if (count > 1)
                        return $"📂 Скопировано файлов: {count}";
                    else
                        return Content; // Если файл один, показываем путь
                }
                return Content ?? "";
            }
        }
    }
}