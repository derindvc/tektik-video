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
using Microsoft.Win32.SafeHandles;

internal static class Host
{
    private const int MaxRequestBytes = 16384;
    private const string ProgressPrefix = "TEKTIK_PROGRESS ";
    private const string FilePrefix = "TEKTIK_FILE ";
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
    private static Stream output;
    private static int disconnected;

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
                        Download(uri.AbsoluteUri, input);
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

    private static string DownloadFolder()
    {
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

    private static void Download(string url, BinaryReader input)
    {
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
            var startInfo = new ProcessStartInfo {
                FileName = downloader,
                Arguments = BuildArguments(staging, tools, deno, url),
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
                Error("Bu sayfada indirilebilir bir video bulunamadı.");
                return;
            }
            string path = Path.GetFullPath(stagedFile);
            if (!String.Equals(Path.GetDirectoryName(path), staging, StringComparison.OrdinalIgnoreCase)
                || !VideoExtensions.Contains(Path.GetExtension(path)) || !File.Exists(path)
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

    private static string BuildArguments(string staging, string tools, string deno, string url)
    {
        string[] arguments = {
            "--ignore-config", "--no-plugin-dirs", "--no-remote-components", "--no-playlist",
            "--playlist-items", "1", "--match-filter", "!is_live", "--windows-filenames",
            "--no-mtime", "--socket-timeout", "20", "--encoding", "utf-8",
            "--ffmpeg-location", tools, "--js-runtimes", "deno:" + deno,
            "--format", "bv*+ba/b", "--merge-output-format", "mp4/mkv",
            "--paths", staging, "--output", "%(title).100s.%(ext)s",
            "--color", "never", "--no-simulate", "--progress", "--newline",
            "--progress-delta", "0.75",
            "--progress-template", "download:" + ProgressPrefix + "%(progress._percent)s",
            "--progress-template", "postprocess:" + ProgressPrefix + "NA",
            "--print", "after_move:" + FilePrefix + "%(filepath)j",
            "--", url
        };
        return String.Join(" ", Array.ConvertAll(arguments, QuoteArgument));
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
        if (line.IndexOf("sign in", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("private video", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Kaynak site bu video için oturum açılmasını veya ek doğrulama istiyor.";
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
