using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VramTaskManager.Services
{
    public static class IconHelper
    {
        private static readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        public static ImageSource? GetProcessIcon(string? executablePath, string processName)
        {
            string cacheKey = !string.IsNullOrEmpty(executablePath) ? executablePath : processName;

            if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
            {
                return cachedIcon;
            }

            ImageSource? iconSource = null;

            if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
            {
                try
                {
                    using var sysIcon = Icon.ExtractAssociatedIcon(executablePath);
                    if (sysIcon != null)
                    {
                        iconSource = ToImageSource(sysIcon);
                    }
                }
                catch
                {
                    // Fallback to null
                }
            }

            _iconCache[cacheKey] = iconSource;
            return iconSource;
        }

        private static ImageSource? ToImageSource(Icon icon)
        {
            IntPtr hBitmap = icon.ToBitmap().GetHbitmap();
            try
            {
                var imageSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                imageSource.Freeze();
                return imageSource;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
    }
}
