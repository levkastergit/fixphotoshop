using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FixPhotoshop
{
    sealed class Fix
    {
        public string Id;
        public string Title;
        public string Explain;
        public bool Manual;
        public string Instructions;
        // Experiments that cost something (no GPU, no presets) are put back when they turn out not to help.
        public bool RevertIfNoHelp;
        public Func<List<string>> Apply;
    }

    // Every change is a rename next to the original, recorded in a manifest, so all of it can be undone.
    static class Backups
    {
        public const string Prefix = "fixphotoshop-bak-";
        const string ReplacedPrefix = "fixphotoshop-replaced-";

        static string Manifest { get { return Path.Combine(Util.DataDir, "backups.tsv"); } }

        static string FreeName(string dir, string prefix, string name)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            for (int i = 0; ; i++)
            {
                string candidate = Path.Combine(dir, prefix + stamp + (i == 0 ? "" : "-" + i) + "-" + name);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        static void MovePath(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }

        static void Record(string fixId, string kind, string path, string backup)
        {
            File.AppendAllText(Manifest, string.Join("\t", new[] { fixId, kind, path, backup }) + Environment.NewLine);
        }

        public static string SetAside(string fixId, string path)
        {
            string backup = FreeName(Path.GetDirectoryName(path), Prefix, Path.GetFileName(path));
            MovePath(path, backup);
            Record(fixId, "rename", path, backup);
            return backup;
        }

        public static void Created(string fixId, string path)
        {
            Record(fixId, "created", path, "");
        }

        static List<string[]> Entries()
        {
            if (!File.Exists(Manifest)) return new List<string[]>();
            return File.ReadAllLines(Manifest).Where(l => l.Trim().Length > 0).Select(l => l.Split('\t')).Where(p => p.Length == 4).ToList();
        }

        public static int Count { get { return Entries().Count; } }

        public static List<string> Undo(string fixId)
        {
            var log = new List<string>();
            var all = Entries();
            var keep = new List<string[]>();
            for (int i = all.Count - 1; i >= 0; i--)
            {
                var e = all[i];
                if (fixId != null && e[0] != fixId)
                {
                    keep.Insert(0, e);
                    continue;
                }
                try
                {
                    UndoEntry(e, log);
                }
                catch (Exception ex)
                {
                    log.Add("Не удалось вернуть " + e[2] + ": " + ex.Message);
                    keep.Insert(0, e);
                }
            }
            File.WriteAllLines(Manifest, keep.Select(e => string.Join("\t", e)).ToArray());
            return log;
        }

        static void UndoEntry(string[] e, List<string> log)
        {
            string path = e[2];
            if (e[1] == "created")
            {
                if (File.Exists(path)) File.Delete(path);
                log.Add("Удалено созданное программой: " + path);
                return;
            }
            string backup = e[3];
            if (!File.Exists(backup) && !Directory.Exists(backup))
            {
                log.Add("Резервная копия не найдена, пропускаю: " + backup);
                return;
            }
            // Whatever Adobe recreated meanwhile is kept aside too, so nothing is destroyed by undoing.
            if (File.Exists(path) || Directory.Exists(path))
                MovePath(path, FreeName(Path.GetDirectoryName(path), ReplacedPrefix, Path.GetFileName(path)));
            MovePath(backup, path);
            log.Add("Возвращено: " + path);
        }
    }

    static class Fixes
    {
        public static string AcrRoot { get { return Path.Combine(Util.AppData, "Adobe", "CameraRaw"); } }
        public static string AcrCacheDir { get { return Path.Combine(Util.LocalAppData, "Adobe", "CameraRaw", "Cache"); } }

        static readonly HashSet<string> NotUserData = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GPU", "logs", "Cache" };

        public static List<string> AcrUserDataDirs()
        {
            if (!Directory.Exists(AcrRoot)) return new List<string>();
            return Directory.GetDirectories(AcrRoot)
                .Where(d =>
                {
                    string n = Path.GetFileName(d);
                    return !NotUserData.Contains(n) && !n.StartsWith("fixphotoshop-", StringComparison.OrdinalIgnoreCase);
                }).ToList();
        }

        static List<string> GpuStateFiles()
        {
            return Util.WalkFiles(Path.Combine(AcrRoot, "GPU"), 3).Where(f =>
            {
                string n = Path.GetFileName(f);
                return n.Equals("Camera Raw GPU Config.txt", StringComparison.OrdinalIgnoreCase) || n.StartsWith("TempDisableGPU", StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        static List<string> PhotoshopGpuDirs()
        {
            string gpu = Path.Combine(AcrRoot, "GPU");
            if (!Directory.Exists(gpu)) return new List<string>();
            return Directory.GetDirectories(gpu).Where(d =>
            {
                string n = Path.GetFileName(d);
                return n.IndexOf("Photoshop", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       n.IndexOf("Lightroom", StringComparison.OrdinalIgnoreCase) < 0 &&
                       !n.StartsWith("fixphotoshop-", StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        // The settings folder of the Photoshop in use, e.g. %APPDATA%\Adobe\Adobe Photoshop 2026\Adobe Photoshop 2026 Settings.
        static string PhotoshopSettingsDir(HangCapture capture)
        {
            string adobe = Path.Combine(Util.AppData, "Adobe");
            if (!Directory.Exists(adobe)) return null;
            var found = new List<string>();
            foreach (string dir in Directory.GetDirectories(adobe, "Adobe Photoshop*"))
            {
                string settings = Path.Combine(dir, Path.GetFileName(dir) + " Settings");
                if (Directory.Exists(settings)) found.Add(settings);
            }
            if (found.Count == 0) return null;
            if (capture != null && capture.PhotoshopPath != null)
            {
                string product = Path.GetFileName(Path.GetDirectoryName(capture.PhotoshopPath));
                string match = found.FirstOrDefault(f => Path.GetFileName(f).Equals(product + " Settings", StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return found.OrderByDescending(f => Directory.GetLastWriteTime(f)).First();
        }

        static List<string> RunningOverlayProcesses(HangCapture capture)
        {
            var names = new List<string>();
            if (capture == null) return names;
            foreach (var m in capture.Modules.Where(x => Mods.OverlayProcesses.ContainsKey(x.Name)))
                foreach (string n in Mods.OverlayProcesses[m.Name])
                    if (Process.GetProcessesByName(n).Length > 0 && !names.Contains(n)) names.Add(n);
            return names;
        }

        public static List<Fix> Build(HangCapture capture, IEnumerable<Finding> findings, ISet<string> done)
        {
            var list = new List<Fix>();

            var gpuFiles = GpuStateFiles();
            if (gpuFiles.Count > 0)
                list.Add(new Fix
                {
                    Id = "gpu-reset",
                    Title = "Сбросить результаты проверки видеокарты в Camera Raw",
                    Explain = "Файлы «Camera Raw GPU Config.txt» и TempDisableGPU будут убраны в сторону, и Camera Raw заново проверит видеокарту. Это шаг из FAQ Adobe.",
                    Apply = () => SetAsideAll("gpu-reset", GpuStateFiles())
                });

            var psGpuDirs = PhotoshopGpuDirs();
            if (psGpuDirs.Count > 0)
                list.Add(new Fix
                {
                    Id = "gpu-off",
                    Title = "Проверить Camera Raw без видеокарты",
                    Explain = "Будет создан файл TempDisableGPU3. По описанию Adobe, пока такой файл есть, Camera Raw отключает ускорение GPU. Если зависание исчезнет, дело в видеокарте или драйвере.",
                    RevertIfNoHelp = true,
                    Apply = () =>
                    {
                        var log = new List<string>();
                        foreach (string dir in PhotoshopGpuDirs())
                        {
                            string marker = Path.Combine(dir, "TempDisableGPU3");
                            if (File.Exists(marker)) continue;
                            File.WriteAllText(marker, "");
                            Backups.Created("gpu-off", marker);
                            log.Add("Создан " + marker);
                        }
                        return log;
                    }
                });
            else
                list.Add(new Fix
                {
                    Id = "gpu-off-manual",
                    Title = "Отключить видеокарту в настройках Camera Raw (вручную)",
                    Explain = "Программа не нашла папку GPU для Photoshop, поэтому этот шаг нужно выполнить вручную.",
                    Manual = true,
                    Instructions = "1. Запустите Photoshop.\n2. Откройте Редактирование → Настройки → Camera Raw (Edit → Preferences → Camera Raw).\n3. На вкладке «Производительность» в поле «Использовать графический процессор» выберите «Выкл».\n4. Нажмите ОК и откройте Camera Raw как обычно."
                });

            var overlays = RunningOverlayProcesses(capture);
            if (overlays.Count > 0)
                list.Add(new Fix
                {
                    Id = "overlays",
                    Title = "Закрыть оверлеи: " + string.Join(", ", overlays.ToArray()),
                    Explain = "Модули этих программ найдены внутри Photoshop. Программы будут закрыты; настройки не меняются, запустить их можно в любой момент.",
                    Apply = () =>
                    {
                        var log = new List<string>();
                        foreach (string n in RunningOverlayProcesses(capture))
                            foreach (var p in Process.GetProcessesByName(n))
                            {
                                try { p.Kill(); log.Add("Закрыт " + n); }
                                catch (Exception ex) { log.Add("Не удалось закрыть " + n + " (" + ex.Message + "). Закройте его вручную."); }
                            }
                        return log;
                    }
                });

            if (Directory.Exists(AcrCacheDir))
                list.Add(new Fix
                {
                    Id = "acr-cache",
                    Title = "Убрать кэш Camera Raw",
                    Explain = "Папка кэша будет переименована, Camera Raw создаст новую. Фотографии и правки не затрагиваются.",
                    Apply = () => SetAsideAll("acr-cache", new List<string> { AcrCacheDir })
                });

            list.Add(new Fix
            {
                Id = "acr-prefs",
                Title = "Сбросить настройки Camera Raw (вручную)",
                Explain = "Выполняется сочетанием клавиш внутри Photoshop.",
                Manual = true,
                Instructions = "1. Запустите Photoshop.\n2. Зажмите Ctrl+Alt и, не отпуская, выберите Редактирование → Настройки → Camera Raw (Edit → Preferences → Camera Raw).\n3. На вопрос о сбросе настроек ответьте «Да».\n4. Если вопроса нет, повторите, удерживая только Ctrl.\n5. Откройте Camera Raw как обычно."
            });

            if (AcrUserDataDirs().Count > 0)
                list.Add(new Fix
                {
                    Id = "acr-userdata",
                    Title = "Временно убрать пресеты и профили Camera Raw",
                    Explain = "Папки с пресетами и профилями будут переименованы. Если причина в них, вы сможете вернуть их по частям; если нет, программа вернёт всё сама.",
                    RevertIfNoHelp = true,
                    Apply = () => SetAsideAll("acr-userdata", AcrUserDataDirs())
                });

            string settings = PhotoshopSettingsDir(capture);
            if (settings != null)
                list.Add(new Fix
                {
                    Id = "ps-prefs",
                    Title = "Временно сбросить настройки Photoshop",
                    Explain = "Папка настроек будет переименована, Photoshop создаст новую. Если не поможет, программа вернёт старую.",
                    RevertIfNoHelp = true,
                    Apply = () => SetAsideAll("ps-prefs", new List<string> { settings })
                });

            list.Add(new Fix
            {
                Id = "plugins",
                Title = "Проверить без сторонних плагинов (вручную)",
                Explain = "Запуск Photoshop без необязательных и сторонних плагинов на один сеанс.",
                Manual = true,
                Instructions = "1. Закройте Photoshop.\n2. Удерживая Shift, запустите Photoshop.\n3. На вопрос «Пропустить загрузку дополнительных и сторонних плагинов?» ответьте «Да».\n4. Откройте Camera Raw как обычно.\nЕсли зависания нет, виноват один из плагинов: включайте их по одному."
            });

            list.Add(new Fix
            {
                Id = "rollback",
                Title = "Откатить Camera Raw на предыдущую версию (вручную)",
                Explain = "На форуме Adobe пользователи сообщали, что проблемы с GPU исчезали на более старых версиях Camera Raw.",
                Manual = true,
                Instructions = "1. Откройте страницу Adobe: https://helpx.adobe.com/camera-raw/kb/camera-raw-plug-in-installer.html\n2. Посмотрите, есть ли установщик предыдущей версии (набор версий там неполный) и совместим ли он с вашим Photoshop.\n3. Отключите автообновление Photoshop в Creative Cloud, иначе новая версия вернётся.\n4. Если установщика нет: Creative Cloud → Photoshop → «…» → Другие версии."
            });

            var boost = new Dictionary<string, int>();
            foreach (var f in findings)
                foreach (string id in f.Fixes)
                {
                    int c;
                    boost.TryGetValue(id, out c);
                    boost[id] = Math.Max(c, f.Confidence);
                }

            // Rollback stays last among equals: it is the most work and is only a timing correlation in most cases.
            return list
                .Where(f => !done.Contains(f.Id))
                .Select((f, i) => new { f, i })
                .OrderByDescending(x => x.f.Id == "rollback" ? 0 : (boost.ContainsKey(x.f.Id) ? boost[x.f.Id] : 0))
                .ThenBy(x => x.i)
                .Select(x => x.f)
                .ToList();
        }

        static List<string> SetAsideAll(string fixId, List<string> paths)
        {
            var log = new List<string>();
            foreach (string p in paths)
            {
                string backup = Backups.SetAside(fixId, p);
                log.Add("Убрано: " + p + " -> " + Path.GetFileName(backup));
            }
            return log;
        }
    }
}
