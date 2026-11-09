using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScreenRecorderLib;

internal static class MediaTool
{
    public static string Run(params string[] arguments)
    {
        string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "ScreenLiteMedia.exe");
        var info = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(Quote)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (Process process = Process.Start(info))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new IOException("Модуль звука завершился с ошибкой. " + error.Result.Trim());
            return output.Result;
        }
    }

    private static string Quote(string value)
    {
        // File paths contain no quotes. Preserve a trailing directory separator when quoting.
        int tail = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', tail) + "\"";
    }
}

internal sealed class AudioApp
{
    public int Pid;
    public string Name;
    public string Label;
    public DateTime Started;
    public int[] Members;

    public bool IsRunning()
    {
        try
        {
            using (Process process = Process.GetProcessById(Pid))
                return process.StartTime == Started;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    public override string ToString() { return Label; }
}

internal static class AudioAppCatalog
{
    private static readonly HashSet<string> Infrastructure = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ApplicationFrameHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "TextInputHost.exe",
        "ScreenLite.exe", "ScreenLiteMedia.exe", "steam.exe"
    };

    public static List<AudioApp> Find()
    {
        var snapshot = GetProcessTree();
        int self;
        int session;
        using (var current = Process.GetCurrentProcess()) { self = current.Id; session = current.SessionId; }
        var pids = new HashSet<int>();
        foreach (string line in MediaTool.Run("apps").Split('\n'))
        {
            int pid;
            if (int.TryParse(line, out pid)) pids.Add(pid);
        }
        foreach (var window in Recorder.GetWindows())
        {
            if (window.Pid.HasValue && !string.IsNullOrWhiteSpace(window.Title)) pids.Add(window.Pid.Value);
        }
        var candidates = new Dictionary<int, AudioApp>();
        foreach (int pid in pids)
        {
            Entry entry;
            if (pid == self || !snapshot.TryGetValue(pid, out entry) || Infrastructure.Contains(entry.Name)) continue;
            var ancestry = new HashSet<int> { entry.ProcessId };
            while (ancestry.Add(entry.ParentId))
            {
                Entry parent;
                if (!snapshot.TryGetValue(entry.ParentId, out parent) || parent.ProcessId == entry.ProcessId ||
                    !string.Equals(parent.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) break;
                if (parent.ParentId == entry.ProcessId) break;
                entry = parent;
            }
            int root = entry.ProcessId;
            if (candidates.ContainsKey(root)) continue;
            try
            {
                using (Process process = Process.GetProcessById(root))
                {
                    if (process.SessionId != session) continue;
                    candidates.Add(root, new AudioApp
                    {
                        Pid = root, Name = entry.Name, Label = entry.Name, Started = process.StartTime
                    });
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        var groups = new Dictionary<int, HashSet<string>>();
        foreach (AudioApp app in candidates.Values)
        {
            AudioApp root = app;
            int parentId = snapshot[app.Pid].ParentId;
            var visited = new HashSet<int> { app.Pid };
            Entry parent;
            while (visited.Add(parentId) && snapshot.TryGetValue(parentId, out parent))
            {
                AudioApp candidate;
                if (candidates.TryGetValue(parentId, out candidate) && candidate.Started <= root.Started)
                    root = candidate;
                parentId = parent.ParentId;
            }
            HashSet<string> names;
            if (!groups.TryGetValue(root.Pid, out names))
                groups.Add(root.Pid, names = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            names.Add(app.Name);
        }
        var apps = groups.Select(group =>
        {
            AudioApp app = candidates[group.Key];
            app.Members = snapshot.Values.Where(entry => IsDescendant(entry.ProcessId, app.Pid, snapshot))
                .Select(entry => entry.ProcessId).ToArray();
            app.Label = string.Join(" + ", group.Value.OrderBy(name => name).Select(name =>
                string.Equals(name, "steamwebhelper.exe", StringComparison.OrdinalIgnoreCase) ? "Steam" : name));
            return app;
        }).OrderBy(app => app.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var duplicate in apps.GroupBy(app => app.Label).Where(group => group.Count() > 1))
            foreach (var app in duplicate) app.Label += "  [PID " + app.Pid + "]";
        return apps;
    }

    private static bool IsDescendant(int pid, int root, Dictionary<int, Entry> snapshot)
    {
        Entry parent;
        for (int depth = 0; depth < 128 && pid != 0 && snapshot.TryGetValue(pid, out parent); depth++)
        {
            if (pid == root) return true;
            pid = parent.ParentId;
        }
        return false;
    }

    public static Dictionary<int, double> ReadPeaks()
    {
        var peaks = new Dictionary<int, double>();
        foreach (string row in MediaTool.Run("levels").Split('\n'))
        {
            string[] columns = row.Trim().Split(' ');
            int pid;
            double peak;
            if (columns.Length == 2 && int.TryParse(columns[0], out pid) &&
                double.TryParse(columns[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out peak)) peaks[pid] = peak;
        }
        return peaks;
    }

    private static Dictionary<int, Entry> GetProcessTree()
    {
        var result = new Dictionary<int, Entry>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var entry = new Entry { Size = Marshal.SizeOf(typeof(Entry)) };
            if (!Process32First(snapshot, ref entry)) throw new Win32Exception();
            do { result[entry.ProcessId] = entry; } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public int Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public int ModuleId, Threads, ParentId, Priority, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class TrackVolumeControl : UserControl
{
    private readonly CheckedListBox list = new CheckedListBox();
    private readonly TrackBar volume = new TrackBar();
    private readonly Label value = new Label();
    private readonly ProgressBar original = new ProgressBar();
    private readonly ProgressBar adjusted = new ProgressBar();
    private AudioTrack[] tracks = new AudioTrack[0];
    private bool loading;
    public int Selected { get { return list.SelectedIndex; } }

    public TrackVolumeControl(int width, int listHeight)
    {
        Size = new System.Drawing.Size(width, listHeight + 114);
        list.CheckOnClick = true;
        list.IntegralHeight = false;
        list.HorizontalScrollbar = true;
        list.SetBounds(0, 0, width, listHeight);
        list.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
        {
            if (loading) return;
            tracks[e.Index].Enabled = e.NewValue == CheckState.Checked;
            if (e.Index == Selected) UpdateMeters();
        };
        list.SelectedIndexChanged += delegate { if (!loading) UpdateSelection(); };
        Controls.Add(list);

        volume.Minimum = 0;
        volume.Maximum = 200;
        volume.TickFrequency = 25;
        volume.SmallChange = 5;
        volume.LargeChange = 25;
        volume.AutoSize = false;
        volume.SetBounds(0, listHeight + 6, width - 67, 37);
        volume.ValueChanged += delegate
        {
            if (!loading && Selected >= 0) tracks[Selected].Volume = volume.Value;
            value.Text = volume.Value + "%";
            UpdateMeters();
        };
        Controls.Add(volume);
        value.SetBounds(width - 60, listHeight + 12, 58, 23);
        Controls.Add(value);
        AddMeter("Сигнал", original, width, listHeight + 50);
        AddMeter("С уровнем", adjusted, width, listHeight + 80);
    }

    private void AddMeter(string caption, ProgressBar meter, int width, int y)
    {
        var label = new Label { Text = caption };
        label.SetBounds(0, y, 90, 24);
        Controls.Add(label);
        meter.SetBounds(92, y + 3, width - 92, 14);
        Controls.Add(meter);
    }

    public void SetTracks(AudioTrack[] items)
    {
        loading = true;
        tracks = items;
        list.Items.Clear();
        foreach (AudioTrack track in tracks) list.Items.Add(track.Name, track.Enabled);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        loading = false;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        loading = true;
        volume.Enabled = Selected >= 0;
        volume.Value = Selected < 0 ? 100 : tracks[Selected].Volume;
        value.Text = volume.Value + "%";
        loading = false;
        UpdateMeters();
    }

    public void SetAll(bool enabled)
    {
        for (int i = 0; i < tracks.Length; i++) list.SetItemChecked(i, enabled);
    }

    public void UpdateMeters()
    {
        AudioTrack track = Selected >= 0 ? tracks[Selected] : null;
        double peak = track == null ? 0 : track.Peak;
        original.Value = ToMeter(peak);
        adjusted.Value = ToMeter(track == null ? 0 : peak * track.EffectiveVolume / 100);
    }

    private static int ToMeter(double value)
    {
        // Log scale makes quiet voices and gain changes visible.
        return value <= 0.001 ? 0 : (int)Math.Max(0, Math.Min(100, 100 + 100 * Math.Log10(value) / 3));
    }
}

internal sealed class AudioPicker : Form
{
    private readonly TrackVolumeControl tracks = new TrackVolumeControl(498, 171);
    private readonly CheckBox enhance = new CheckBox();
    private readonly AudioTrack[] items;
    private readonly AudioProject previewProject;
    private readonly AudioApp[] apps;
    private readonly Timer meterTimer = new Timer { Interval = 350 };
    private bool polling;
    private System.Media.SoundPlayer player;
    public bool Enhance { get { return enhance.Checked; } }
    public int[] SelectedTracks { get { return Enumerable.Range(0, items.Length).Where(i => items[i].EffectiveVolume > 0).ToArray(); } }
    public int[] Volumes { get { return items.Select(track => track.Volume).ToArray(); } }

    public AudioPicker(string title, string hint, AudioTrack[] items, AudioApp[] apps, AudioProject preview, bool improve)
    {
        this.items = items;
        this.apps = apps;
        previewProject = preview;
        bool chooseApps = apps != null;
        Text = title;
        Font = new System.Drawing.Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleDimensions = new System.Drawing.SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new System.Drawing.Size(530, 451);

        var instructions = new Label { Text = hint };
        instructions.SetBounds(16, 13, 498, 59);
        Controls.Add(instructions);
        tracks.Location = new System.Drawing.Point(16, 74);
        tracks.SetTracks(items);
        Controls.Add(tracks);

        AddButton("Выбрать все", 16, 360, 121, DialogResult.None, delegate { tracks.SetAll(true); });
        AddButton("Снять все", 144, 360, 102, DialogResult.None, delegate { tracks.SetAll(false); });
        if (chooseApps)
        {
            AddButton("Авто", 16, 405, 74, DialogResult.Retry, null);
            AddButton("Общий микс", 96, 405, 118, DialogResult.Ignore, null);
            meterTimer.Tick += RefreshPeaks;
            Shown += delegate { meterTimer.Start(); };
        }
        else
        {
            enhance.Text = "Улучшить звук";
            enhance.Checked = improve;
            enhance.SetBounds(16, 407, 193, 26);
            Controls.Add(enhance);
            AddButton("Прослушать фрагмент", 256, 360, 180, DialogResult.None, PlayPreview);
        }
        CancelButton = AddButton("Отмена", 271, 405, 92, DialogResult.Cancel, null);
        AcceptButton = AddButton(chooseApps ? "Ручной режим" : "Экспорт MP4", 370, 405, 144, DialogResult.OK, null);
        FormClosed += delegate
        {
            meterTimer.Dispose();
            StopPreview();
        };
    }

    private Button AddButton(string text, int x, int y, int width, DialogResult result, Action click)
    {
        var button = new Button { Text = text, DialogResult = result };
        button.SetBounds(x, y, width, 30);
        if (click != null) button.Click += delegate { click(); };
        Controls.Add(button);
        return button;
    }

    private async void RefreshPeaks(object sender, EventArgs e)
    {
        if (polling || tracks.Selected < 0) return;
        polling = true;
        try
        {
            var peaks = await System.Threading.Tasks.Task.Run(() => AudioAppCatalog.ReadPeaks());
            int i = tracks.Selected;
            if (IsDisposed || i < 0) return;
            double peak = 0;
            foreach (int pid in apps[i].Members)
            {
                double value;
                if (peaks.TryGetValue(pid, out value)) peak = Math.Max(peak, value);
            }
            items[i].Peak = peak;
            tracks.UpdateMeters();
        }
        catch (IOException) { }
        catch (Win32Exception) { }
        finally { polling = false; }
    }

    private void StopPreview()
    {
        if (player == null) return;
        player.Stop();
        if (player.Stream != null) player.Stream.Dispose();
        player.Dispose();
        player = null;
    }

    private void PlayPreview()
    {
        int i = tracks.Selected;
        if (i < 0) return;
        try
        {
            StopPreview();
            double peak;
            var wave = previewProject.Preview(i, items[i].EffectiveVolume, out peak);
            items[i].Peak = peak;
            tracks.UpdateMeters();
            if (wave == null) return;
            player = new System.Media.SoundPlayer(wave);
            player.Play();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
