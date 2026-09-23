namespace FolderSizeMeter;
static class AutomaticTests
{
    public static int Run(string directory)
    {
        var log = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); log.Add("PASS " + name); }
        Directory.CreateDirectory(directory);
        string root = Path.Combine(directory, "auto-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        AutomaticScanner? scanner = null;
        try
        {
            string child = Path.Combine(root, "תמונות"), sibling = Path.Combine(root, "Other");
            Directory.CreateDirectory(child); Directory.CreateDirectory(sibling);
            File.WriteAllBytes(Path.Combine(child, "file.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(sibling, "file.bin"), new byte[200]);
            Check(WatchArea.ChildFor(root, Path.Combine(child, "nested", "file")) == child, "Nested event maps to direct child");
            Check(!ExplorerLocations.IsLocal(@"\\server\share"), "Network roots excluded");
            Check(new Settings().AutoFollowExplorer, "Automatic mode enabled for migrated settings");
            AutoUpdate? latest = null; object gate = new();
            string currentRoot = root;
            scanner = new AutomaticScanner(new Settings(), update => { lock (gate) latest = update; }, () => new List<string> { Volatile.Read(ref currentRoot) });
            scanner.Start();
            bool WaitFor(Func<Snapshot, bool> predicate)
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline) { lock (gate) if (latest != null && predicate(latest.Snapshot)) return true; Thread.Sleep(100); }
                return false;
            }
            Check(WaitFor(s => s.Folders.Any(r => r.Path == child && r.Bytes == 100 && r.Complete) && s.Folders.Any(r => r.Path == sibling && r.Bytes == 200 && r.Complete)), "Automatic discovery scans both children without manual roots");
            File.WriteAllBytes(Path.Combine(child, "file.bin"), new byte[400]);
            Check(WaitFor(s => s.Folders.Any(r => r.Path == child && r.Bytes == 400 && r.Complete)), "File watcher refreshes changed size before periodic scan");
            string renamed = Path.Combine(root, "Renamed"); Directory.Move(child, renamed);
            Check(WaitFor(s => !s.Folders.Any(r => r.Path == child) && s.Folders.Any(r => r.Path == renamed && r.Bytes == 400)), "Rename removes old cache and calculates new folder");
            File.Delete(Path.Combine(renamed, "file.bin"));
            Check(WaitFor(s => s.Folders.Any(r => r.Path == renamed && r.Bytes == 0 && r.Complete)), "Deletion updates total");
            using (var watcher = new WatchArea(root)) { var before = watcher.Version(sibling); watcher.InvalidateAll(); Check(before != watcher.Version(sibling), "Watcher overflow recovery invalidates every child"); }
            string nextRoot = Path.Combine(directory, "navigation-" + Guid.NewGuid().ToString("N"));
            string nextChild = Path.Combine(nextRoot, "Next"); Directory.CreateDirectory(nextChild);
            File.WriteAllBytes(Path.Combine(nextChild, "file.bin"), new byte[321]);
            Volatile.Write(ref currentRoot, nextRoot);
            Check(WaitFor(s => s.Folders.Any(r => r.Path == nextChild && r.Bytes == 321 && r.Complete)), "Navigation discovers and scans a newly opened location");
            scanner.Stop(); Check(scanner.Completion.Wait(TimeSpan.FromSeconds(3)), "Automatic worker stops and releases watchers");
            log.Add("ALL AUTOMATIC TESTS PASSED"); File.WriteAllLines(Path.Combine(directory, "automatic-tests.txt"), log); return 0;
        }
        catch (Exception e) { log.Add("FAIL " + e); File.WriteAllLines(Path.Combine(directory, "automatic-tests.txt"), log); return 1; }
        finally { scanner?.Stop(); }
    }
}
