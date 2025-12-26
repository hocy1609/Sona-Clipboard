using Microsoft.UI.Xaml.Media.Imaging;

namespace Sona_Clipboard
{
    public class ClipboardItem
    {
        public int Id { get; set; }
        public string Type { get; set; } = "Text";
        public string? Content { get; set; }
        public byte[]? ImageBytes { get; set; }
        public string Timestamp { get; set; } = "";
        public BitmapImage? Thumbnail { get; set; }
    }
}