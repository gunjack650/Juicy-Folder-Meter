using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace FolderSizeMeter;

public sealed record FolderRow(string Path, long Bytes, bool Complete);
public sealed record Snapshot(DateTime CapturedUtc, List<FolderRow> Folders);
public sealed class Settings
{
    public long OrangeBytes { get; set; } = 50_000_000;
    public long RedBytes { get; set; } = 1_000_000_000;
    public List<string> Roots { get; set; } = new();
    public int IntervalMinutes { get; set; } = 2;
    public bool AutoFollowExplorer { get; set; } = true;
    public void Validate()
    {
        if (OrangeBytes < 1 || RedBytes <= OrangeBytes) throw new ArgumentException("Red must be greater than orange, and both must be positive.");
        if (IntervalMinutes < 1 || IntervalMinutes > 5) throw new ArgumentException("Scan interval must be 1–5 minutes.");
    }
}
public static class Storage
{
    public static string DirectoryPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSizeMeter");
    public static T? Read<T>(string name)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(System.IO.Path.Combine(DirectoryPath, name))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
    public static void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = System.IO.Path.Combine(DirectoryPath, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
public static class Scanner
{
    public const int MaximumFolders = 16000;
    public static Snapshot Scan(IEnumerable<string> roots, CancellationToken token, bool throttle = false)
    {
        var rows = new Dictionary<string, FolderRow>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        int entries = 0;
        (long size, bool complete) Visit(string path, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (rows.TryGetValue(path, out var known)) return (known.Bytes, known.Complete);
            if (depth > 128 || ++visited > MaximumFolders || path.Length >= SharedCache.PathChars) return (0, false);
            long total = 0; bool complete = true;
            try
            {
                var info = new DirectoryInfo(path);
                if (!info.Exists) throw new DirectoryNotFoundException(path);
                if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                { rows[path] = new FolderRow(path, 0, false); return (0, false); }
                foreach (var entry in info.EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (throttle && ++entries % 128 == 0 && token.WaitHandle.WaitOne(2)) token.ThrowIfCancellationRequested();
                    if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) { complete = false; continue; }
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        var child = Visit(entry.FullName, depth + 1);
                        total = checked(total + child.size); complete &= child.complete;
                    }
                    else total = checked(total + ((FileInfo)entry).Length);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OverflowException) { complete = false; }
            rows[path] = new FolderRow(path, total, complete);
            return (total, complete);
        }
        var normalized = roots.Select(System.IO.Path.GetFullPath).Select(System.IO.Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToList();
        var selected = new List<string>();
        foreach (var path in normalized)
        {
            token.ThrowIfCancellationRequested();
            if (path.StartsWith("\\\\") || new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) continue;
            if (selected.Any(p => path.StartsWith(p.EndsWith('\\') ? p : p + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            selected.Add(path); Visit(path, 0);
        }
        return new Snapshot(DateTime.UtcNow, rows.Values.ToList());
    }
}
public sealed unsafe class SharedCache : IDisposable
{
    public const int PathChars = 1024, Capacity = 16384, Header = 32, Record = 2056;
    public const long Size = Header + (long)Capacity * Record;
    public const string Name = "Local\\FolderSizeMeter.Cache.v1";
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor view;
    byte* memory;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint CharUpperBuff(StringBuilder text, uint count);
    public static string Key(string path)
    {
        var key = new StringBuilder(System.IO.Path.TrimEndingDirectorySeparator(path));
        CharUpperBuff(key, (uint)key.Length); return key.ToString();
    }
    public SharedCache(string name = Name)
    {
        file = MemoryMappedFile.CreateOrOpen(name, Size, MemoryMappedFileAccess.ReadWrite);
        view = file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref memory);
    }
    public static int Classify(long bytes, Settings settings) => bytes > settings.RedBytes ? 2 : bytes >= settings.OrangeBytes ? 1 : 0;
    public void Publish(Snapshot snapshot, Settings settings)
    {
        var rows = snapshot.Folders.Where(r => r.Complete && r.Path.Length < PathChars).Select(r => (Path: Key(r.Path), Kind: Classify(r.Bytes, settings))).OrderBy(r => r.Path, StringComparer.Ordinal).Take(Capacity).ToArray();
        // Odd sequence: readers fail closed during publication. No Explorer locks or waits.
        ref int sequence = ref *(int*)memory;
        int start = Volatile.Read(ref sequence); if ((start & 1) != 0) start++;
        Interlocked.Exchange(ref sequence, start + 1);
        try
        {
            *(uint*)(memory + 4) = 0x314D5346;
            *(int*)(memory + 8) = rows.Length;
            *(long*)(memory + 16) = Environment.TickCount64;
            for (int i = 0; i < rows.Length; i++)
            {
                byte* row = memory + Header + i * Record;
                var chars = new Span<char>(row, PathChars); chars.Clear(); rows[i].Path.AsSpan().CopyTo(chars);
                *(int*)(row + 2048) = rows[i].Kind;
            }
        }
        finally { Interlocked.Exchange(ref sequence, start + 2); }
    }
    public void Clear() => Publish(new Snapshot(DateTime.UtcNow, new()), new Settings());
    public void Dispose() { Clear(); view.SafeMemoryMappedViewHandle.ReleasePointer(); view.Dispose(); file.Dispose(); }
}
