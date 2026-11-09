using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenRecorderLib;

internal static class CaptureSettings
{
    public const int Fps = 60;
    public const int AudioBitsPerSecond = 160000;

    public static Size GetFrameSize(Size screen, int targetHeight)
    {
        double scale = targetHeight == 0 ? 1 : Math.Min(1.0, (double)targetHeight / screen.Height);
        // H.264 4:2:0 requires even dimensions.
        return new Size((int)(screen.Width * scale) / 2 * 2, (int)(screen.Height * scale) / 2 * 2);
    }

    public static int VideoBitrate(Size frame, bool compact, int fps = Fps)
    {
        double pixels = (double)frame.Width * frame.Height;
        return (int)Math.Round(Math.Max(2.0, pixels / (1920 * 1080) * (compact ? 8 : 13) * Math.Pow(fps / 60.0, 0.65)) * 1000000);
    }

    public static RecorderOptions Create(string display, Size size, bool compact, bool includeMic, int fps = Fps)
    {
        var options = RecorderOptions.Default;
        options.SourceOptions.RecordingSources.Add(new DisplayRecordingSource(display)
        {
            RecorderApi = RecorderApi.DesktopDuplication,
            IsCursorCaptureEnabled = true
        });
        options.OutputOptions.OutputFrameSize = new ScreenSize(size.Width, size.Height);
        options.OutputOptions.Stretch = StretchMode.Uniform;
        options.VideoEncoderOptions = new VideoEncoderOptions
        {
            Encoder = new H264VideoEncoder
            {
                EncoderProfile = H264Profile.High,
                BitrateMode = compact ? H264BitrateControlMode.UnconstrainedVBR : H264BitrateControlMode.CBR
            },
            Framerate = fps,
            IsFixedFramerate = true,
            IsHardwareEncodingEnabled = true,
            IsMp4FastStartEnabled = false,
            Bitrate = VideoBitrate(size, compact, fps)
        };
        options.AudioOptions.IsAudioEnabled = true;
        options.AudioOptions.Channels = AudioChannels.Stereo;
        options.AudioOptions.Bitrate = AudioBitrate.bitrate_160kbps;
        var playback = LoopbackAudioSource.Default;
        playback.Volume = includeMic ? 0.65f : 1.0f;
        options.AudioOptions.AudioSources.Add(playback);
        if (includeMic)
        {
            var mic = CaptureAudioSource.Default;
            mic.Volume = 0.35f;
            options.AudioOptions.AudioSources.Add(mic);
        }
        return options;
    }
}

internal enum CaptureAudioMode { Automatic, Manual, System }

internal sealed class RecorderForm : Form
{
    private const int RecordHotkey = 1;
    private const int PauseHotkey = 2;
    private const int WmHotkey = 0x0312;
    private static readonly Color PanelColor = Color.FromArgb(37, 44, 56);
    private static readonly Color MutedColor = Color.FromArgb(176, 189, 206);
    private static readonly Color AccentColor = Color.FromArgb(103, 225, 181);

    private readonly ComboBox displays = new ComboBox();
    private readonly ComboBox resolutions = new ComboBox();
    private readonly ComboBox profiles = new ComboBox();
    private readonly ComboBox frameRates = new ComboBox();
    private readonly Button settingsButton;
    private readonly Button pauseButton;
    private readonly CheckBox mic = new CheckBox();
    private readonly CheckBox enhance = new CheckBox();
    private readonly Button appsButton;
    private readonly Button editButton;
    private readonly TextBox folder = new TextBox();
    private readonly Label estimate = new Label();
    private readonly Label loadEstimate = new Label();
    private readonly Button lightButton;
    private readonly ToolTip hints = new ToolTip();
    private readonly Label status = new Label();
    private readonly Label hotkeyLabel = new Label();
    private readonly Button recordButton;
    private readonly Button browseButton;
    private readonly Timer timer = new Timer();
    private readonly Stopwatch elapsed = new Stopwatch();

    private Screen[] screens;
    private Recorder recorder;
    private string finalPath;
    private string pendingPath;
    private bool stopping;
    private bool closeAfterSave;
    private bool recordKeyRegistered;
    private bool pauseKeyRegistered;
    private bool paused;
    private bool active;
    private bool panelAttempted;
    private int bitrate;
    private int screenHertz;
    private long observedBytes;
    private RecorderPreferences preferences = RecorderPreferences.Load();
    private RecordingPanel recordingPanel;
    private bool dialogOpen;
    private List<AudioApp> selectedApps;
    private CaptureAudioMode audioMode = CaptureAudioMode.Automatic;
    private readonly Dictionary<string, AudioTrack> audioRules = new Dictionary<string, AudioTrack>(StringComparer.OrdinalIgnoreCase);
    private AudioProject audioProject;
    private string latestVideo;

    public RecorderForm()
    {
        Text = "ScreenLite";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(510, 657);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 30, 39);
        ForeColor = Color.WhiteSmoke;

        Label title = AddLabel("ScreenLite", 22, 16, 465, 35, ForeColor);
        title.Font = new Font("Segoe UI Semibold", 20);
        AddLabel("Запись экрана и звука  •  до 144 FPS  •  MP4", 24, 54, 462, 24, MutedColor);

        AddLabel("Экран", 24, 97, 132, 24, MutedColor);
        AddCombo(displays, 159, 94, 327);
        AddLabel("Разрешение", 24, 136, 132, 24, MutedColor);
        AddCombo(resolutions, 159, 133, 327);
        AddLabel("Размер файла", 24, 175, 132, 24, MutedColor);
        AddCombo(profiles, 159, 172, 327);
        resolutions.Items.AddRange(new object[] { "720p", "1080p", "Оригинальное" });
        profiles.Items.AddRange(new object[] { "Компактный", "Выше качество" });
        resolutions.SelectedIndex = 0;
        profiles.SelectedIndex = 0;

        AddLabel("Частота кадров", 24, 214, 132, 24, MutedColor);
        AddCombo(frameRates, 159, 211, 327);
        foreach (int rate in RecorderPreferences.FrameRates) frameRates.Items.Add(rate + " FPS");
        frameRates.SelectedIndex = Array.IndexOf(RecorderPreferences.FrameRates, preferences.FrameRate);
        estimate.SetBounds(24, 251, 462, 32);
        estimate.Font = new Font("Segoe UI Semibold", 15);
        estimate.ForeColor = ForeColor;
        Controls.Add(estimate);
        loadEstimate.SetBounds(24, 288, 298, 30);
        loadEstimate.Font = new Font("Segoe UI Semibold", 13);
        loadEstimate.ForeColor = AccentColor;
        Controls.Add(loadEstimate);
        hints.SetToolTip(loadEstimate, "Оценка по разрешению, FPS и способу записи звука. Реальная нагрузка зависит от GPU, игры и числа источников.");
        hints.SetToolTip(estimate, "Ориентир по битрейту. Фактический размер зависит от движения в кадре.");
        lightButton = AddButton("Лёгкий режим", 331, 286, 155, 31, false, ApplyLightPreset);
        hints.SetToolTip(lightButton, "720p · 30 FPS · компактный MP4 · общий звук без обработки и раздельных дорожек.");

        mic.Text = "Записывать микрофон";
        mic.Checked = true;
        mic.SetBounds(24, 340, 210, 30);
        Controls.Add(mic);
        enhance.Text = "Автоматически улучшать звук";
        enhance.Checked = true;
        enhance.SetBounds(234, 340, 252, 30);
        Controls.Add(enhance);
        appsButton = AddButton("Звук · Авто (раздельные дорожки)", 24, 377, 462, 36, false, ChooseAudioApps);

        AddLabel("Куда сохранять", 24, 430, 132, 24, MutedColor);
        folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenLite");
        folder.SetBounds(159, 428, 285, 28);
        folder.ReadOnly = true;
        folder.BackColor = PanelColor;
        folder.ForeColor = ForeColor;
        folder.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(folder);
        browseButton = AddButton("…", 451, 426, 35, 31, false, BrowseFolder);
        AddButton("Открыть папку", 24, 472, 132, 37, false, OpenFolder);
        pauseButton = AddButton("Пауза", 164, 472, 114, 37, false, TogglePause);
        pauseButton.Enabled = false;
        recordButton = AddButton("Начать запись", 286, 472, 200, 37, true, ToggleRecording);
        editButton = AddButton("Изменить звук записи…", 24, 519, 248, 36, false, EditSavedAudio);
        settingsButton = AddButton("Клавиши и мини-панель", 280, 519, 206, 36, false, EditSettings);
        status.SetBounds(24, 576, 462, 25);
        status.AutoEllipsis = true;
        status.Text = "Готово к записи";
        Controls.Add(status);
        hotkeyLabel.SetBounds(24, 607, 462, 42);
        hotkeyLabel.ForeColor = MutedColor;
        Controls.Add(hotkeyLabel);

        Load += delegate { RefreshDisplays(); };
        displays.SelectedIndexChanged += delegate
        {
            screenHertz = displays.SelectedIndex < 0 ? 0 : DisplayRefresh.Hertz(screens[displays.SelectedIndex].DeviceName);
            UpdateEstimate();
        };
        resolutions.SelectedIndexChanged += delegate { UpdateEstimate(); };
        profiles.SelectedIndexChanged += delegate { UpdateEstimate(); };
        frameRates.SelectedIndexChanged += delegate { UpdateEstimate(); };
        enhance.CheckedChanged += delegate { UpdateEstimate(); };
        timer.Interval = 500;
        timer.Tick += delegate { if (active && !stopping) ShowElapsed(); };
        FormClosing += OnFormClosing;
        FormClosed += delegate { timer.Dispose(); hints.Dispose(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterShortcuts(preferences);
        RefreshShortcutLabel();
    }

    private void RegisterShortcuts(RecorderPreferences settings)
    {
        recordKeyRegistered = RegisterHotKey(Handle, RecordHotkey, HotkeyInput.Modifiers(settings.RecordKey),
            (uint)(settings.RecordKey & Keys.KeyCode));
        pauseKeyRegistered = RegisterHotKey(Handle, PauseHotkey, HotkeyInput.Modifiers(settings.PauseKey),
            (uint)(settings.PauseKey & Keys.KeyCode));
    }

    private void UnregisterShortcuts()
    {
        if (recordKeyRegistered) UnregisterHotKey(Handle, RecordHotkey);
        if (pauseKeyRegistered) UnregisterHotKey(Handle, PauseHotkey);
        recordKeyRegistered = pauseKeyRegistered = false;
    }

    private void RefreshShortcutLabel()
    {
        hotkeyLabel.Text = "Старт / стоп  " + HotkeyInput.Describe(preferences.RecordKey) +
            (recordKeyRegistered ? "" : " (занято)") + "\nПауза / продолжить  " +
            HotkeyInput.Describe(preferences.PauseKey) + (pauseKeyRegistered ? "" : " (занято)");
    }

    private void EditSettings()
    {
        dialogOpen = true;
        UnregisterShortcuts();
        try
        {
            using (var dialog = new RecorderSettingsDialog(preferences))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var updated = dialog.Settings;
                updated.FrameRate = SelectedFrameRate;
                RegisterShortcuts(updated);
                if (!recordKeyRegistered || !pauseKeyRegistered)
                {
                    MessageBox.Show(this, "Windows не смогла назначить одну из клавиш. Выбери другое сочетание.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                updated.Save();
                preferences = updated;
            }
        }
        catch (Exception error) { ShowError(error.Message); }
        finally
        {
            UnregisterShortcuts();
            RegisterShortcuts(preferences);
            RefreshShortcutLabel();
            dialogOpen = false;
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterShortcuts();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && !dialogOpen)
        {
            if (message.WParam.ToInt32() == RecordHotkey) ToggleRecording();
            if (message.WParam.ToInt32() == PauseHotkey) TogglePause();
        }
        base.WndProc(ref message);
    }
    private Label AddLabel(string text, int x, int y, int width, int height, Color color)
    {
        var label = new Label { Text = text, ForeColor = color };
        label.SetBounds(x, y, width, height);
        Controls.Add(label);
        return label;
    }

    private void AddCombo(ComboBox combo, int x, int y, int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.BackColor = PanelColor;
        combo.ForeColor = ForeColor;
        combo.FlatStyle = FlatStyle.Flat;
        combo.SetBounds(x, y, width, 28);
        Controls.Add(combo);
    }

    private Button AddButton(string text, int x, int y, int width, int height, bool primary, Action action)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? AccentColor : PanelColor,
            ForeColor = primary ? BackColor : ForeColor
        };
        button.FlatAppearance.BorderSize = 0;
        button.SetBounds(x, y, width, height);
        button.Click += delegate { action(); };
        Controls.Add(button);
        return button;
    }

    private void RefreshDisplays()
    {
        string selected = displays.SelectedIndex >= 0 ? screens[displays.SelectedIndex].DeviceName : null;
        screens = Screen.AllScreens;
        displays.Items.Clear();
        foreach (Screen screen in screens)
        {
            displays.Items.Add(string.Format("{0}  ·  {1} × {2}",
                screen.Primary ? "Основной экран" : screen.DeviceName, screen.Bounds.Width, screen.Bounds.Height));
        }
        int index = Array.FindIndex(screens, screen => screen.DeviceName == selected);
        if (index < 0) index = Array.FindIndex(screens, screen => screen.Primary);
        if (index >= 0) displays.SelectedIndex = index;
    }

    private Size SelectedFrameSize()
    {
        int height = resolutions.SelectedIndex == 0 ? 720 : resolutions.SelectedIndex == 1 ? 1080 : 0;
        return CaptureSettings.GetFrameSize(screens[displays.SelectedIndex].Bounds.Size, height);
    }

    private int SelectedFrameRate { get { return frameRates.SelectedIndex < 0 ? 60 :
        RecorderPreferences.FrameRates[frameRates.SelectedIndex]; } }

    private void UpdateEstimate()
    {
        if (displays.SelectedIndex < 0) return;
        Size size = SelectedFrameSize();
        int fps = SelectedFrameRate;
        int bits = CaptureSettings.VideoBitrate(size, profiles.SelectedIndex == 0, fps) + CaptureSettings.AudioBitsPerSecond;
        estimate.Text = string.Format("{0} × {1}  ·  {2} FPS  ·  ≈ {3:0} МБ/мин",
            size.Width, size.Height, fps, bits * 60.0 / 8000000);
        double work = (double)size.Width * size.Height * fps / (1920 * 1080 * 60);
        if (audioMode != CaptureAudioMode.System || enhance.Checked) work += 0.15;
        string level = work <= 0.55 ? "низкая" : work <= 1.15 ? "средняя" : "высокая";
        loadEstimate.Text = "Нагрузка: " + level;
        loadEstimate.ForeColor = work <= 0.55 ? AccentColor : work <= 1.15
            ? Color.FromArgb(253, 193, 102) : Color.FromArgb(255, 137, 151);
        if (screenHertz != 0 && fps > screenHertz)
            loadEstimate.Text += "  ·  экран " + screenHertz + " Гц";
    }

    private void ApplyLightPreset()
    {
        resolutions.SelectedIndex = 0;
        profiles.SelectedIndex = 0;
        frameRates.SelectedIndex = 0;
        audioMode = CaptureAudioMode.System;
        enhance.Checked = false;
        appsButton.Text = "Звук · Общий микс";
        UpdateEstimate();
    }
    private void BrowseFolder()
    {
        using (var picker = new FolderBrowserDialog { SelectedPath = folder.Text, Description = "Папка для записей ScreenLite" })
            if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath;
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(folder.Text);
            Process.Start("explorer.exe", "\"" + folder.Text + "\"");
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void ToggleRecording()
    {
        if (stopping || dialogOpen) return;
        if (recorder != null) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        try
        {
            RefreshDisplays();
            if (displays.SelectedIndex < 0) throw new InvalidOperationException("Экран не найден.");
            if (audioMode == CaptureAudioMode.System && Recorder.GetSystemAudioLoopbackDevices().Count == 0)
                throw new InvalidOperationException("Windows не видит активных устройств воспроизведения.");
            if (audioMode == CaptureAudioMode.Manual && selectedApps.Any(app => !app.IsRunning()))
                throw new InvalidOperationException("Одно из приложений закрылось или перезапустилось. Обнови список через кнопку «Звук».");
            if (mic.Checked && Recorder.GetSystemAudioCaptureDevices().Count == 0)
                throw new InvalidOperationException("Микрофон недоступен. Снимите галочку, чтобы записать системный звук.");

            Directory.CreateDirectory(folder.Text);
            finalPath = Path.Combine(folder.Text, "ScreenLite " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff") + ".mp4");
            pendingPath = Path.ChangeExtension(finalPath, ".recording.mp4");
            if (File.Exists(finalPath) || File.Exists(pendingPath) || Directory.Exists(AudioProject.DirectoryFor(finalPath)))
            {
                finalPath = Path.ChangeExtension(finalPath, null) + " " + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4";
                pendingPath = Path.ChangeExtension(finalPath, ".recording.mp4");
            }
            var options = CaptureSettings.Create(screens[displays.SelectedIndex].DeviceName, SelectedFrameSize(),
                profiles.SelectedIndex == 0, mic.Checked, SelectedFrameRate);
            bitrate = options.VideoEncoderOptions.Bitrate + CaptureSettings.AudioBitsPerSecond;
            observedBytes = 0;
            panelAttempted = false;

            ConfigureAudio(options.AudioOptions);
            recorder = Recorder.CreateRecorder(options);
            if (audioProject != null) recorder.OnAudioPacketRecorded += audioProject.WritePacket;
            recorder.OnStatusChanged += OnStatusChanged;
            recorder.OnRecordingComplete += OnRecordingComplete;
            recorder.OnRecordingFailed += OnRecordingFailed;
            SetRecordingControls(true);
            status.Text = "Подключение экрана и звука…";
            recorder.Record(pendingPath);
        }
        catch (Exception error)
        {
            ReleaseRecorder();
            if (audioProject != null)
            {
                audioProject.Dispose();
                audioProject.DeleteTemporaryTracks();
                audioProject = null;
            }
            ShowError(error.Message);
        }
    }

    private void OnStatusChanged(object sender, RecordingStatusEventArgs e)
    {
        if (e.Status != RecorderStatus.Recording && e.Status != RecorderStatus.Paused) return;
        BeginInvoke((Action)delegate
        {
            if (!ReferenceEquals(sender, recorder) || stopping || recorder.Status != e.Status) return;
            if (e.Status == RecorderStatus.Paused)
            {
                paused = true;
                elapsed.Stop();
                pauseButton.Text = "Продолжить";
            }
            else
            {
                active = true;
                paused = false;
                elapsed.Start();
                pauseButton.Text = "Пауза";
                if (!panelAttempted)
                {
                    panelAttempted = true;
                    ShowRecordingPanel();
                    if (recordKeyRegistered) WindowState = FormWindowState.Minimized;
                }
                timer.Start();
            }
            pauseButton.Enabled = active;
            ShowElapsed();
        });
    }

    private void ShowRecordingPanel()
    {
        if (!preferences.ShowPanel) return;
        var panel = new RecordingPanel(preferences);
        AudioTrack[] tracks = audioProject == null ? new AudioTrack[0] : audioProject.Tracks;
        if (panel.ShowOn(screens[displays.SelectedIndex], preferences.PanelCorner, tracks))
            recordingPanel = panel;
        else panel.Dispose();
    }

    private void TogglePause()
    {
        if (!active || stopping || dialogOpen || recorder == null) return;
        bool resume = recorder.Status == RecorderStatus.Paused;
        if (!resume && recorder.Status != RecorderStatus.Recording) return;
        try
        {
            if (audioProject != null) audioProject.Paused = !resume;
            if (resume) recorder.Resume();
            else recorder.Pause();
        }
        catch (Exception error)
        {
            if (audioProject != null) audioProject.Paused = resume;
            ShowError(error.Message);
        }
    }

    private void ShowElapsed()
    {
        status.Text = (paused ? "Ⅱ Пауза   " : "● Запись   ") + elapsed.Elapsed.ToString(@"hh\:mm\:ss");
        if (recordingPanel == null) return;
        if (!paused || observedBytes == 0)
        {
            try { observedBytes = new FileInfo(pendingPath).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        double megabytes = observedBytes > 0 ? (observedBytes * 1.02 + 16384) / 1000000
            : bitrate * elapsed.Elapsed.TotalSeconds / 8000000;
        string estimate = megabytes < 1000 ? megabytes.ToString("0.0") + " МБ" : (megabytes / 1000).ToString("0.00") + " ГБ";
        recordingPanel.UpdateStatus(paused, elapsed.Elapsed, estimate);
    }
    private void StopRecording()
    {
        stopping = true;
        timer.Stop();
        pauseButton.Enabled = false;
        recordButton.Enabled = false;
        if (recordingPanel != null) recordingPanel.Hide();
        status.Text = "Сохранение MP4…";
        try { recorder.Stop(); }
        catch (Exception error) { CompleteRecording(error.Message); }
    }

    private void OnRecordingComplete(object sender, RecordingCompleteEventArgs e)
    {
        BeginInvoke((Action)delegate { if (ReferenceEquals(sender, recorder)) CompleteRecording(null); });
    }

    private void OnRecordingFailed(object sender, RecordingFailedEventArgs e)
    {
        BeginInvoke((Action)delegate { if (ReferenceEquals(sender, recorder)) CompleteRecording(e.Error); });
    }

    private void ConfigureAudio(AudioOptions options)
    {
        bool separate = audioMode != CaptureAudioMode.System;
        if (!separate && !enhance.Checked) return;
        var tracks = new List<AudioTrack>();
        if (separate)
        {
            var apps = audioMode == CaptureAudioMode.Automatic ? AudioAppCatalog.Find() : selectedApps;
            options.AudioSources.Clear();
            foreach (AudioApp app in apps)
            {
                AudioTrack rule;
                audioRules.TryGetValue(app.Label, out rule);
                var source = new ProcessAudioSource(app.Pid);
                options.AudioSources.Add(source);
                tracks.Add(new AudioTrack
                {
                    Name = app.Label, SourceId = source.ID,
                    Enabled = rule == null || rule.Enabled, Volume = rule == null ? 100 : rule.Volume
                });
            }
            if (apps.Count == 0 && audioMode == CaptureAudioMode.Automatic &&
                Recorder.GetSystemAudioLoopbackDevices().Count > 0)
            {
                var source = LoopbackAudioSource.Default;
                options.AudioSources.Add(source);
                tracks.Add(new AudioTrack { Name = "Системный звук", SourceId = source.ID });
            }
            if (mic.Checked)
            {
                var microphone = CaptureAudioSource.Default;
                microphone.Volume = 0.7f;
                options.AudioSources.Add(microphone);
                tracks.Add(new AudioTrack { Name = "Микрофон", SourceId = microphone.ID, Microphone = true });
            }
            options.IsAudioEnabled = tracks.Count != 0;
        }
        else
        {
            foreach (AudioSourceBase source in options.AudioSources)
            {
                bool microphone = source is CaptureAudioSource;
                tracks.Add(new AudioTrack
                {
                    Name = microphone ? "Микрофон" : "Системный звук",
                    SourceId = source.ID, Microphone = microphone
                });
            }
        }
        options.IsAudioPacketPreviewEnabled = tracks.Count != 0;
        // Only the audited per-track PCM is used for the final mix.
        options.MasterVolume = 0f;
        audioProject = new AudioProject(finalPath, tracks.ToArray(), enhance.Checked);
        audioProject.Start();
    }
    private async void CompleteRecording(string error)
    {
        ReleaseRecorder();
        stopping = true;
        SetRecordingControls(true);
        recordButton.Enabled = false;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        AudioProject project = audioProject;
        audioProject = null;
        bool separate = audioMode != CaptureAudioMode.System;
        try
        {
            if (error != null) throw new IOException(error);
            if (project != null)
            {
                status.Text = "Сохранение звука и MP4…";
                await Task.Run(delegate
                {
                    project.Finish();
                    if (project.Tracks.Length != 0)
                    {
                        project.Export(pendingPath, finalPath, Enumerable.Range(0, project.Tracks.Length).ToArray(), project.Enhance);
                        try { File.Delete(pendingPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                    else File.Move(pendingPath, finalPath);
                    if (!separate) project.DeleteTemporaryTracks();
                });
            }
            else File.Move(pendingPath, finalPath);
            latestVideo = finalPath;
            status.Text = "Сохранено  ·  " + Path.GetFileName(finalPath);
        }
        catch (Exception failure)
        {
            if (project != null) project.Dispose();
            string message = failure.Message;
            if (File.Exists(pendingPath))
            {
                try
                {
                    File.Move(pendingPath, finalPath);
                    latestVideo = finalPath;
                    message += (project == null ? "\n\nДоступна исходная запись: " :
                        "\n\nДоступно видео без обработанного звука: ") + finalPath;
                }
                catch (IOException) { message += "\n\nФайл записи: " + pendingPath; }
                catch (UnauthorizedAccessException) { message += "\n\nФайл записи: " + pendingPath; }
            }
            ShowError(message);
        }
        finally { FinishBusyOperation(); }
    }

    private void ChooseAudioApps()
    {
        dialogOpen = true;
        try
        {
            AudioApp[] apps = AudioAppCatalog.Find().ToArray();
            var items = apps.Select(app =>
            {
                AudioTrack rule;
                audioRules.TryGetValue(app.Label, out rule);
                return new AudioTrack
                {
                    Name = app.Label, Enabled = rule == null || rule.Enabled, Volume = rule == null ? 100 : rule.Volume
                };
            }).ToArray();
            using (var picker = new AudioPicker("Звук приложений",
                "Выбери источник, чтобы настроить его громкость. «Авто» обновляет список при каждом старте. Галочки управляют записью.",
                items, apps, null, false))
            {
                DialogResult result = picker.ShowDialog(this);
                if (result == DialogResult.Cancel) return;
                foreach (AudioTrack item in items) audioRules[item.Name] = item;
                if (result == DialogResult.Ignore) audioMode = CaptureAudioMode.System;
                if (result == DialogResult.Retry) audioMode = CaptureAudioMode.Automatic;
                if (result == DialogResult.OK)
                {
                    audioMode = CaptureAudioMode.Manual;
                    selectedApps = apps.ToList();
                }
            }
            appsButton.Text = audioMode == CaptureAudioMode.Automatic ? "Звук · Авто (раздельные дорожки)"
                : audioMode == CaptureAudioMode.System ? "Звук · Общий микс"
                : "Звук · Ручной выбор (" + selectedApps.Count(app => audioRules[app.Label].Enabled) + ")";
            UpdateEstimate();
        }
        catch (Exception failure) { ShowError(failure.Message); }
        finally { dialogOpen = false; }
    }
    private async void EditSavedAudio()
    {
        string video;
        AudioProject project;
        int[] selection;
        int[] levels;
        bool improve;
        dialogOpen = true;
        try
        {
            using (var file = new OpenFileDialog
            {
                Title = "Выбери запись ScreenLite", Filter = "Видео MP4 (*.mp4)|*.mp4",
                InitialDirectory = folder.Text, FileName = latestVideo ?? ""
            })
            {
                if (file.ShowDialog(this) != DialogResult.OK) return;
                video = file.FileName;
            }
            if (!File.Exists(AudioProject.ManifestFor(video)))
                throw new IOException("У этой записи нет отдельных дорожек. Выбери исходный MP4 из режима «Авто» или «Ручной» и оставь его папку .audio рядом.");
            project = AudioProject.Open(video);
            using (var picker = new AudioPicker("Изменить звук записи",
                "Сними галочки или измени громкость каждого источника. Можно прослушать фрагмент выбранной дорожки перед экспортом.",
                project.Tracks, null, project, project.Enhance))
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                selection = picker.SelectedTracks;
                levels = picker.Volumes;
                improve = picker.Enhance;
            }
        }
        catch (Exception failure) { ShowError(failure.Message); return; }
        finally { dialogOpen = false; }

        stopping = true;
        SetRecordingControls(true);
        recordButton.Enabled = false;
        status.Text = "Экспорт MP4 с выбранным звуком…";
        try
        {
            string stem = Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video));
            string destination = stem + " — звук.mp4";
            for (int version = 2; File.Exists(destination); version++)
                destination = stem + " — звук " + version + ".mp4";
            await Task.Run(delegate { project.Export(video, destination, selection, improve, levels); });
            status.Text = "Сохранено  ·  " + Path.GetFileName(destination);
        }
        catch (Exception failure) { ShowError(failure.Message); }
        finally { FinishBusyOperation(); }
    }

    private void FinishBusyOperation()
    {
        stopping = false;
        SetRecordingControls(false);
        if (closeAfterSave) Close();
    }
    private void ReleaseRecorder()
    {
        timer.Stop();
        elapsed.Reset();
        paused = active = false;
        if (recordingPanel != null)
        {
            recordingPanel.Dispose();
            recordingPanel = null;
        }
        if (recorder != null)
        {
            if (audioProject != null) recorder.OnAudioPacketRecorded -= audioProject.WritePacket;
            recorder.OnStatusChanged -= OnStatusChanged;
            recorder.OnRecordingComplete -= OnRecordingComplete;
            recorder.OnRecordingFailed -= OnRecordingFailed;
            recorder.Dispose();
            recorder = null;
        }
        stopping = false;
        SetRecordingControls(false);
    }

    private void SetRecordingControls(bool recording)
    {
        displays.Enabled = resolutions.Enabled = profiles.Enabled = mic.Enabled = browseButton.Enabled = !recording;
        enhance.Enabled = appsButton.Enabled = editButton.Enabled = settingsButton.Enabled = frameRates.Enabled = lightButton.Enabled = !recording;
        pauseButton.Enabled = recording && active && !stopping;
        if (!recording) pauseButton.Text = "Пауза";
        recordButton.Enabled = true;
        recordButton.Text = recording ? "Остановить и сохранить" : "Начать запись";
    }

    private void ShowError(string message)
    {
        status.Text = "Ошибка записи";
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (recorder == null && !stopping) return;
        e.Cancel = true;
        closeAfterSave = true;
        if (!stopping && recorder != null) StopRecording();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveRecorder;
        try { Run(); }
        catch (FileNotFoundException error)
        {
            MessageBox.Show("Распакуй ScreenLite.exe и папку data рядом друг с другом.\n\n" + error.Message,
                "ScreenLite", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Assembly ResolveRecorder(object sender, ResolveEventArgs args)
    {
        if (new AssemblyName(args.Name).Name != "ScreenRecorderLib") return null;
        return Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "ScreenRecorderLib.dll"));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RecorderForm());
    }
}
