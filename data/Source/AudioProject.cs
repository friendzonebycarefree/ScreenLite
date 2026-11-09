using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using ScreenRecorderLib;

internal sealed class AudioTrack
{
    public string Name;
    public string SourceId;
    public bool Microphone;
    public volatile int Volume = 100;
    public volatile bool Enabled = true;
    private double peak;
    public double Peak { get { return Volatile.Read(ref peak); } set { Volatile.Write(ref peak, value); } }
    public int EffectiveVolume { get { return Enabled ? Volume : 0; } }
}

internal sealed class AudioProject : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameBytes = 4;
    private readonly string directory;
    private readonly Dictionary<string, int> indexes = new Dictionary<string, int>();
    private BinaryWriter[] writers;
    private byte[] scaled = new byte[0];
    public volatile bool Paused;
    public readonly AudioTrack[] Tracks;
    public readonly bool Enhance;
    private long frames;
    private Exception writeError;

    public static string DirectoryFor(string video) { return Path.ChangeExtension(video, ".audio"); }
    public static string ManifestFor(string video) { return Path.Combine(DirectoryFor(video), "tracks.xml"); }

    public AudioProject(string video, AudioTrack[] tracks, bool enhance)
    {
        directory = DirectoryFor(video);
        Tracks = tracks;
        Enhance = enhance;
    }

    public void Start()
    {
        Directory.CreateDirectory(directory);
        writers = new BinaryWriter[Tracks.Length];
        for (int i = 0; i < Tracks.Length; i++)
        {
            indexes.Add(Tracks[i].SourceId, i);
            writers[i] = new BinaryWriter(new FileStream(
                TrackPath(i), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536));
        }
    }

    private string TrackPath(int index) { return Path.Combine(directory, (index + 1) + ".pcmtrack"); }

    // The recorder aligns these 48 kHz stereo PCM packets on a common timeline.
    // Silent packets are represented by gaps so excluded or idle apps cost no disk space.
    public void WritePacket(object sender, AudioDataRecordedEventArgs e)
    {
        if (Paused || writeError != null || e.AudioData == null || e.AudioData.Data == null) return;
        int size = e.AudioData.Data.Length;
        if (size == 0) return;
        try
        {
            if (size % FrameBytes != 0) throw new IOException("Неверный формат пакета звука.");
            foreach (AudioPacketSource source in e.AudioData.Sources)
            {
                int index;
                if (!indexes.TryGetValue(source.Id, out index)) continue;
                AudioTrack track = Tracks[index];
                track.Peak = source.Gain;
                int volume = track.EffectiveVolume;
                if (source.Gain == 0 || volume == 0) continue;
                if (source.Data.Length != size) throw new IOException("Не совпадают размеры пакетов звука.");
                byte[] data = source.Data;
                if (volume != 100)
                {
                    if (scaled.Length < size) scaled = new byte[size];
                    ScaleSamples(data, scaled, size, volume);
                    data = scaled;
                }
                BinaryWriter writer = writers[index];
                writer.Write(frames);
                writer.Write(size);
                writer.Write(data, 0, size);
            }
            frames += size / FrameBytes;
        }
        catch (Exception error) { writeError = error; }
    }

    public void Finish()
    {
        Dispose();
        if (writeError != null) throw new IOException("Не удалось сохранить раздельный звук.", writeError);
        using (var manifest = XmlWriter.Create(Path.Combine(directory, "tracks.xml"),
            new XmlWriterSettings { Indent = true }))
        {
            manifest.WriteStartElement("screenlite-audio");
            manifest.WriteAttributeString("version", "1");
            manifest.WriteAttributeString("frames", XmlConvert.ToString(frames));
            manifest.WriteAttributeString("enhance", XmlConvert.ToString(Enhance));
            foreach (AudioTrack track in Tracks)
            {
                manifest.WriteStartElement("track");
                manifest.WriteAttributeString("mic", XmlConvert.ToString(track.Microphone));
                manifest.WriteString(track.Name);
                manifest.WriteEndElement();
            }
            manifest.WriteEndElement();
        }
    }

    public static AudioProject Open(string video)
    {
        var xml = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(ManifestFor(video),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 65536 }))
            xml.Load(reader);
        XmlElement root = xml.DocumentElement;
        if (root == null || root.Name != "screenlite-audio" || root.GetAttribute("version") != "1")
            throw new IOException("Этот формат проекта звука не поддерживается.");
        var tracks = root.SelectNodes("track").Cast<XmlElement>().Select(track => new AudioTrack
        {
            Name = track.InnerText,
            Microphone = XmlConvert.ToBoolean(track.GetAttribute("mic"))
        }).ToArray();
        long frames = XmlConvert.ToInt64(root.GetAttribute("frames"));
        if (frames < 0 || frames > SampleRate * 86400L * 7 || tracks.Length > 256)
            throw new IOException("Повреждены данные проекта звука.");
        return new AudioProject(video, tracks, XmlConvert.ToBoolean(root.GetAttribute("enhance"))) { frames = frames };
    }

    public void Export(string video, string destination, int[] selected, bool improve, int[] volumes = null)
    {
        string token = Guid.NewGuid().ToString("N");
        string pcm = Path.Combine(directory, token + ".mix.pcm");
        string staging = Path.Combine(Path.GetDirectoryName(destination), ".screenlite-" + token + ".mp4");
        try
        {
            if (selected.Length > 0 && frames > 0) Mix(pcm, selected, improve, volumes);
            MediaTool.Run("mux", video, File.Exists(pcm) ? pcm : "-", staging);
            File.Move(staging, destination);
        }
        finally
        {
            DeleteIfPresent(pcm);
            DeleteIfPresent(staging);
        }
    }

    public void DeleteTemporaryTracks()
    {
        for (int i = 0; i < Tracks.Length; i++) DeleteIfPresent(TrackPath(i));
        DeleteIfPresent(Path.Combine(directory, "tracks.xml"));
        try { Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void DeleteIfPresent(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Mix(string path, int[] selected, bool improve, int[] volumes)
    {
        const int blockFrames = SampleRate / 100;
        var tracks = new List<TrackReader>();
        try
        {
            foreach (int index in selected)
                tracks.Add(new TrackReader(TrackPath(index), improve && Tracks[index].Microphone,
                    volumes == null ? 100 : volumes[index]));
            var sum = new float[blockFrames * 2];
            var bytes = new byte[blockFrames * FrameBytes];
            double envelope = 0.02, gain = 1, limiter = 1;
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                for (long offset = 0; offset < frames; offset += blockFrames)
                {
                    int count = (int)Math.Min(blockFrames, frames - offset);
                    int samples = count * 2;
                    Array.Clear(sum, 0, samples);
                    foreach (TrackReader track in tracks) track.AddTo(sum, offset, count);
                    double energy = 0, peak = 0;
                    for (int i = 0; i < samples; i++)
                    {
                        energy += sum[i] * sum[i];
                        peak = Math.Max(peak, Math.Abs(sum[i]));
                    }

                    if (improve)
                    {
                        double rms = Math.Sqrt(energy / samples);
                        envelope += (rms - envelope) * (rms > envelope ? 0.12 : 0.02);
                        double target = envelope < 0.025 ? 1 : Math.Min(2.25, Math.Max(0.5, 0.15 / envelope));
                        gain += (target - gain) * 0.035;
                    }
                    double limit = improve ? 0.89 : 1.0;
                    double safe = Math.Min(1.0, limit / Math.Max(peak * gain, 0.0001));
                    limiter = Math.Min(safe, limiter + 0.075 * (1.0 - limiter));
                    for (int i = 0; i < samples; i++)
                    {
                        int value = (int)Math.Round(sum[i] * gain * limiter * 32767);
                        bytes[i * 2] = (byte)value;
                        bytes[i * 2 + 1] = (byte)(value >> 8);
                    }
                    output.Write(bytes, 0, count * FrameBytes);
                }
            }
        }
        finally { foreach (TrackReader track in tracks) track.Dispose(); }
    }

    private static void ScaleSamples(byte[] input, byte[] output, int length, int volume)
    {
        for (int i = 0; i < length; i += 2)
        {
            int sample = (short)(input[i] | (input[i + 1] << 8)) * volume / 100;
            sample = Math.Max(short.MinValue, Math.Min(short.MaxValue, sample));
            output[i] = (byte)sample;
            output[i + 1] = (byte)(sample >> 8);
        }
    }

    public MemoryStream Preview(int index, int volume, out double peak)
    {
        const int count = SampleRate * 2;
        long start;
        using (var reader = new BinaryReader(File.OpenRead(TrackPath(index))))
            start = reader.BaseStream.Length == 0 ? frames : reader.ReadInt64();
        int sampleFrames = (int)Math.Max(0, Math.Min(count, frames - start));
        if (sampleFrames == 0)
        {
            peak = 0;
            return null;
        }
        var samples = new float[sampleFrames * 2];
        using (var reader = new TrackReader(TrackPath(index), false, 100))
            reader.AddTo(samples, start, sampleFrames);
        peak = samples.Max(value => Math.Abs(value));
        var wave = new MemoryStream();
        using (var writer = new BinaryWriter(wave, System.Text.Encoding.ASCII, true))
        {
            int length = sampleFrames * FrameBytes;
            writer.Write(0x46464952);
            writer.Write(length + 36);
            writer.Write(0x45564157);
            writer.Write(0x20746d66);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(SampleRate);
            writer.Write(SampleRate * FrameBytes);
            writer.Write((short)FrameBytes);
            writer.Write((short)16);
            writer.Write(0x61746164);
            writer.Write(length);
            foreach (float sample in samples)
                writer.Write((short)Math.Round(32767 * Math.Max(-1, Math.Min(1, sample * volume / 100f))));
        }
        wave.Position = 0;
        return wave;
    }

    public void Dispose()
    {
        if (writers == null) return;
        foreach (BinaryWriter writer in writers)
        {
            try { if (writer != null) writer.Dispose(); }
            catch (IOException error) { if (writeError == null) writeError = error; }
        }
        writers = null;
    }

    private sealed class TrackReader : IDisposable
    {
        private readonly BinaryReader reader;
        private readonly bool highPass;
        private readonly float volume;
        private readonly float[] previousInput = new float[2];
        private readonly float[] previousOutput = new float[2];
        private byte[] data = new byte[0];
        private long packetStart, packetEnd;

        public TrackReader(string path, bool filter, int level)
        {
            reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536));
            highPass = filter;
            volume = level / 100f;
        }

        private void Advance()
        {
            if (reader.BaseStream.Position == reader.BaseStream.Length)
            {
                packetStart = packetEnd = long.MaxValue;
                return;
            }
            long start = reader.ReadInt64();
            int bytes = reader.ReadInt32();
            if (start < packetEnd || start > SampleRate * 86400L * 7 || bytes <= 0 ||
                bytes > FrameBytes * SampleRate * 30 || bytes % FrameBytes != 0)
                throw new IOException("Повреждена дорожка звука.");
            if (data.Length < bytes) data = new byte[bytes];
            int read = 0;
            while (read < bytes)
            {
                int next = reader.Read(data, read, bytes - read);
                if (next == 0) throw new EndOfStreamException("Дорожка звука записана не полностью.");
                read += next;
            }
            packetStart = start;
            packetEnd = start + bytes / FrameBytes;
        }

        public void AddTo(float[] mix, long position, int count)
        {
            if (!highPass && packetStart >= position + count) return;
            for (int i = 0; i < count; i++)
            {
                long frame = position + i;
                while (frame >= packetEnd) Advance();
                bool exists = frame >= packetStart;
                if (!exists && !highPass)
                {
                    if (packetStart >= position + count) break;
                    i = (int)(packetStart - position) - 1;
                    continue;
                }
                for (int channel = 0; channel < 2; channel++)
                {
                    int sample = exists ? (int)(frame - packetStart) * FrameBytes + channel * 2 : 0;
                    float value = exists ? (short)(data[sample] | (data[sample + 1] << 8)) / 32768f : 0;
                    if (highPass)
                    {
                        float filtered = 0.9896f * (previousOutput[channel] + value - previousInput[channel]);
                        previousInput[channel] = value;
                        previousOutput[channel] = filtered;
                        value = filtered;
                    }
                    mix[i * 2 + channel] += value * volume;
                }
            }
        }

        public void Dispose() { reader.Dispose(); }
    }
}
