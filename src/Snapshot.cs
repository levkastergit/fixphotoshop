using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace FixPhotoshop
{
    sealed class FileStamp
    {
        public string FilePath;
        public string Version;
        public DateTime Created;
        public DateTime Modified;
        // Installers may keep the build date as "modified", so the later of the two is the time it landed on disk.
        public DateTime Effective { get { return Created > Modified ? Created : Modified; } }
    }

    sealed class AcrState
    {
        public bool RootExists;
        public List<string> TempDisable = new List<string>();
        public List<string> GpuFiles = new List<string>();
        public List<string> GpuAppDirs = new List<string>();
        public string GpuConfigText;
        public bool DisableInferenceFile;
        public long CacheBytes;
        public int CacheFiles;
        public List<string> UserData = new List<string>();
        public List<string> LogTails = new List<string>();
        public List<string> ModelZoo = new List<string>();
        public List<string> ModelLogLines = new List<string>();
    }

    sealed class Snapshot
    {
        public const int ChangeWindowDays = 21;

        public DateTime TakenAt = DateTime.Now;
        public string Os = "";
        public double RamGb;
        public double RamFreeGb;
        public List<string> GpuLines = new List<string>();
        public List<string> RealGpus = new List<string>();
        public string NvidiaSmi;
        public string HwSchMode = "нет данных";
        public List<string> DiskLines = new List<string>();
        public long SystemFreeBytes = -1;
        public List<FileStamp> Photoshops = new List<FileStamp>();
        public List<FileStamp> AcrPlugins = new List<FileStamp>();
        public List<LogEvent> PhotoshopEvents = new List<LogEvent>();
        public List<LogEvent> SystemEvents = new List<LogEvent>();
        public List<Change> Changes = new List<Change>();
        public AcrState Acr = new AcrState();
        public List<string> Problems = new List<string>();

        // The earliest recorded Photoshop hang/crash in the recent window, used only to line up what changed around it.
        public DateTime? Onset
        {
            get
            {
                DateTime from = DateTime.Now.AddDays(-ChangeWindowDays);
                var hits = PhotoshopEvents.Where(e => e.When >= from).OrderBy(e => e.When).ToList();
                if (hits.Count == 0) return null;
                return hits[0].When;
            }
        }

        public static double FreeRamGb()
        {
            var m = new Native.MEMORYSTATUSEX();
            m.dwLength = 64;
            return Native.GlobalMemoryStatusEx(ref m) ? m.ullAvailPhys / 1073741824.0 : -1;
        }

        void Step(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { Problems.Add(name + ": " + ex.Message); }
        }

        public static Snapshot Collect()
        {
            var s = new Snapshot();
            s.Step("Система", s.ReadSystem);
            s.Step("Видеокарты", s.ReadGpus);
            s.Step("Диски", s.ReadDisks);
            s.Step("Photoshop", s.ReadAdobeFiles);
            s.Step("Журнал приложений", s.ReadPhotoshopEvents);
            s.Step("Журнал системы", s.ReadSystemEvents);
            s.Step("Обновления Windows", s.ReadHotfixes);
            s.Step("Установленные программы", s.ReadInstalled);
            s.Step("Настройки Camera Raw", s.ReadAcr);
            s.Changes = s.Changes.OrderByDescending(c => c.When).ToList();
            return s;
        }

        // Camera Raw writes its logs and failure markers during the failure, so they are read again at that moment.
        public void RefreshAcr()
        {
            Acr = new AcrState();
            Step("Настройки Camera Raw", ReadAcr);
        }

        void ReadSystem()
        {
            using (var q = new ManagementObjectSearcher("SELECT Caption, BuildNumber, TotalVisibleMemorySize FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject o in q.Get())
                {
                    Os = o["Caption"] + ", сборка " + o["BuildNumber"];
                    RamGb = Convert.ToDouble(o["TotalVisibleMemorySize"]) / 1048576.0;
                }
            }
            RamFreeGb = FreeRamGb();
            object hags = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", null);
            if (hags != null) HwSchMode = hags.ToString() == "2" ? "включено" : "выключено";
        }

        void ReadGpus()
        {
            using (var q = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, Status FROM Win32_VideoController"))
            {
                foreach (ManagementObject o in q.Get())
                {
                    string name = Convert.ToString(o["Name"]);
                    string date = "";
                    string raw = Convert.ToString(o["DriverDate"]);
                    if (!string.IsNullOrEmpty(raw))
                        date = Util.Try(() => ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd"), raw);
                    GpuLines.Add(string.Format("{0} | драйвер {1} от {2} | статус {3}", name, o["DriverVersion"], date, o["Status"]));
                    string lower = name.ToLowerInvariant();
                    if (lower.Contains("nvidia") || lower.Contains("geforce") || lower.Contains("quadro") || lower.Contains("radeon") ||
                        lower.Contains("amd") || lower.Contains("intel") || lower.Contains("arc "))
                        RealGpus.Add(name);
                }
            }
            NvidiaSmi = QueryNvidiaSmi();
        }

        public static string QueryNvidiaSmi()
        {
            string exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(exe)) return null;
            var psi = new ProcessStartInfo(exe, "--query-gpu=name,driver_version,memory.used,memory.total,utilization.gpu,temperature.gpu,pstate --format=csv,noheader");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            try
            {
                using (var p = Process.Start(psi))
                {
                    var read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(8000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        return "ТАЙМАУТ: nvidia-smi не ответил за 8 с";
                    }
                    return read.Wait(1000) ? read.Result.Trim() : "";
                }
            }
            catch (Exception ex) { return "не удалось запустить nvidia-smi: " + ex.Message; }
        }

        void ReadDisks()
        {
            string sys = Path.GetPathRoot(Environment.SystemDirectory);
            foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
            {
                DiskLines.Add(string.Format("{0} свободно {1} из {2}", d.Name, Util.Gb(d.AvailableFreeSpace), Util.Gb(d.TotalSize)));
                if (string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase)) SystemFreeBytes = d.AvailableFreeSpace;
            }
        }

        static FileStamp Stamp(string path)
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return new FileStamp
            {
                FilePath = path,
                Version = string.IsNullOrEmpty(v.ProductVersion) ? v.FileVersion : v.ProductVersion,
                Created = File.GetCreationTime(path),
                Modified = File.GetLastWriteTime(path)
            };
        }

        void ReadAdobeFiles()
        {
            var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetEnvironmentVariable("ProgramFiles") ?? "";
            string adobe = Path.Combine(pf, "Adobe");
            if (Directory.Exists(adobe))
            {
                foreach (string dir in Directory.GetDirectories(adobe, "Adobe Photoshop*"))
                {
                    string exe = Path.Combine(dir, "Photoshop.exe");
                    if (File.Exists(exe)) exes.Add(exe);
                }
            }
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Adobe\Photoshop"))
            {
                if (key != null)
                {
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        using (var v = key.OpenSubKey(sub))
                        {
                            string app = v == null ? null : v.GetValue("ApplicationPath") as string;
                            if (string.IsNullOrEmpty(app)) continue;
                            string exe = Path.Combine(app, "Photoshop.exe");
                            if (File.Exists(exe)) exes.Add(exe);
                        }
                    }
                }
            }
            foreach (string exe in exes) Photoshops.Add(Stamp(exe));

            var roots = new List<string>();
            foreach (string env in new[] { "CommonProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)" })
            {
                string v = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(v)) roots.Add(Path.Combine(v, "Adobe", "Plug-Ins"));
            }
            roots.AddRange(exes.Select(e => Path.Combine(Path.GetDirectoryName(e), "Plug-ins")));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string f in Util.WalkFiles(root, 6))
                {
                    string n = Path.GetFileName(f);
                    if (n.StartsWith("Camera Raw", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".8bi", StringComparison.OrdinalIgnoreCase) && seen.Add(f))
                        AcrPlugins.Add(Stamp(f));
                }
            }

            DateTime from = DateTime.Now.AddDays(-ChangeWindowDays);
            foreach (var p in Photoshops.Where(x => x.Effective >= from))
                Changes.Add(new Change { When = p.Effective, Kind = "Photoshop", What = "Photoshop.exe версии " + p.Version });
            foreach (var p in AcrPlugins.Where(x => x.Effective >= from))
                Changes.Add(new Change { When = p.Effective, Kind = "Camera Raw", What = "Плагин Camera Raw версии " + p.Version });
        }

        static List<EventRecord> Query(string log, string xpath, int max)
        {
            var result = new List<EventRecord>();
            var query = new EventLogQuery(log, PathType.LogName, xpath);
            query.ReverseDirection = true;
            using (var reader = new EventLogReader(query))
            {
                EventRecord e;
                while (result.Count < max && (e = reader.ReadEvent()) != null) result.Add(e);
            }
            return result;
        }

        static string Prop(EventRecord e, int i)
        {
            return e.Properties.Count > i ? Convert.ToString(e.Properties[i].Value) : "";
        }

        void ReadPhotoshopEvents()
        {
            const string xp = "*[System[Provider[@Name='Application Hang' or @Name='Application Error'] and TimeCreated[timediff(@SystemTime) <= 5184000000]]]";
            foreach (var e in Query("Application", xp, 2000))
            {
                using (e)
                {
                    if (Prop(e, 0).IndexOf("Photoshop", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string text = e.Id == 1002
                        ? "Зависание (Application Hang), версия " + Prop(e, 1)
                        : "Сбой, версия " + Prop(e, 1) + ", модуль " + Prop(e, 3) + ", код " + Prop(e, 6);
                    PhotoshopEvents.Add(new LogEvent { When = e.TimeCreated ?? DateTime.MinValue, Source = e.ProviderName, Id = e.Id, Text = text });
                }
            }
            PhotoshopEvents = PhotoshopEvents.OrderByDescending(x => x.When).ToList();
        }

        void ReadSystemEvents()
        {
            const string xp = "*[System[Provider[@Name='nvlddmkm' or @Name='Display' or @Name='Microsoft-Windows-Kernel-Power' or @Name='Microsoft-Windows-WHEA-Logger' or @Name='disk'] " +
                "and (Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]";
            foreach (var e in Query("System", xp, 500))
            {
                using (e)
                {
                    string p = e.ProviderName;
                    bool keep = p == "nvlddmkm" || p == "Microsoft-Windows-WHEA-Logger" ||
                                (p == "Display" && e.Id == 4101) ||
                                (p == "Microsoft-Windows-Kernel-Power" && e.Id == 41) ||
                                (p == "disk" && (e.Id == 7 || e.Id == 11 || e.Id == 51 || e.Id == 153));
                    if (!keep) continue;
                    SystemEvents.Add(new LogEvent { When = e.TimeCreated ?? DateTime.MinValue, Source = p, Id = e.Id, Text = Util.Clip(Util.Oneline(Util.Try(() => e.FormatDescription(), "")), 200) });
                }
            }
            SystemEvents = SystemEvents.OrderByDescending(x => x.When).ToList();
        }

        void ReadHotfixes()
        {
            DateTime from = DateTime.Now.AddDays(-ChangeWindowDays);
            using (var q = new ManagementObjectSearcher("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering"))
            {
                foreach (ManagementObject o in q.Get())
                {
                    DateTime when;
                    if (DateTime.TryParse(Convert.ToString(o["InstalledOn"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out when) && when >= from)
                        Changes.Add(new Change { When = when, Kind = "Обновление Windows", What = Convert.ToString(o["HotFixID"]) });
                }
            }
        }

        void ReadInstalled()
        {
            DateTime from = DateTime.Now.AddDays(-ChangeWindowDays);
            ReadUninstall(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", from);
            ReadUninstall(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", from);
            ReadUninstall(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", from);
        }

        void ReadUninstall(RegistryKey hive, string path, DateTime from)
        {
            using (var key = hive.OpenSubKey(path))
            {
                if (key == null) return;
                foreach (string sub in key.GetSubKeyNames())
                {
                    using (var v = key.OpenSubKey(sub))
                    {
                        if (v == null) continue;
                        string name = v.GetValue("DisplayName") as string;
                        string date = v.GetValue("InstallDate") as string;
                        DateTime when;
                        if (string.IsNullOrEmpty(name) || !DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out when) || when < from)
                            continue;
                        Changes.Add(new Change { When = when, Kind = "Установлена программа", What = name + " " + v.GetValue("DisplayVersion") });
                    }
                }
            }
        }

        void ReadAcr()
        {
            string root = Fixes.AcrRoot;
            Acr.RootExists = Directory.Exists(root);
            string gpu = Path.Combine(root, "GPU");
            foreach (string f in Util.WalkFiles(gpu, 3))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith(Backups.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                Acr.GpuFiles.Add(string.Format("{0} ({1} байт, {2:yyyy-MM-dd HH:mm})", f, new FileInfo(f).Length, File.GetLastWriteTime(f)));
                if (name.StartsWith("TempDisableGPU", StringComparison.OrdinalIgnoreCase) && !Backups.CreatedByProgram(f)) Acr.TempDisable.Add(f);
                if (name.Equals("Camera Raw GPU Config.txt", StringComparison.OrdinalIgnoreCase) && Acr.GpuConfigText == null)
                    Acr.GpuConfigText = Util.Clip(Util.TailLines(f, 200), 6000);
            }
            if (Directory.Exists(gpu))
                Acr.GpuAppDirs.AddRange(Directory.GetDirectories(gpu).Select(Path.GetFileName));
            Acr.DisableInferenceFile = Util.WalkFiles(root, 3).Any(f => Path.GetFileName(f).Equals("DisableGPUInference.txt", StringComparison.OrdinalIgnoreCase));

            foreach (string f in Util.WalkFiles(Fixes.AcrCacheDir, 3))
            {
                Acr.CacheFiles++;
                Acr.CacheBytes += Util.Try(() => new FileInfo(f).Length, 0L);
            }

            foreach (string zoo in Fixes.ModelZooDirs())
            {
                var files = Util.WalkFiles(zoo, 6);
                Acr.ModelZoo.Add(string.Format("{0}: файлов {1}, размер {2}", zoo, files.Count, Util.Gb(files.Sum(f => Util.Try(() => new FileInfo(f).Length, 0L)))));
                foreach (string f in files.OrderByDescending(x => Util.Try(() => File.GetLastWriteTime(x), DateTime.MinValue)).Take(40))
                    Acr.ModelZoo.Add(string.Format("  {0:yyyy-MM-dd HH:mm}  {1} байт  {2}", File.GetLastWriteTime(f), new FileInfo(f).Length, f.Substring(zoo.Length).TrimStart('\\')));
            }

            if (Acr.RootExists)
            {
                foreach (string dir in Fixes.AcrUserDataDirs())
                {
                    var files = Util.WalkFiles(dir, 8);
                    int empty = files.Count(f => Util.Try(() => new FileInfo(f).Length == 0, false));
                    DateTime newest = files.Count == 0 ? DateTime.MinValue : files.Max(f => Util.Try(() => File.GetLastWriteTime(f), DateTime.MinValue));
                    Acr.UserData.Add(string.Format("{0}: файлов {1}, пустых {2}, самый новый {3}", Path.GetFileName(dir), files.Count, empty,
                        files.Count == 0 ? "-" : newest.ToString("yyyy-MM-dd HH:mm")));
                }
                string logs = Path.Combine(root, "logs");
                var logFiles = Util.WalkFiles(logs, 2)
                    .Where(x => Path.GetFileName(x).IndexOf("Lightroom", StringComparison.OrdinalIgnoreCase) < 0)
                    .OrderByDescending(x => Util.Try(() => File.GetLastWriteTime(x), DateTime.MinValue))
                    .Take(3);
                foreach (string f in logFiles)
                {
                    var digest = LogDigest.Analyze(LogDigest.ReadLastLines(f, 8 * 1024 * 1024));
                    Acr.LogTails.Add("--- " + f + "\n" + string.Join("\n", digest.Lines.ToArray()));
                    foreach (string line in digest.ModelTrouble)
                        if (!Acr.ModelLogLines.Contains(line)) Acr.ModelLogLines.Add(line);
                }
            }
        }
    }
}
