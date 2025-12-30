using System;
using Microsoft.UI.Xaml.Data;

namespace Sona_Clipboard.Converters
{
    public class TypeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string? type = value as string;
            if (type == "Image") return "\uEB9F";
            if (type == "File") return "\uE8B7";
            return "\uE8C4";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
