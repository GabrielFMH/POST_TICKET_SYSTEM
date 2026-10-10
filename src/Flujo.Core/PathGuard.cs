using Flujo.Documents;

namespace Flujo.Core;

public sealed class PathGuard
{
    private readonly List<string> _roots;

    public PathGuard(IEnumerable<string> allowedRoots)
    {
        _roots = allowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_roots.Count == 0)
        {
            throw new PathNotAllowedException("No hay directorios permitidos.");
        }
    }

    public string RequireAllowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new PathNotAllowedException("La ruta está vacía o no es válida.");
        }

        var full = Path.GetFullPath(path);
        if (!_roots.Any(root => IsInside(full, root)))
        {
            throw new PathNotAllowedException($"La ruta queda fuera de los directorios permitidos: {full}.");
        }

        return full;
    }

    public void EnsureDistinct(string source, string destination)
    {
        var left = Path.GetFullPath(source);
        var right = Path.GetFullPath(destination);
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
        {
            throw new PathNotAllowedException("No se puede sobrescribir el documento original.");
        }
    }

    private static bool IsInside(string fullPath, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullPath.Equals(root, comparison))
        {
            return true;
        }

        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, comparison);
    }
}
