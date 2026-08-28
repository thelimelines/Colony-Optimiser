using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ColonyOptimizer.App;

public sealed class IconPathToImageConverter : IValueConverter
{
    private const int DecodePixelSize = 48;
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void ClearCache() => Cache.Clear();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ImageSource image)
        {
            return image;
        }

        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return DependencyProperty.UnsetValue;
        }

        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var loaded = Load(path);
        if (loaded is null)
        {
            return DependencyProperty.UnsetValue;
        }

        return Cache.GetOrAdd(path, loaded);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;

    private static ImageSource? Load(string path)
    {
        try
        {
            var localPath = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : path;
            if (!File.Exists(localPath))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = DecodePixelSize;
            image.UriSource = new Uri(Path.GetFullPath(localPath), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
