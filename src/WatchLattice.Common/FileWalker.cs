namespace WatchLattice.Common;

/// <summary>
/// Lazily enumerates files beneath a set of roots, swallowing the access errors
/// that are routine when walking a live user profile (locked files, denied
/// directories, paths that vanish mid-walk). The walk is read-only.
/// </summary>
public static class FileWalker
{
    public static IEnumerable<string> Enumerate(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        foreach (var root in roots)
        {
            foreach (var file in EnumerateRoot(root, cancellationToken))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> EnumerateRoot(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            string[] subdirs = Array.Empty<string>();
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                // Expected while sweeping a live system; move on.
            }

            foreach (var sub in subdirs)
            {
                pending.Push(sub);
            }

            string[] files = Array.Empty<string>();
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }
}
