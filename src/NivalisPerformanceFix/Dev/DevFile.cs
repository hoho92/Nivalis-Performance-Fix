using System.IO;
using BepInEx;

namespace NivalisPerformanceFix.Dev;

/// <summary>Developer log files under BepInEx/, appended across sessions; past 20 MB the old one becomes *.old.</summary>
internal static class DevFile
{
    private const long MaxBytes = 20L * 1024 * 1024;

    public static StreamWriter Open(string name)
    {
        string path = Path.Combine(Paths.BepInExRootPath, name);
        if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".old", true);
        return new StreamWriter(path, true) { AutoFlush = true };
    }
}
