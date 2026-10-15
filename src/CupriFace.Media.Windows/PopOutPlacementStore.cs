using System.Globalization;

namespace CupriFace.Media.Windows;

/// <summary>Small, non-sensitive persistence for the user's last pop-out rectangle.</summary>
internal sealed class PopOutPlacementStore(string path)
{
    private readonly object _gate = new();

    internal PopOutBounds? Load()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(path)) return null;
                var parts = File.ReadAllText(path).Split(',');
                if (parts.Length is < 4 or > 6) return null;
                var maximized = parts.Length == 5
                    && bool.TryParse(parts[4], out var parsedMaximized)
                    && parsedMaximized;
                if (parts.Length == 6)
                    maximized = bool.TryParse(parts[4], out parsedMaximized) && parsedMaximized;
                var fullscreen = parts.Length == 6
                    && bool.TryParse(parts[5], out var parsedFullscreen)
                    && parsedFullscreen;
                return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                    && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                    && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                    && width > 0 && height > 0
                        ? new PopOutBounds(x, y, width, height, maximized, fullscreen)
                        : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void Save(PopOutBounds bounds)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(
                    path,
                    string.Join(',',
                        bounds.X.ToString(CultureInfo.InvariantCulture),
                        bounds.Y.ToString(CultureInfo.InvariantCulture),
                        bounds.Width.ToString(CultureInfo.InvariantCulture),
                        bounds.Height.ToString(CultureInfo.InvariantCulture),
                        bounds.Maximized.ToString(CultureInfo.InvariantCulture),
                        bounds.Fullscreen.ToString(CultureInfo.InvariantCulture)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Geometry is a convenience. Playback and docking must not depend on persistence.
        }
    }
}
