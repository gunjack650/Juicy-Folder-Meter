using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace FolderSizeMeter;
static class SelfTest
{
    public static int Run(string[] args)
    {
        using var mutex = new Mutex(true, "Local\\FolderSizeMeter.Scanner.v1", out bool first);
        if (!first) return 2;
        string root = Path.GetFullPath(args.Length > 1 ? args[1] : "test-work");
        Directory.CreateDirectory(root); Storage.DirectoryPath = Path.Combine(root, "state");
        var log = new List<string>();
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); log.Add("PASS " + label); }
        try
        {
            var settings = new Settings();
            Check(SharedCache.Classify(49_999_999, settings) == 0, "Below orange");
            Check(SharedCache.Classify(50_000_000, settings) == 1, "Exact orange boundary");
            Check(SharedCache.Classify(1_000_000_000, settings) == 1, "Exact red boundary stays orange");
            Check(SharedCache.Classify(1_000_000_001, settings) == 2, "Above red boundary");
            string folder = Path.Combine(root, "fixtures"), child = Path.Combine(folder, "Nested"); Directory.CreateDirectory(child);
            File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[500]); File.WriteAllBytes(Path.Combine(child, "b.bin"), new byte[700]);
            var result = Scanner.Scan(new[] { folder, child }, CancellationToken.None);
            Check(result.Folders.Single(r => r.Path == folder).Bytes == 1200, "Recursive total and overlapping roots");
            Check(result.Folders.Count == 2 && result.Folders.All(r => r.Complete), "Each directory counted once");
            var cancelled = new CancellationToken(true); bool stopped = false;
            try { Scanner.Scan(new[] { folder }, cancelled); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "Cancellation");
            Check(!Scanner.Scan(new[] { Path.Combine(root, "missing") }, CancellationToken.None).Folders.Single().Complete, "Missing folder is incomplete");
            Storage.Write("settings.json", settings); Check(Storage.Read<Settings>("settings.json")!.RedBytes == settings.RedBytes, "Settings persistence");
            File.WriteAllText(Path.Combine(Storage.DirectoryPath,"settings.json"), "bad json"); Check(Storage.Read<Settings>("settings.json") == null, "Corrupt settings handled");
            using var cache = new SharedCache();
            string probe = Path.Combine(AppContext.BaseDirectory, "OverlayProbe.exe"), dll = Path.Combine(AppContext.BaseDirectory, "FolderSizeOverlay.dll");
            void Probe(string path, int expected)
            {
                var start = new ProcessStartInfo(probe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                start.ArgumentList.Add(dll); start.ArgumentList.Add(path); start.ArgumentList.Add(expected.ToString());
                using var process = Process.Start(start)!; string output = process.StandardOutput.ReadToEnd(); process.WaitForExit(); log.Add(output.Trim()); Check(process.ExitCode == 0, $"Native COM lookup {path}: {expected}");
            }
            cache.Publish(result, new Settings { OrangeBytes = 500, RedBytes = 1000 });
            Probe(folder, 2); Probe(child, 1);
            var fake = new Snapshot(DateTime.UtcNow, new() { new(folder, 50_000_000, true), new(child, 1_000_000_001, true), new(root, 5_000_000_000, false) });
            cache.Publish(fake, settings); Probe(folder.ToLowerInvariant(), 1); Probe(child, 2); Probe(root, 0); Probe(folder + "-unknown", 0);
            using (var map = MemoryMappedFile.OpenExisting(SharedCache.Name)) using (var view = map.CreateViewAccessor())
            {
                long tick = view.ReadInt64(16); view.Write(16, Environment.TickCount64 - 600001); Probe(folder, 0); view.Write(16, tick);
                int sequence = view.ReadInt32(0); view.Write(0, sequence | 1); Probe(folder, 0); view.Write(0, sequence);
                view.Write(8, int.MaxValue); Probe(folder, 0);
            }
            cache.Clear(); Probe(child, 0);
            var many = Enumerable.Range(0, 16000).Select(i => new FolderRow(Path.Combine(root, "Folder" + i.ToString("D5")), 50_000_000, true)).ToList();
            many.Add(new FolderRow(Path.Combine(root, "תמונות"), 1_000_000_001, true));
            cache.Publish(new Snapshot(DateTime.UtcNow, many), settings);
            Probe(many[0].Path, 1); Probe(many[15999].Path, 1); Probe(many[16000].Path, 2);
            settings.OrangeBytes = 60_000_000; settings.RedBytes = 2_000_000_000;
            cache.Publish(new Snapshot(DateTime.UtcNow, many), settings); Probe(many[0].Path, 0); Probe(many[16000].Path, 1);
            File.Delete(Path.Combine(child, "b.bin"));
            Check(Scanner.Scan(new[] { folder }, CancellationToken.None).Folders.Single(r => r.Path == folder).Bytes == 500, "Deletion reflected on rescan");
            log.Add("ALL TESTS PASSED"); File.WriteAllLines(Path.Combine(root, "test-results.txt"), log); return 0;
        }
        catch (Exception e) { log.Add("FAIL " + e); File.WriteAllLines(Path.Combine(root, "test-results.txt"), log); return 1; }
    }
}
