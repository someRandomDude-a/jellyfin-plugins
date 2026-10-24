using System;
using System.IO;

namespace Jellyfin.Plugin.UserManager.Tests;

/// <summary>
/// Creates an isolated directory for tests that touch SQLite or the key file.
/// </summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "jellyfin-user-manager-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup; a stale test directory is harmless.
        }
    }
}
