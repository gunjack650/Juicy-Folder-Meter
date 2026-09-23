using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FolderSizeMeter;

public static class ExplorerLocations
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    public static List<string> Read()
    {
        object? shell = null, windows = null;
        var found = new List<(string path, bool foreground)>();
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            windows = ((dynamic)shell!).Windows();
            int count = Math.Min((int)((dynamic)windows).Count, 64);
            for (int i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = ((dynamic)windows).Item(i);
                    if (window == null) continue;
                    string executable = ((dynamic)window).FullName;
                    if (!Path.GetFileName(executable).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string url = ((dynamic)window).LocationURL;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc) continue;
                    string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(uri.LocalPath));
                    if (IsLocal(path)) found.Add((path, new IntPtr((long)((dynamic)window).HWND) == GetForegroundWindow()));
                }
                catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { Release(window); }
            }
        }
        finally { Release(windows); Release(shell); }
        return found.OrderByDescending(p => p.foreground).Select(p => p.path).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
    }
    public static bool IsLocal(string path)
    {
        try { return !path.StartsWith("\\\\") && Path.IsPathFullyQualified(path) && new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed; }
        catch { return false; }
    }
    static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}

// Watches only currently open locations. Callbacks never enumerate or scan.
public sealed class WatchArea : IDisposable
{
    readonly FileSystemWatcher watcher;
    readonly ConcurrentDictionary<string, long> versions = new(StringComparer.OrdinalIgnoreCase);
    long epoch, lastChange;
    public string Path { get; }
    public WatchArea(string path)
    {
        Path = path;
        watcher = new FileSystemWatcher(path) { IncludeSubdirectories = !string.Equals(path, System.IO.Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase), InternalBufferSize = 16384,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes };
        watcher.Changed += (_, e) => Changed(e.FullPath); watcher.Created += (_, e) => Changed(e.FullPath); watcher.Deleted += (_, e) => Changed(e.FullPath);
        watcher.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
        watcher.Error += (_, _) => InvalidateAll(); watcher.EnableRaisingEvents = true;
    }
    public static string ChildFor(string root, string changed)
    {
        string relative = System.IO.Path.GetRelativePath(root, changed);
        if (relative == "." || relative.StartsWith("..") || System.IO.Path.IsPathRooted(relative)) return root;
        return System.IO.Path.Combine(root, relative.Split(System.IO.Path.DirectorySeparatorChar)[0]);
    }
    void Changed(string path)
    {
        Interlocked.Exchange(ref lastChange, Environment.TickCount64);
        if (versions.Count >= 4096) { InvalidateAll(); return; }
        versions.AddOrUpdate(ChildFor(Path, path), 1, (_, v) => v + 1);
    }
    public void InvalidateAll() { Interlocked.Increment(ref epoch); versions.Clear(); }
    public (long all, long child) Version(string child) => (Volatile.Read(ref epoch), versions.TryGetValue(child, out long v) ? v : 0);
    public bool Quiet => Environment.TickCount64 - Volatile.Read(ref lastChange) > 750;
    public void Dispose() => watcher.Dispose();
}

public sealed record AutoUpdate(Snapshot Snapshot, string Status);
public sealed class AutomaticScanner
{
    readonly Settings settings;
    readonly Action<AutoUpdate> publish;
    readonly Func<List<string>> discover;
    readonly CancellationTokenSource stop = new();
    readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<string, WatchArea> watches = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CachedJob> jobs = new(StringComparer.OrdinalIgnoreCase);
    public Task Completion => completion.Task;
    sealed record CachedJob(Snapshot Result, DateTime Scanned, (long, long) Version);
    public AutomaticScanner(Settings settings, Action<AutoUpdate> publish, Func<List<string>>? discover = null)
    { this.settings = settings; this.publish = publish; this.discover = discover ?? ExplorerLocations.Read; }
    public void Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Juicy Folder Meter automatic scanner", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public void Stop() => stop.Cancel();
    static bool SafeDirectory(string path)
    {
        try { return ExplorerLocations.IsLocal(path) && Directory.Exists(path) && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) == 0; }
        catch { return false; }
    }
    void Run()
    {
        try
        {
            var token = stop.Token;
            var roots = new List<string>(); var targets = new List<(string path, string parent)>();
            DateTime discoveryDue = DateTime.MinValue, lastPublish = DateTime.MinValue;
            string? discoveryError = null;
            while (!token.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= discoveryDue)
                {
                    // COM discovery runs on this background STA, never on the Settings or Explorer UI thread.
                    try { roots = discover().Where(SafeDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList(); discoveryError = null; }
                    catch (Exception e) { roots = new(); discoveryError = "Cannot read Explorer locations: " + e.Message; }
                    foreach (var old in watches.Keys.Except(roots, StringComparer.OrdinalIgnoreCase).ToArray()) { watches[old].Dispose(); watches.Remove(old); }
                    foreach (var root in roots)
                        if (!watches.ContainsKey(root)) { try { watches[root] = new WatchArea(root); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { } }
                    targets.Clear();
                    foreach (var root in roots)
                    {
                        try
                        {
                            foreach (var child in new DirectoryInfo(root).EnumerateDirectories().Take(512))
                            {
                                token.ThrowIfCancellationRequested();
                                if ((child.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) == 0 && child.FullName.Length < SharedCache.PathChars)
                                    targets.Add((child.FullName, root));
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    }
                    var wanted = targets.Select(t => t.path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    // Forget deleted/renamed jobs in open locations; keep other recent navigation results briefly.
                    foreach (var old in jobs.Keys.ToArray())
                        if ((roots.Any(r => string.Equals(Path.GetDirectoryName(old), r, StringComparison.OrdinalIgnoreCase)) && !wanted.Contains(old)) || DateTime.UtcNow - jobs[old].Scanned > TimeSpan.FromMinutes(8)) jobs.Remove(old);
                    discoveryDue = DateTime.UtcNow.AddSeconds(2);
                }
                (long,long) Version(string parent, string path) => watches.TryGetValue(parent, out var w) ? w.Version(path) : (0,0);
                bool Due((string path, string parent) t) => !jobs.TryGetValue(t.path, out var job) || DateTime.UtcNow - job.Scanned > TimeSpan.FromMinutes(settings.IntervalMinutes) || job.Version != Version(t.parent, t.path);
                var pending = targets.Where(Due).Where(t => !watches.TryGetValue(t.parent, out var w) || w.Quiet).ToList();
                // Unknown folders first; a huge folder cannot prevent its siblings from receiving results.
                var next = pending.OrderBy(t => jobs.ContainsKey(t.path) ? 1 : 0).FirstOrDefault();
                if (next.path != null)
                {
                    var version = Version(next.parent, next.path);
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(8));
                    Snapshot result;
                    try { result = Scanner.Scan(new[] { next.path }, budget.Token, throttle: true); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { result = new Snapshot(DateTime.UtcNow, new() { new(next.path, 0, false) }); }
                    // If files changed during this traversal, do not publish a supposedly accurate total.
                    if (version != Version(next.parent, next.path)) result = new Snapshot(DateTime.UtcNow, result.Folders.Select(r => r with { Complete = false }).ToList());
                    jobs[next.path] = new CachedJob(result, DateTime.UtcNow, version);
                    // Bound retained results, not just the published mapping, during long navigation sessions.
                    int retained = jobs.Values.Sum(j => j.Result.Folders.Count);
                    foreach (var old in jobs.OrderBy(j => j.Value.Scanned).Select(j => j.Key).ToArray())
                    {
                        if (retained <= Scanner.MaximumFolders && jobs.Count <= 1024) break;
                        if (old == next.path) continue;
                        retained -= jobs[old].Result.Folders.Count; jobs.Remove(old);
                    }
                }
                if (next.path != null || DateTime.UtcNow - lastPublish > TimeSpan.FromSeconds(5))
                {
                    var rows = jobs.Values.OrderByDescending(j => j.Scanned).SelectMany(j => j.Result.Folders).DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Take(Scanner.MaximumFolders).ToList();
                    publish(new AutoUpdate(new Snapshot(DateTime.UtcNow, rows), discoveryError ?? $"Automatic • {roots.Count} Explorer locations • {rows.Count:N0} cached folders • {pending.Count} awaiting calculation. Open a local folder in Explorer."));
                    lastPublish = DateTime.UtcNow;
                }
                if (stop.Token.WaitHandle.WaitOne(next.path != null ? 150 : 500)) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { try { publish(new AutoUpdate(new Snapshot(DateTime.UtcNow, new()), "Automatic scanner stopped: " + e.Message)); } catch { /* Host may already be shutting down. */ } }
        finally { foreach (var w in watches.Values) w.Dispose(); completion.TrySetResult(); }
    }
}
