using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using ScreenRecorderLib;

internal sealed class RecorderPreferences
{
    private const string RegistryPath = @"Software\ScreenLite";
    public static readonly Keys DefaultRecord = Keys.Control | Keys.Alt | Keys.F9;
    public static readonly Keys DefaultPause = Keys.Control | Keys.Alt | Keys.F10;

    public Keys RecordKey = DefaultRecord;
    public Keys PauseKey = DefaultPause;
    public bool ShowPanel = true;
    public int PanelCorner = 1;
    public int PanelOpacity = 90;
    public int FrameRate = 60;

    public static readonly int[] FrameRates = { 30, 60, 90, 120, 144 };

    public static RecorderPreferences Load()
    {
        var defaults = new RecorderPreferences();
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath))
            {
                var raw = key == null ? null : key.GetValue("Preferences") as string;
                if (raw == null) return defaults;
                var values = raw.Split(',');
                if (values.Length != 6) return defaults;
                var numbers = values.Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                Keys record = (Keys)numbers[0], pause = (Keys)numbers[1];
                if (!HotkeyInput.IsValid(record) || !HotkeyInput.IsValid(pause) || record == pause)
                    return defaults;
                defaults.RecordKey = record;
                defaults.PauseKey = pause;
                defaults.ShowPanel = numbers[2] != 0;
                defaults.PanelCorner = Math.Max(0, Math.Min(3, numbers[3]));
                defaults.PanelOpacity = Math.Max(50, Math.Min(100, numbers[4]));
                if (FrameRates.Contains(numbers[5])) defaults.FrameRate = numbers[5];
            }
        }
        catch (System.IO.IOException) { return new RecorderPreferences(); }
        catch (UnauthorizedAccessException) { return new RecorderPreferences(); }
        catch (System.Security.SecurityException) { return new RecorderPreferences(); }
        catch (FormatException) { return new RecorderPreferences(); }
        catch (OverflowException) { return new RecorderPreferences(); }
        return defaults;
    }

    public void Save()
    {
        int[] values = { (int)RecordKey, (int)PauseKey, ShowPanel ? 1 : 0, PanelCorner, PanelOpacity, FrameRate };
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            key.SetValue("Preferences", string.Join(",", values.Select(value => value.ToString(CultureInfo.InvariantCulture))));
    }
}

internal sealed class HotkeyInput : TextBox
{
    private Keys combination;
    public Keys Combination
    {
        get { return combination; }
        set { combination = value; Text = Describe(value); }
    }

    public HotkeyInput(Keys value)
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Combination = value;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (IsValid(keyData))
        {
            Combination = keyData;
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    public static bool IsValid(Keys combination)
    {
        Keys key = combination & Keys.KeyCode;
        bool function = key >= Keys.F1 && key <= Keys.F24 && key != Keys.F12;
        bool letterOrDigit = (key >= Keys.A && key <= Keys.Z) ||
            (key >= Keys.D0 && key <= Keys.D9) || (key >= Keys.NumPad0 && key <= Keys.NumPad9);
        return function || (letterOrDigit && (combination & (Keys.Control | Keys.Alt)) != Keys.None);
    }

    public static string Describe(Keys combination)
    {
        Keys key = combination & Keys.KeyCode;
        string name = key >= Keys.D0 && key <= Keys.D9
            ? ((int)(key - Keys.D0)).ToString(CultureInfo.InvariantCulture) : key.ToString();
        return ((combination & Keys.Control) != 0 ? "Ctrl+" : "") +
            ((combination & Keys.Alt) != 0 ? "Alt+" : "") +
            ((combination & Keys.Shift) != 0 ? "Shift+" : "") + name;
    }

    public static uint Modifiers(Keys combination)
    {
        const uint NoRepeat = 0x4000;
        return NoRepeat | ((combination & Keys.Alt) != 0 ? 1u : 0u) |
            ((combination & Keys.Control) != 0 ? 2u : 0u) |
            ((combination & Keys.Shift) != 0 ? 4u : 0u);
    }
}

internal sealed class RecorderSettingsDialog : Form
{
    private readonly HotkeyInput record;
    private readonly HotkeyInput pause;
    private readonly CheckBox panel = new CheckBox { Text = "Показывать мини-панель" };
    private readonly ComboBox corner = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown opacity = new NumericUpDown { Minimum = 50, Maximum = 100, Increment = 5 };
    public RecorderPreferences Settings
    {
        get
        {
            return new RecorderPreferences
            {
                RecordKey = record.Combination, PauseKey = pause.Combination, ShowPanel = panel.Checked,
                PanelCorner = corner.SelectedIndex, PanelOpacity = (int)opacity.Value
            };
        }
    }

    public RecorderSettingsDialog(RecorderPreferences settings)
    {
        Text = "Горячие клавиши и мини-панель";
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(484, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        record = new HotkeyInput(settings.RecordKey);
        pause = new HotkeyInput(settings.PauseKey);
        Add(new Label { Text = "Выбери поле и нажми сочетание. Поддерживаются F1–F24 (кроме F12) и Ctrl/Alt с буквой или цифрой." }, 18, 14, 448, 55);
        Add(new Label { Text = "Старт / стоп" }, 18, 80, 190, 24);
        Add(record, 215, 77, 250, 27);
        Add(new Label { Text = "Пауза / продолжить" }, 18, 116, 190, 24);
        Add(pause, 215, 113, 250, 27);
        panel.Checked = settings.ShowPanel;
        Add(panel, 18, 150, 447, 28);
        Add(new Label { Text = "Угол экрана" }, 18, 188, 190, 24);
        corner.Items.AddRange(new object[] { "Сверху слева", "Сверху справа", "Снизу слева", "Снизу справа" });
        corner.SelectedIndex = settings.PanelCorner;
        Add(corner, 215, 185, 250, 27);
        Add(new Label { Text = "Непрозрачность, %" }, 18, 225, 190, 24);
        opacity.Value = settings.PanelOpacity;
        Add(opacity, 215, 222, 90, 27);

        var defaults = new Button { Text = "По умолчанию" };
        defaults.Click += delegate
        {
            record.Combination = RecorderPreferences.DefaultRecord;
            pause.Combination = RecorderPreferences.DefaultPause;
            panel.Checked = true; corner.SelectedIndex = 1; opacity.Value = 90;
        };
        Add(defaults, 18, 274, 137, 31);
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel };
        Add(cancel, 252, 274, 102, 31);
        CancelButton = cancel;
        var save = new Button { Text = "Сохранить", DialogResult = DialogResult.OK };
        Add(save, 360, 274, 105, 31);
        AcceptButton = save;
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK || record.Combination != pause.Combination) return;
            e.Cancel = true;
            MessageBox.Show(this, "Назначь разные сочетания для записи и паузы.", Text);
        };
    }

    private void Add(Control control, int x, int y, int width, int height)
    {
        control.SetBounds(x, y, width, height);
        Controls.Add(control);
    }
}

internal sealed class RecordingPanel : Form
{
    private readonly Label state = new Label();
    private readonly Label size = new Label();
    private readonly Label keys = new Label();
    private readonly TrackVolumeControl volumes = new TrackVolumeControl(308, 124);
    private readonly Button expand = new Button();
    private Screen screen;
    private int corner;
    private int collapsedHeight;

    public RecordingPanel(RecorderPreferences settings)
    {
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(336, 112);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = settings.PanelOpacity / 100.0;
        BackColor = Color.FromArgb(24, 30, 39);
        ForeColor = Color.WhiteSmoke;
        state.Font = new Font("Segoe UI Semibold", 13);
        state.SetBounds(13, 8, 311, 30);
        size.SetBounds(14, 41, 311, 24);
        keys.SetBounds(14, 69, 311, 41);
        keys.ForeColor = Color.FromArgb(176, 189, 206);
        keys.Text = "Стоп  " + HotkeyInput.Describe(settings.RecordKey) +
            "\nПауза / продолжить  " + HotkeyInput.Describe(settings.PauseKey);
        expand.Text = "Звук приложений ▾";
        expand.FlatStyle = FlatStyle.Flat;
        expand.ForeColor = keys.ForeColor;
        expand.SetBounds(14, 107, 308, 27);
        expand.Click += delegate { SetExpanded(!volumes.Visible); };
        volumes.Location = new Point(14, 145);
        volumes.Visible = false;
        Controls.AddRange(new Control[] { state, size, keys, expand, volumes });
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    public bool ShowOn(Screen screen, int corner, AudioTrack[] tracks)
    {
        // Force the handle into existence before showing any pixels.
        if (!Recorder.SetExcludeFromCapture(Handle, true)) return false;
        this.screen = screen;
        this.corner = corner;
        volumes.SetTracks(tracks);
        expand.Visible = tracks.Length > 0;
        collapsedHeight = tracks.Length > 0 ? expand.Bottom + 10 : keys.Bottom + 3;
        ClientSize = new Size(ClientSize.Width, collapsedHeight);
        Reposition();
        Show();
        Reposition();
        return true;
    }

    private void SetExpanded(bool expanded)
    {
        volumes.Visible = expanded;
        expand.Text = expanded ? "Звук приложений ▴" : "Звук приложений ▾";
        ClientSize = new Size(ClientSize.Width, expanded ? volumes.Bottom + 9 : collapsedHeight);
        Reposition();
    }

    private void Reposition()
    {
        Rectangle area = screen.WorkingArea;
        int x = (corner & 1) == 0 ? area.Left + 14 : area.Right - Width - 14;
        int y = (corner & 2) == 0 ? area.Top + 14 : area.Bottom - Height - 14;
        Location = new Point(x, y);
    }

    public void UpdateStatus(bool paused, TimeSpan elapsed, string estimatedSize)
    {
        state.ForeColor = paused ? Color.FromArgb(253, 193, 102) : Color.FromArgb(255, 137, 151);
        state.Text = (paused ? "Ⅱ Пауза   " : "● Запись   ") + elapsed.ToString(@"hh\:mm\:ss");
        size.Text = "Размер MP4  ≈  " + estimatedSize;
        if (volumes.Visible) volumes.UpdateMeters();
    }
}

internal static class DisplayRefresh
{
    public static int Hertz(string deviceName)
    {
        var mode = new DeviceMode { Size = 220 };
        return EnumDisplaySettings(deviceName, -1, ref mode) && mode.Frequency > 1 ? mode.Frequency : 0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DeviceMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public int Frequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int mode, ref DeviceMode deviceMode);
}
