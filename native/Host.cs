using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

internal static class Host
{
    private const int MaxRequestBytes = 262144;
    private const string ProgressPrefix = "TEKTIK_PROGRESS ";
    private const string FilePrefix = "TEKTIK_FILE ";
    private const string DurationPrefix = "TEKTIK_DURATION ";
    private const string SizePrefix = "TEKTIK_SIZE ";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly object OutputLock = new object();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer {
        MaxJsonLength = MaxRequestBytes, RecursionLimit = 8
    };
    private static readonly HashSet<string> VideoExtensions = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase) {
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".mpg", ".mpeg",
        ".3gp", ".3g2", ".wmv", ".flv", ".mts", ".m2ts", ".ogv", ".ts"
    };
    private static readonly HashSet<string> AudioExtensions = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".opus", ".ogg", ".wav", ".flac", ".aac" };
    private static Stream output;
    private static int disconnected;

    [STAThread]
    private static void Main()
    {
        output = Console.OpenStandardOutput();
        using (var input = new BinaryReader(Console.OpenStandardInput(), Utf8))
        {
            try
            {
                var request = ReadRequest(input);
                if (request == null)
                    return;

                switch (ReadString(request, "type"))
                {
                    case "ping":
                        Send(new { type = "ready" });
                        break;
                    case "openFolder":
                        OpenFolder();
                        break;
                    case "getFolder":
                        Send(new { type = "folder", path = DownloadFolder() });
                        break;
                    case "chooseFolder":
                        ChooseFolder();
                        break;
                    case "download":
                        string url = ReadString(request, "url");
                        Uri uri;
                        if (String.IsNullOrEmpty(url) || url.Length > 12000
                            || !Uri.TryCreate(url, UriKind.Absolute, out uri)
                            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                            || String.IsNullOrEmpty(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo)
                            || !uri.IsWellFormedOriginalString())
                        {
                            Error("Geçerli bir HTTP veya HTTPS video adresi gerekiyor.");
                            return;
                        }
                        Download(uri.AbsoluteUri, input, request);
                        break;
                    default:
                        Error("Bu işlem desteklenmiyor.");
                        break;
                }
            }
            catch (InvalidDataException)
            {
                Error("Tarayıcıdan gelen istek okunamadı.");
            }
            catch (UnauthorizedAccessException)
            {
                Error("İndirilenler klasörüne erişilemiyor.");
            }
            catch (IOException)
            {
                Error("Dosya kaydedilemedi. Klasör erişimini ve disk alanını kontrol et.");
            }
            catch (Exception)
            {
                Error("Yerel yardımcı başlatılamadı. Kurulumu yeniden çalıştırmayı dene.");
            }
        }
    }

    private static Dictionary<string, object> ReadRequest(BinaryReader input)
    {
        byte[] header = input.ReadBytes(sizeof(int));
        if (header.Length == 0)
            return null;
        if (header.Length != sizeof(int))
            throw new InvalidDataException();
        int length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > MaxRequestBytes)
            throw new InvalidDataException();
        byte[] bytes = input.ReadBytes(length);
        if (bytes.Length != length)
            throw new InvalidDataException();
        try
        {
            var request = Json.Deserialize<Dictionary<string, object>>(Utf8.GetString(bytes));
            if (request == null)
                throw new InvalidDataException();
            return request;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Invalid JSON request.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("Invalid JSON request.", exception);
        }
    }

    private static string ReadString(Dictionary<string, object> request, string key)
    {
        object value;
        return request.TryGetValue(key, out value) ? value as string : null;
    }

    private static void Send(object message)
    {
        lock (OutputLock)
        {
            if (Thread.VolatileRead(ref disconnected) != 0)
                return;
            try
            {
                byte[] bytes = Utf8.GetBytes(Json.Serialize(message));
                byte[] header = BitConverter.GetBytes(bytes.Length);
                output.Write(header, 0, header.Length);
                output.Write(bytes, 0, bytes.Length);
                output.Flush();
            }
            catch (IOException)
            {
                Interlocked.Exchange(ref disconnected, 1);
            }
        }
    }

    private static void Error(string message)
    {
        Send(new { type = "error", message = message });
    }

    private static void OpenFolder()
    {
        var info = new ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            Arguments = QuoteArgument(DownloadFolder()),
            UseShellExecute = false
        };
        using (Process.Start(info))
            Send(new { type = "opened" });
    }

    private static void ChooseFolder()
    {
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = "Videoların kaydedileceği klasörü seç";
            dialog.SelectedPath = DownloadFolder();
            if (dialog.ShowDialog() != DialogResult.OK)
            {
                Send(new { type = "cancelled" });
                return;
            }
            string path = Directory.CreateDirectory(dialog.SelectedPath).FullName;
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "folder.txt"), path, Utf8);
            Send(new { type = "folder", path = path });
        }
    }

    private static string DownloadFolder()
    {
        string setting = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "folder.txt");
        if (File.Exists(setting))
        {
            string chosen = File.ReadAllText(setting, Utf8).Trim();
            if (Path.IsPathRooted(chosen) && Directory.Exists(chosen))
                return chosen;
        }
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        IntPtr pointer;
        int result = SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out pointer);
        string root;
        try
        {
            root = result == 0 ? Marshal.PtrToStringUni(pointer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
        if (String.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        return Directory.CreateDirectory(Path.Combine(root, "TekTik Video")).FullName;
    }

    private static void Download(string url, BinaryReader input, Dictionary<string, object> request)
    {
        string quality = ReadString(request, "quality") ?? "best";
        string mode = ReadString(request, "mode") ?? "video";
        object limitValue;
        int maxMinutes = request.TryGetValue("maxMinutes", out limitValue) && limitValue is int ? (int)limitValue : 0;
        if (!(quality == "best" || quality == "1080" || quality == "720" || quality == "480")
            || !(mode == "video" || mode == "audio")
            || !(maxMinutes == 0 || maxMinutes == 10 || maxMinutes == 30 || maxMinutes == 60 || maxMinutes == 120))
        {
            Error("İndirme ayarları geçersiz.");
            return;
        }
        string tools = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
        string downloader = Path.Combine(tools, "yt-dlp.exe");
        string deno = Path.Combine(tools, "deno.exe");
        if (!File.Exists(downloader) || !File.Exists(deno)
            || !File.Exists(Path.Combine(tools, "ffmpeg.exe"))
            || !File.Exists(Path.Combine(tools, "ffprobe.exe")))
        {
            Error("İndirme bileşenleri eksik. TekTık Video kurulumunu yeniden çalıştır.");
            return;
        }

        string folder = DownloadFolder();
        string staging = Directory.CreateDirectory(Path.Combine(folder, ".tektik-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.SetAttributes(staging, File.GetAttributes(staging) | FileAttributes.Hidden);
            string cookiesPath = WriteCookies(request, staging, url);
            var startInfo = new ProcessStartInfo {
                FileName = downloader,
                Arguments = BuildArguments(staging, tools, deno, url, quality, mode, maxMinutes, cookiesPath),
                WorkingDirectory = tools,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            string stagedFile = null;
            string failure = null;
            using (SafeFileHandle job = CreateDownloadJob())
            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    var exception = new Win32Exception(Marshal.GetLastWin32Error());
                    if (!process.HasExited)
                        process.Kill();
                    throw exception;
                }
                process.StandardInput.Close();
                WatchDisconnect(input, job);
                Send(new { type = "started" });
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data == null || ReportProgress(args.Data))
                        return;
                    string knownFailure = DownloadFailure(args.Data);
                    if (knownFailure != null)
                        failure = knownFailure;
                };
                process.BeginErrorReadLine();

                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith(FilePrefix, StringComparison.Ordinal))
                        stagedFile = Json.Deserialize<string>(line.Substring(FilePrefix.Length));
                    else if (line.StartsWith(DurationPrefix, StringComparison.Ordinal))
                        ReportMediaInfo("duration", line.Substring(DurationPrefix.Length));
                    else if (line.StartsWith(SizePrefix, StringComparison.Ordinal))
                        ReportMediaInfo("size", line.Substring(SizePrefix.Length));
                    else
                        ReportProgress(line);
                }
                process.WaitForExit();
                if (Thread.VolatileRead(ref disconnected) != 0)
                    return;
                if (process.ExitCode != 0)
                {
                    Error(failure ?? "Video indirilemedi. Sayfanın erişilebilir olduğunu kontrol edip yeniden dene.");
                    return;
                }
            }

            if (stagedFile == null)
            {
                Error(maxMinutes > 0 ? "Video seçilen süre sınırını aşıyor veya indirilebilir bir dosya bulunamadı."
                    : "Bu sayfada indirilebilir bir video bulunamadı.");
                return;
            }
            string path = Path.GetFullPath(stagedFile);
            if (!String.Equals(Path.GetDirectoryName(path), staging, StringComparison.OrdinalIgnoreCase)
                || !(mode == "audio" ? AudioExtensions : VideoExtensions).Contains(Path.GetExtension(path)) || !File.Exists(path)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                Error("Bu sayfadan desteklenen bir video dosyası alınamadı.");
                return;
            }
            string filename = SaveVideo(path, folder);
            Send(new { type = "completed", filename = filename });
        }
        finally
        {
            try { Directory.Delete(staging, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ReportMediaInfo(string field, string raw)
    {
        double value;
        if (Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && value >= 0 && !Double.IsInfinity(value) && !Double.IsNaN(value))
            Send(new { type = "info", field = field, value = value });
    }

    private static string WriteCookies(Dictionary<string, object> request, string staging, string url)
    {
        object value;
        if (!request.TryGetValue("cookies", out value)) return null;
        var entries = value as System.Collections.ArrayList;
        if (entries == null || entries.Count > 100) throw new InvalidDataException();
        string host = new Uri(url).Host;
        string path = Path.Combine(staging, "cookies.txt");
        using (var writer = new StreamWriter(path, false, Utf8))
        {
            writer.NewLine = "\r\n";
            writer.WriteLine("# Netscape HTTP Cookie File");
            foreach (object item in entries)
            {
                var cookie = item as Dictionary<string, object>;
                if (cookie == null) throw new InvalidDataException();
                string domain = ReadString(cookie, "domain") ?? "";
                string name = ReadString(cookie, "name") ?? "";
                string content = ReadString(cookie, "value") ?? "";
                string cookiePath = ReadString(cookie, "path") ?? "/";
                string bareDomain = domain.TrimStart('.');
                if (!(host == bareDomain || host.EndsWith("." + bareDomain, StringComparison.OrdinalIgnoreCase))
                    || String.IsNullOrEmpty(bareDomain) || name.Length > 256 || content.Length > 4096
                    || name.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0
                    || content.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0
                    || cookiePath.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
                    throw new InvalidDataException();
                bool secure = cookie.ContainsKey("secure") && cookie["secure"] is bool && (bool)cookie["secure"];
                writer.WriteLine(String.Join("\t", domain, domain.StartsWith(".") ? "TRUE" : "FALSE",
                    cookiePath, secure ? "TRUE" : "FALSE", "0", name, content));
            }
        }
        return path;
    }

    private static string BuildArguments(string staging, string tools, string deno, string url,
        string quality, string mode, int maxMinutes, string cookiesPath)
    {
        var arguments = new List<string> {
            "--ignore-config", "--no-plugin-dirs", "--no-remote-components", "--no-playlist",
            "--playlist-items", "1", "--match-filter", maxMinutes > 0
                ? "!is_live & duration & duration <= " + (maxMinutes * 60) : "!is_live", "--windows-filenames",
            "--no-mtime", "--socket-timeout", "20", "--encoding", "utf-8",
            "--ffmpeg-location", tools, "--js-runtimes", "deno:" + deno,
            "--format", mode == "audio" ? "bestaudio/best" : quality == "best" ? "bv*+ba/b" :
                "bv*[height<=" + quality + "]+ba/b[height<=" + quality + "]",
            "--merge-output-format", "mp4/mkv",
            "--paths", staging, "--output", "%(title).100s.%(ext)s",
            "--color", "never", "--no-simulate", "--progress", "--newline",
            "--progress-delta", "0.75",
            "--progress-template", "download:" + ProgressPrefix + "%(progress._percent)s",
            "--progress-template", "postprocess:" + ProgressPrefix + "NA",
            "--print", "before_dl:" + DurationPrefix + "%(duration)s",
            "--print", "before_dl:" + SizePrefix + "%(filesize,filesize_approx)s",
            "--print", "after_move:" + FilePrefix + "%(filepath)j",
        };
        if (mode == "audio") arguments.AddRange(new[] { "--extract-audio", "--audio-format", "mp3" });
        if (cookiesPath != null) arguments.AddRange(new[] { "--cookies", cookiesPath });
        arguments.Add("--");
        arguments.Add(url);
        return String.Join(" ", Array.ConvertAll(arguments.ToArray(), QuoteArgument));
    }

    // ProcessStartInfo uses one command-line string on .NET Framework. Escape each
    // argument according to Windows argv rules before passing it directly to yt-dlp.
    private static string QuoteArgument(string value)
    {
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
                quoted.Append('\\', backslashes + 1);
            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static bool ReportProgress(string line)
    {
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            return false;
        double percent;
        string number = line.Substring(ProgressPrefix.Length);
        double? progress = Double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)
            && !Double.IsNaN(percent) && !Double.IsInfinity(percent)
            ? (double?)Math.Round(Math.Max(0, Math.Min(99, percent)), 1)
            : null;
        Send(new { type = "progress", percent = progress });
        return true;
    }

    private static string DownloadFailure(string line)
    {
        if (!line.StartsWith("ERROR:", StringComparison.Ordinal))
            return null;
        if (line.IndexOf("DRM", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Bu video DRM korumalı.";
        if (line.IndexOf("unsupported url", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Bu sayfa video indirmeyi desteklemiyor.";
        if (line.IndexOf("Requested format is not available", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Seçilen kalite bu videoda bulunamadı. Ayarlardan En iyi mevcut kalite seçeneğini dene.";
        if (line.IndexOf("sign in", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("private video", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Site oturum açılmasını istiyor. Video sayfasında sağ tıklayıp Bu sitenin oturum çerezleriyle indir seçeneğini dene.";
        if (line.IndexOf("HTTP Error 429", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Kaynak site çok fazla istek aldı. Bir süre sonra yeniden dene.";
        return null;
    }

    private static string SaveVideo(string path, string folder)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 1; index <= 1000; index++)
        {
            string name = index == 1 ? stem + extension
                : stem + " (" + index.ToString(CultureInfo.InvariantCulture) + ")" + extension;
            string destination = Path.Combine(folder, name);
            try
            {
                File.Move(path, destination);
                return name;
            }
            catch (IOException)
            {
                if (!File.Exists(destination) && !Directory.Exists(destination))
                    throw;
            }
        }
        throw new IOException("No unused file name.");
    }

    // A closed browser port cancels yt-dlp and any child tools. The Windows job
    // also terminates the children if the browser shuts down the host abruptly.
    private static void WatchDisconnect(BinaryReader input, SafeFileHandle job)
    {
        var watcher = new Thread(delegate()
        {
            try { input.ReadByte(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            Interlocked.Exchange(ref disconnected, 1);
            job.Dispose();
        }) { IsBackground = true };
        watcher.Start();
    }

    private static SafeFileHandle CreateDownloadJob()
    {
        const int ExtendedLimitInformation = 9;
        const uint KillOnJobClose = 0x2000;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var limits = new JobExtendedLimits();
        limits.Basic.LimitFlags = KillOnJobClose;
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits,
            (uint)Marshal.SizeOf(typeof(JobExtendedLimits))))
        {
            var exception = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw exception;
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits Basic;
        public JobIoCounters IoCounters;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobExtendedLimits info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
