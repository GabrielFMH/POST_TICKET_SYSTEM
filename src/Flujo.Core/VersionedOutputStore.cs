using System.Globalization;

namespace Flujo.Core;

public sealed class VersionedOutputStore
{
    public string Create(string outputRoot, string slug, DateTimeOffset timestamp)
    {
        var safeSlug = Sanitize(slug);
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(outputRoot, safeSlug, stamp);
        var unique = directory;
        var suffix = 1;
        while (Directory.Exists(unique))
        {
            unique = directory + "-" + suffix;
            suffix++;
        }

        Directory.CreateDirectory(unique);
        return unique;
    }

    public static string Sanitize(string value)
    {
        var chars = value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray();
        var cleaned = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "documento" : cleaned;
    }
}
