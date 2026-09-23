using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FolderSizeMeter;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--explorer-probe")) { try { File.WriteAllLines(args[1], ExplorerLocations.Read()); return 0; } catch (Exception e) { File.WriteAllText(args[1], "Explorer access failed: " + e.Message); return 1; } }
        if (args.Contains("--automatic-test")) return AutomaticTests.Run(args[1]);
        if (args.Contains("--render-ui"))
        {
            Storage.DirectoryPath = Path.GetFullPath(args[1]);
            using var form = new SettingsWindow(false, preview: true);
            void Realize(Control c) { _ = c.Handle; foreach (Control child in c.Controls) Realize(child); c.PerformLayout(); }
            Realize(form); form.PerformLayout();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(args[2]); return 0;
        }
        if (args.Contains("--self-test")) return SelfTest.Run(args);
        using var mutex = new Mutex(true, "Local\\FolderSizeMeter.Scanner.v1", out bool first);
        if (!first) { MessageBox.Show("Juicy Folder Meter is already running. Open Settings from its tray icon."); return 0; }
        try { using var window = new SettingsWindow(args.Contains("--tray")); Application.Run(window); return 0; }
        catch (Exception e) { MessageBox.Show(e.Message, "Juicy Folder Meter"); return 1; }
    }
}
sealed class SettingsWindow : Form
{
    Settings settings;
    readonly SharedCache cache;
    readonly NotifyIcon tray;
    readonly ListBox roots = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly NumericUpDown orange = new() { Maximum = 100000000, Minimum = 0.01m, DecimalPlaces = 2, Width = 150 };
    readonly NumericUpDown red = new() { Maximum = 100000000, Minimum = 0.01m, DecimalPlaces = 2, Width = 150 };
    readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 5, Width = 65 };
    readonly CheckBox automatic = new() { Text = "Automatically follow Explorer — uncheck to use the manual folder list", AutoSize = true, Checked = true };
    AutomaticScanner? autoScanner;
    int autoGeneration;
    DateTime saveAutoDue = DateTime.MinValue;
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(720, 0), Text = "Open a local folder in Explorer. Sizes are calculated automatically in the background." };
    readonly DataGridView table = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 10000 };
    CancellationTokenSource? scanCancellation;
    Task? scanTask;
    Snapshot? snapshot;
    DateTime nextScan = DateTime.MinValue;
    bool exiting, startHidden;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(uint change, uint flags, string? item1, IntPtr item2);
    public SettingsWindow(bool hidden, bool preview = false)
    {
        cache = new SharedCache(preview ? SharedCache.Name + ".Preview." + Guid.NewGuid().ToString("N") : SharedCache.Name);
        startHidden = hidden;
        settings = Storage.Read<Settings>("settings.json") ?? new Settings();
        try { settings.Validate(); if (settings.Roots == null) settings.Roots = new(); } catch { settings = new Settings(); }
        Text = "Juicy Folder Meter 1.1 — Automatic"; MinimumSize = new Size(820, 720); Size = new Size(940, 800); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(246, 248, 251);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 10 };
        Controls.Add(layout);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label { Text = "Juicy Folder Meter", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { Text = "A small size marker. Your original folder icon and preview stay intact.", AutoSize = true, Margin = new Padding(0, 8, 0, 16) });
        var thresholdPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        thresholdPanel.Controls.Add(new Label { Text = "● Orange from (MB)", ForeColor = Color.DarkOrange, AutoSize = true, Margin = new Padding(0, 7, 8, 0) }); thresholdPanel.Controls.Add(orange);
        thresholdPanel.Controls.Add(new Label { Text = "● Red above (MB)", ForeColor = Color.Firebrick, AutoSize = true, Margin = new Padding(16, 7, 8, 0) }); thresholdPanel.Controls.Add(red); layout.Controls.Add(thresholdPanel);
        layout.Controls.Add(new Label { Text = "Decimal units: 1 MB = 1,000,000 bytes. Defaults: 50 MB / 1,000 MB (1 GB).", AutoSize = true, Margin = new Padding(0, 6, 0, 16) });
        automatic.Checked = settings.AutoFollowExplorer; layout.Controls.Add(automatic);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.Controls.Add(roots);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 12) };
        Button Button(string text, EventHandler click) { var b = new Button { Text = text, AutoSize = true, Height = 34 }; b.Click += click; return b; }
        actions.Controls.Add(Button("Add folder", (_, _) => { using var picker = new FolderBrowserDialog(); if (picker.ShowDialog(this) == DialogResult.OK && !roots.Items.Contains(picker.SelectedPath)) roots.Items.Add(picker.SelectedPath); }));
        actions.Controls.Add(Button("Remove", (_, _) => { if (roots.SelectedItem != null) roots.Items.Remove(roots.SelectedItem); }));
        actions.Controls.Add(new Label { Text = "Scan every (min)", AutoSize = true, Margin = new Padding(12, 8, 6, 0) }); actions.Controls.Add(interval);
        actions.Controls.Add(Button("Save && scan", async (_, _) => await SaveAndScan()));
        actions.Controls.Add(Button("Pause", async (_, _) => { await StopAutomatic(); scanCancellation?.Cancel(); nextScan = DateTime.MaxValue; status.Text = "Paused. Choose Save & scan to resume."; })); layout.Controls.Add(actions);
        layout.Controls.Add(status);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Columns.Add("folder", "Folder"); table.Columns.Add("size", "Size (MB)"); table.Columns.Add("marker", "Status"); table.Columns[0].FillWeight = 65; table.Columns[1].FillWeight = 15; table.Columns[2].FillWeight = 20;
        layout.Controls.Add(table);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers");
        string[] names = key?.GetSubKeyNames() ?? Array.Empty<string>();
        bool registered = names.Contains("FolderSizeMeter Orange") && names.Contains("FolderSizeMeter Red");
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(840, 0), Margin = new Padding(0, 12, 0, 0), ForeColor = names.Length >= 15 ? Color.Firebrick : Color.DimGray, Text = $"Explorer integration: {(registered ? "registered" : "not installed — see Install.cmd")}. Registered overlay handlers: {names.Length}.\n{(names.Length >= 15 ? "Overlay slots are oversubscribed: size markers may not appear on this PC." : "Windows has only 15 overlay slots; other apps may take precedence.")}" });
        orange.Value = Math.Clamp(settings.OrangeBytes / 1000000m, orange.Minimum, orange.Maximum); red.Value = Math.Clamp(settings.RedBytes / 1000000m, red.Minimum, red.Maximum); interval.Value = settings.IntervalMinutes;
        roots.Items.AddRange(settings.Roots.ToArray());
        tray = new NotifyIcon { Icon = SystemIcons.Information, Text = "Juicy Folder Meter", Visible = true };
        var menu = new ContextMenuStrip(); menu.Items.Add("Settings", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); }); menu.Items.Add("Scan now", null, async (_, _) => await StartScan()); menu.Items.Add("Exit", null, async (_, _) => await ExitApp()); tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => { Show(); Activate(); };
        timer.Tick += async (_, _) => { if (autoScanner == null && DateTime.UtcNow >= nextScan) await StartScan(); }; timer.Start();
        Shown += async (_, _) => { if (startHidden) Hide(); await StartScan(); };
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
    async Task SaveAndScan()
    {
        try
        {
            var updated = new Settings { OrangeBytes = (long)(orange.Value * 1000000), RedBytes = (long)(red.Value * 1000000), IntervalMinutes = (int)interval.Value, Roots = roots.Items.Cast<string>().ToList(), AutoFollowExplorer = automatic.Checked }; updated.Validate();
            foreach (string root in updated.Roots) if (root.StartsWith("\\\\") || new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed) throw new ArgumentException("Please choose folders on a local fixed drive.");
            await StopAutomatic(); scanCancellation?.Cancel(); if (scanTask != null) await scanTask;
            Storage.Write("settings.json", updated);
            cache.Clear();
            if (snapshot != null) await Task.Run(() => RefreshChanged(snapshot, new Snapshot(DateTime.UtcNow, new())));
            snapshot = null; settings = updated;
            RefreshRoots(); await StartScan();
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Settings"); }
    }
    Task StartScan()
    {
        if (!exiting && settings.AutoFollowExplorer)
        {
            if (autoScanner == null)
            {
                status.Text = "Following Explorer automatically. Open any local folder; markers will appear as sizes become available.";
                int generation = ++autoGeneration;
                autoScanner = new AutomaticScanner(settings, update =>
                {
                    if (Volatile.Read(ref autoGeneration) != generation) return;
                    cache.Publish(update.Snapshot, settings);
                    if (DateTime.UtcNow >= saveAutoDue) { try { Storage.Write("snapshot.json", update.Snapshot); } catch (IOException) { } catch (UnauthorizedAccessException) { } saveAutoDue = DateTime.UtcNow.AddMinutes(1); }
                    if (!IsDisposed && IsHandleCreated) BeginInvoke(() =>
                    {
                        if (exiting || autoScanner == null || autoGeneration != generation) return;
                        var previous = snapshot; snapshot = update.Snapshot;
                        RenderRows(snapshot); status.Text = update.Status;
                        RefreshChanged(previous, snapshot);
                    });
                });
                autoScanner.Start();
            }
            return Task.CompletedTask;
        }
        if (exiting || scanTask is { IsCompleted: false }) return Task.CompletedTask;
        scanTask = ScanAsync(); return scanTask;
    }
    async Task StopAutomatic()
    {
        var previous = autoScanner; autoScanner = null;
        Interlocked.Increment(ref autoGeneration);
        if (previous != null) { previous.Stop(); await previous.Completion; }
    }
    void RenderRows(Snapshot data)
    {
        table.Rows.Clear();
        foreach (var row in data.Folders.OrderByDescending(r => r.Bytes).Take(500))
        {
            int kind = SharedCache.Classify(row.Bytes, settings);
            int index = table.Rows.Add(row.Path, (row.Bytes / 1000000m).ToString("N2"), !row.Complete ? "Pending / incomplete" : kind == 2 ? "● Red" : kind == 1 ? "● Orange" : "Normal");
            table.Rows[index].Cells[2].Style.ForeColor = !row.Complete ? Color.DimGray : kind == 2 ? Color.Firebrick : kind == 1 ? Color.DarkOrange : Color.Black;
        }
    }
    async Task ScanAsync()
    {
        nextScan = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes);
        if (settings.Roots.Count == 0) { cache.Clear(); status.Text = "Add a folder, then choose Save & scan."; return; }
        scanCancellation?.Dispose(); scanCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        status.Text = "Scanning in the background… You can close this window.";
        try
        {
            var previous = snapshot;
            snapshot = await Task.Run(() => Scanner.Scan(settings.Roots, scanCancellation.Token));
            await Task.Run(() => { cache.Publish(snapshot, settings); Storage.Write("snapshot.json", snapshot); });
            table.Rows.Clear();
            foreach (var row in snapshot.Folders.OrderByDescending(r => r.Bytes).Take(500))
            {
                int kind = SharedCache.Classify(row.Bytes, settings);
                int index = table.Rows.Add(row.Path, (row.Bytes / 1000000m).ToString("N2"), !row.Complete ? "Incomplete — no marker" : kind == 2 ? "● Red" : kind == 1 ? "● Orange" : "Normal");
                table.Rows[index].Cells[2].Style.ForeColor = !row.Complete ? Color.DimGray : kind == 2 ? Color.Firebrick : kind == 1 ? Color.DarkOrange : Color.Black;
            }
            status.Text = $"Updated {DateTime.Now:t} • {snapshot.Folders.Count:N0} folders • {snapshot.Folders.Count(r => !r.Complete):N0} incomplete. Showing largest 500. Next scan in {settings.IntervalMinutes} min.";
            await Task.Run(() => RefreshChanged(previous, snapshot));
        }
        catch (OperationCanceledException) { status.Text = "Scan stopped or exceeded 3 minutes. Try a smaller folder. Previous results expire after 10 minutes."; }
        catch (Exception e) { status.Text = "Scan failed: " + e.Message; }
        finally { nextScan = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes); }
    }
    void RefreshRoots()
    {
        // Async Shell notifications from the app, never from the handler.
        foreach (var root in settings.Roots) SHChangeNotify(0x1000, 0x2005, root, IntPtr.Zero);
    }
    void RefreshChanged(Snapshot? previous, Snapshot current)
    {
        int Kind(FolderRow row) => row.Complete ? SharedCache.Classify(row.Bytes, settings) : 0;
        var before = previous?.Folders.ToDictionary(r => r.Path, Kind, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in current.Folders)
        {
            int old = before.Remove(row.Path, out int value) ? value : 0;
            if (old != Kind(row)) SHChangeNotify(0x2000, 0x2005, row.Path, IntPtr.Zero);
        }
        foreach (var path in before.Where(p => p.Value != 0).Select(p => p.Key)) SHChangeNotify(0x2000, 0x2005, path, IntPtr.Zero);
        RefreshRoots();
    }
    async Task ExitApp()
    {
        exiting = true; timer.Stop(); await StopAutomatic(); scanCancellation?.Cancel(); if (scanTask != null) await scanTask;
        cache.Clear();
        if (snapshot != null) await Task.Run(() => RefreshChanged(snapshot, new Snapshot(DateTime.UtcNow, new())));
        RefreshRoots(); Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tray.Dispose(); autoScanner?.Stop(); scanCancellation?.Cancel(); if (autoScanner == null || autoScanner.Completion.IsCompleted) cache.Dispose(); scanCancellation?.Dispose(); }
        base.Dispose(disposing);
    }
}
