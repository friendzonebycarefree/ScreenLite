using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyVersion("3.1.0.0")]
[assembly: AssemblyFileVersion("3.1.0.0")]

internal static class ScreenLiteUpdate
{
    private const string Repo = "https://github.com/friendzonebycarefree/ScreenLite/";
    private const string Latest = "https://api.github.com/repos/friendzonebycarefree/ScreenLite/releases/latest";
    private const string Prefix = ".screenlite-update-";
    private const long MaximumSize = 32 * 1024 * 1024;
    private static readonly string[] Files =
    {
        "data/ScreenLiteMedia.exe", "data/ScreenRecorderLib.dll",
        "data/ScreenRecorderLib.LICENSE.txt", "ScreenLite.exe"
    };

    public sealed class Release
    {
        public string tag_name { get; set; }
        public Asset[] assets { get; set; }
    }

    public sealed class Asset
    {
        public string name { get; set; }
        public string browser_download_url { get; set; }
        public string digest { get; set; }
        public long size { get; set; }
    }

    public static bool Start()
    {
        string install = AppDomain.CurrentDomain.BaseDirectory;
        using (var client = new WebClient())
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            client.Headers[HttpRequestHeader.UserAgent] = "ScreenLite";
            var release = new JavaScriptSerializer().Deserialize<Release>(client.DownloadString(Latest));
            Version version;
            if (release == null || release.tag_name == null ||
                !Version.TryParse(release.tag_name.TrimStart('v'), out version))
                throw new IOException("GitHub вернул неизвестный номер версии.");
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
            if (version <= Assembly.GetExecutingAssembly().GetName().Version) return false;

            string url = Repo + "releases/download/" + release.tag_name + "/ScreenLite.zip";
            Asset asset = (release.assets ?? new Asset[0]).SingleOrDefault(item =>
                item.name == "ScreenLite.zip" && item.browser_download_url == url);
            if (asset == null || asset.size <= 0 || asset.size > MaximumSize ||
                asset.digest == null || !System.Text.RegularExpressions.Regex.IsMatch(asset.digest, "^sha256:[0-9a-fA-F]{64}$"))
                throw new IOException("У релиза нет архива с контрольной суммой SHA-256.");

            string stage = Path.Combine(install, Prefix + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(stage);
                string zip = Path.Combine(stage, "release.zip");
                using (Stream input = client.OpenRead(url))
                using (FileStream output = File.Create(zip))
                    CopyLimited(input, output, asset.size);
                using (var sha = SHA256.Create())
                using (FileStream input = File.OpenRead(zip))
                {
                    string digest = "sha256:" + BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                    if (input.Length != asset.size || !String.Equals(digest, asset.digest, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Контрольная сумма архива обновления не совпала.");
                }
                Unpack(zip, stage);
                File.Delete(zip);
                string exe = Path.Combine(stage, "ScreenLite.exe");
                if (AssemblyName.GetAssemblyName(exe).Version != version)
                    throw new IOException("Версия ScreenLite.exe не совпадает с релизом.");
                string helper = Path.Combine(stage, "update.exe");
                File.Copy(exe, helper);
                using (Process current = Process.GetCurrentProcess())
                    Launch(helper, "--apply-update " + current.Id, install);
                return true;
            }
            catch
            {
                TryDeleteStage(stage);
                throw;
            }
        }
    }

    private static void CopyLimited(Stream input, Stream output, long limit)
    {
        byte[] buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            limit -= count;
            if (limit < 0) throw new IOException("Размер обновления превышает ожидаемый.");
            output.Write(buffer, 0, count);
        }
    }

    private static void Unpack(string zip, string stage)
    {
        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            long remaining = MaximumSize;
            foreach (string file in Files)
            {
                string name = "ScreenLite/" + file;
                var entry = archive.Entries.SingleOrDefault(item => item.FullName.Replace('\\', '/') == name);
                if (entry == null || entry.Length <= 0 || entry.Length > remaining)
                    throw new IOException("Архив обновления неполный или слишком большой.");
                remaining -= entry.Length;
                string output = Path.Combine(stage, file);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                using (Stream input = entry.Open())
                using (FileStream stream = File.Create(output))
                    CopyLimited(input, stream, entry.Length);
            }
        }
    }

    public static bool HandleArguments(string[] args)
    {
        int processId;
        Guid token;
        if (args.Length == 2 && args[0] == "--apply-update" && int.TryParse(args[1], out processId))
        {
            string stage = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string name = Path.GetFileName(stage);
            if (name.StartsWith(Prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(name.Substring(Prefix.Length), "N", out token))
                Apply(stage, processId);
            return true;
        }

        if (args.Length == 3 && args[0] == "--finish-update" &&
            int.TryParse(args[1], out processId) && Guid.TryParseExact(args[2], "N", out token))
        {
            string stage = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Prefix + token.ToString("N"));
            System.Threading.Tasks.Task.Run(delegate
            {
                if (WaitForProcess(processId)) TryDeleteStage(stage);
            });
        }
        return false;
    }

    private static void Apply(string stage, int processId)
    {
        string install = Path.GetDirectoryName(stage);
        var replaced = new List<string>();
        try
        {
            if (!WaitForProcess(processId)) throw new IOException("Закрой предыдущий экземпляр ScreenLite и попробуй снова.");
            foreach (string file in Files)
            {
                File.Replace(Path.Combine(stage, file), Path.Combine(install, file), Path.Combine(stage, file + ".old"));
                replaced.Add(file);
            }
            using (Process current = Process.GetCurrentProcess())
                Launch(Path.Combine(install, "ScreenLite.exe"),
                    "--finish-update " + current.Id + " " + Path.GetFileName(stage).Substring(Prefix.Length), install);
        }
        catch (Exception error)
        {
            try
            {
                replaced.Reverse();
                foreach (string file in replaced)
                    File.Replace(Path.Combine(stage, file + ".old"), Path.Combine(install, file), null);
            }
            catch (Exception rollback)
            {
                MessageBox.Show("Обновление прервано. Резервные файлы в " + stage + "\n" + rollback.Message, "ScreenLite");
                return;
            }
            MessageBox.Show(error.Message, "ScreenLite · обновление");
        }
    }

    private static bool WaitForProcess(int id)
    {
        try { using (Process process = Process.GetProcessById(id)) return process.WaitForExit(20000); }
        catch (ArgumentException) { return true; }
    }

    private static void Launch(string path, string arguments, string directory)
    {
        using (Process process = Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = directory
        }))
        {
            if (process == null) throw new IOException("Не удалось запустить ScreenLite.");
        }
    }

    private static void TryDeleteStage(string stage)
    {
        try
        {
            foreach (string file in Files)
            {
                File.Delete(Path.Combine(stage, file));
                File.Delete(Path.Combine(stage, file + ".old"));
            }
            File.Delete(Path.Combine(stage, "release.zip"));
            File.Delete(Path.Combine(stage, "update.exe"));
            string data = Path.Combine(stage, "data");
            if (Directory.Exists(data)) Directory.Delete(data);
            Directory.Delete(stage);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
