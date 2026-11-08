using System;
using System.Collections.Generic;
using System.Linq;

namespace FixPhotoshop
{
    // Turns collected facts into findings. Each finding quotes the evidence it rests on,
    // and confidence says how directly that evidence points at the cause.
    static class Diagnosis
    {
        public static List<Finding> Baseline(Snapshot s)
        {
            var r = new List<Finding>();

            if (s.Acr.TempDisable.Count > 0)
                r.Add(new Finding
                {
                    Confidence = 3,
                    Id = "tempdisable",
                    Title = "Camera Raw сам записал сбой инициализации видеокарты",
                    Evidence = "Найдены файлы: " + string.Join("; ", s.Acr.TempDisable.ToArray()),
                    Advice = "По описанию Adobe такой файл остаётся после фатальной ошибки при инициализации GPU. Пока он существует, Camera Raw отключает ускорение. Если после удаления файл появляется снова, инициализация GPU продолжает падать.",
                    Fixes = new[] { "gpu-reset" }
                });

            var tdr = s.SystemEvents.Where(e => e.Source == "nvlddmkm" || (e.Source == "Display" && e.Id == 4101)).ToList();
            if (tdr.Count > 0)
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "gpu-driver-errors",
                    Title = "Windows записывала ошибки видеодрайвера",
                    Evidence = string.Format("{0} событий за 30 дней, последнее {1:yyyy-MM-dd HH:mm}: {2} {3}", tdr.Count, tdr[0].When, tdr[0].Source, tdr[0].Id),
                    Advice = "Событие 4101 означает, что драйвер перестал отвечать и был перезапущен Windows. Это подтверждает проблему на уровне видеокарты или драйвера, но не привязывает её к Camera Raw.",
                    Fixes = new[] { "gpu-reset", "gpu-off" }
                });

            var whea = s.SystemEvents.Where(e => e.Source == "Microsoft-Windows-WHEA-Logger").ToList();
            if (whea.Count > 0)
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "whea",
                    Title = "Windows фиксировала аппаратные ошибки (WHEA)",
                    Evidence = string.Format("{0} событий за 30 дней, последнее {1:yyyy-MM-dd HH:mm}", whea.Count, whea[0].When),
                    Advice = "Это сообщения о сбоях процессора, памяти или шины. Если есть разгон, верните стандартные частоты и проверьте память. Программные исправления такое не устраняют."
                });

            var disk = s.SystemEvents.Where(e => e.Source == "disk").ToList();
            if (disk.Count > 0)
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "disk-errors",
                    Title = "Windows фиксировала ошибки диска",
                    Evidence = string.Format("{0} событий за 30 дней, последнее {1:yyyy-MM-dd HH:mm}, код {2}", disk.Count, disk[0].When, disk[0].Id),
                    Advice = "Зависания при чтении файлов возможны из-за сбоящего диска. Проверьте его состояние (SMART)."
                });

            if (s.SystemFreeBytes >= 0 && s.SystemFreeBytes < 10L * 1073741824L)
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "low-disk",
                    Title = "Мало места на системном диске",
                    Evidence = "Свободно " + Util.Gb(s.SystemFreeBytes),
                    Advice = "Photoshop и Camera Raw используют диск для временных файлов и кэша. Освободите хотя бы 20 ГБ.",
                    Fixes = new[] { "acr-cache" }
                });

            if (s.RealGpus.Count > 1)
                r.Add(new Finding
                {
                    Confidence = 1,
                    Id = "multi-gpu",
                    Title = "В системе несколько видеокарт",
                    Evidence = string.Join("; ", s.RealGpus.ToArray()),
                    Advice = "Эксперты сообщества Adobe советуют на время проверки отключить встроенную видеокарту в Диспетчере устройств: приложения Adobe плохо работают с несколькими GPU."
                });

            if (s.Onset.HasValue)
            {
                DateTime onset = s.Onset.Value;
                DateTime from = onset.AddDays(-3), to = onset.AddDays(1);
                foreach (var c in s.Changes.Where(x => x.When >= from && x.When <= to).OrderByDescending(x => x.When))
                {
                    bool adobe = c.Kind == "Camera Raw" || c.Kind == "Photoshop";
                    r.Add(new Finding
                    {
                        Confidence = adobe ? 2 : 1,
                        Id = "timeline-" + c.Kind,
                        Title = "Совпадение по времени: " + c.Kind,
                        Evidence = string.Format("{0} ({1:yyyy-MM-dd HH:mm}); первое записанное зависание или сбой Photoshop за {2} дн.: {3:yyyy-MM-dd HH:mm}", c.What, c.When, Snapshot.ChangeWindowDays, onset),
                        Advice = adobe
                            ? "Обновление вышло незадолго до начала проблемы. Это совпадение по времени, а не доказательство. Откат покажет, в нём ли дело."
                            : "Изменение случилось незадолго до начала проблемы. Это только совпадение по времени.",
                        Fixes = c.Kind == "Camera Raw" || c.Kind == "Photoshop" ? new[] { "rollback" } : new string[0]
                    });
                }
            }
            return r;
        }

        public static List<Finding> Hang(HangCapture c)
        {
            var r = new List<Finding>();
            if (!string.IsNullOrEmpty(c.Error) && c.Threads.Count == 0)
            {
                r.Add(new Finding { Confidence = 1, Id = "no-access", Title = "Не удалось прочитать зависший процесс", Evidence = c.Error, Advice = "Запустите FixPhotoshop от имени администратора и повторите." });
                return r;
            }

            var ui = c.UiThreads.ToList();

            var stuck = ui.Where(t => t.KernelStuck).ToList();
            if (stuck.Count > 0)
                r.Add(new Finding
                {
                    Confidence = 3,
                    Id = "kernel-stuck",
                    Title = "Поток окна Photoshop застрял в режиме ядра",
                    Evidence = "Windows не смогла получить состояние потока " + stuck[0].ThreadId + " за 6 секунд.",
                    Advice = "Поток ждёт ответа внутри драйвера устройства. Первыми стоит проверить видеодрайвер, затем антивирус и фильтры диска.",
                    Fixes = new[] { "gpu-reset", "gpu-off" }
                });

            foreach (var t in ui.Where(t => t.HasModule(Mods.IsGpuDriver)).Take(1))
            {
                string mod = t.Frames.First(f => f.Module != null && Mods.IsGpuDriver(f.Module)).Module;
                r.Add(new Finding
                {
                    Confidence = 3,
                    Id = "gpu-stack",
                    Title = "Зависший поток находится внутри видеодрайвера (" + mod + ")",
                    Evidence = "Стек потока " + t.ThreadId + ": " + Top(t),
                    Advice = "Зависание происходит при обращении к видеокарте. Проверим Camera Raw без видеокарты. Если поможет, причина подтверждена; постоянным решением тогда будет драйвер NVIDIA Studio или другая версия Camera Raw.",
                    Fixes = new[] { "gpu-reset", "gpu-off" }
                });
            }

            if (!r.Any(x => x.Id == "gpu-stack"))
                foreach (var t in ui.Where(t => t.HasModule(Mods.IsGfxApi)).Take(1))
                    r.Add(new Finding
                    {
                        Confidence = 2,
                        Id = "gfx-api-stack",
                        Title = "Зависший поток ждёт графическую подсистему (DirectX/OpenGL/OpenCL)",
                        Evidence = "Стек потока " + t.ThreadId + ": " + Top(t),
                        Advice = "Поток остановился в графическом API. Проверим Camera Raw без видеокарты.",
                        Fixes = new[] { "gpu-reset", "gpu-off" }
                    });

            foreach (var t in ui.Where(t => t.WaitCycle).Take(1))
                r.Add(new Finding
                {
                    Confidence = 3,
                    Id = "deadlock",
                    Title = "Взаимная блокировка: потоки Photoshop ждут друг друга",
                    Evidence = "Цепочка ожидания: " + t.WaitChain,
                    Advice = "Такой поток сам не отвиснет. Причину покажут модули в стеках участников цепочки (см. отчёт).",
                    Fixes = new[] { "acr-userdata", "plugins" }
                });

            foreach (var t in ui.Where(t => t.WaitProcesses.Count > 0).Take(1))
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "waits-process",
                    Title = "Photoshop ждёт другую программу: " + string.Join(", ", t.WaitProcesses.ToArray()),
                    Evidence = "Цепочка ожидания: " + t.WaitChain,
                    Advice = "Окно Photoshop заблокировано ответом указанного процесса. Попробуйте закрыть или перезапустить его."
                });

            foreach (var t in ui.Where(t => t.HasModule(Mods.IsNet)).Take(1))
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "network",
                    Title = "Зависший поток ждёт сеть",
                    Evidence = "Стек потока " + t.ThreadId + ": " + Top(t),
                    Advice = "Проверьте: отключите интернет на время запуска Camera Raw. Если зависание уйдёт, дело в сетевом подключении, VPN или файрволе."
                });

            foreach (var t in ui.Where(t => t.HasModule(Mods.IsPrint)).Take(1))
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "printer",
                    Title = "Зависший поток ждёт службу печати",
                    Evidence = "Стек потока " + t.ThreadId + ": " + Top(t),
                    Advice = "Сделайте принтером по умолчанию «Microsoft Print to PDF» и перезапустите службу «Диспетчер печати»."
                });

            // Plug-ins from different vendors can ship DLLs with the same file name.
            var thirdByName = c.Modules.Where(Mods.IsThirdParty)
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var t in ui)
            {
                var hit = t.Frames.FirstOrDefault(f => f.Module != null && thirdByName.ContainsKey(f.Module) && !f.Module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (hit == null) continue;
                r.Add(new Finding
                {
                    Confidence = 3,
                    Id = "third-party-stack",
                    Title = "В зависшем потоке сторонний модуль: " + hit.Module,
                    Evidence = thirdByName[hit.Module].FilePath + " (" + thirdByName[hit.Module].Company + ")",
                    Advice = "Сторонний модуль работает прямо внутри зависшего потока. Отключите или обновите программу, которой он принадлежит, и проверьте снова.",
                    Fixes = new[] { "overlays", "plugins" }
                });
                break;
            }

            var hooks = c.Modules.Where(m => Mods.Overlays.ContainsKey(m.Name)).ToList();
            if (hooks.Count > 0)
                r.Add(new Finding
                {
                    Confidence = 1,
                    Id = "overlay",
                    Title = "В Photoshop внедрён модуль оверлея: " + string.Join(", ", hooks.Select(m => Mods.Overlays[m.Name]).ToArray()),
                    Evidence = string.Join("; ", hooks.Select(m => m.FilePath).ToArray()),
                    Advice = "Оверлеи перехватывают вызовы графики внутри программы. Закройте их на время проверки.",
                    Fixes = new[] { "overlays" }
                });

            foreach (var t in ui.Where(t => t.CpuSeconds >= 2.4).Take(1))
                r.Add(new Finding
                {
                    Confidence = t.HasModule(Mods.IsAcr) ? 3 : 2,
                    Id = "spin",
                    Title = t.HasModule(Mods.IsAcr) ? "Camera Raw занял ядро процессора и не выходит из цикла" : "Поток окна занят на полную мощность и не отвечает",
                    Evidence = string.Format("Поток {0}: {1:0.0} с процессорного времени за 3 с. Стек: {2}", t.ThreadId, t.CpuSeconds, Top(t)),
                    Advice = "Это не ожидание, а непрерывные вычисления. Нередко виноваты повреждённые данные (пресеты, кэш) или ошибка в версии Camera Raw.",
                    Fixes = new[] { "acr-cache", "acr-userdata", "rollback" }
                });

            foreach (var t in ui.Where(t => t.Frames.Count > 0 && IsFileIo(t.Frames[0])).Take(1))
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "file-io",
                    Title = "Зависший поток ждёт операцию с файлом",
                    Evidence = "Верх стека: " + t.Frames[0],
                    Advice = "Проверьте папку кэша Camera Raw и пресеты. Если файлы лежат на сетевом или внешнем диске, проверьте его доступность.",
                    Fixes = new[] { "acr-cache", "acr-userdata" }
                });

            var workers = c.Threads.Where(t => !t.IsUiThread && t.HasModule(Mods.IsAcr) && t.HasModule(Mods.IsGpuAny)).ToList();
            if (workers.Count > 0 && !r.Any(x => x.Id == "gpu-stack" || x.Id == "gfx-api-stack"))
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "acr-gpu-worker",
                    Title = "Рабочий поток Camera Raw остановился на видеокарте",
                    Evidence = "Поток " + workers[0].ThreadId + ": " + Top(workers[0]),
                    Advice = "Окно ждёт рабочий поток Camera Raw, а тот находится в графическом коде.",
                    Fixes = new[] { "gpu-reset", "gpu-off" }
                });

            if (c.NvidiaSmi != null && c.NvidiaSmi.StartsWith("ТАЙМАУТ"))
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "smi-timeout",
                    Title = "Драйвер NVIDIA не ответил на запрос состояния",
                    Evidence = c.NvidiaSmi,
                    Advice = "Драйвер занят или завис в момент зависания Photoshop.",
                    Fixes = new[] { "gpu-reset", "gpu-off" }
                });

            if (ui.Any(t => t.HasModule(Mods.IsAi)))
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "ai",
                    Title = "Зависание произошло в коде ИИ-функций (ONNX/DirectML)",
                    Evidence = Top(ui.First(t => t.HasModule(Mods.IsAi))),
                    Advice = "На форуме Adobe описаны сбои GPU при ИИ-функциях Camera Raw (выбор объекта, адаптивные пресеты).",
                    Fixes = new[] { "gpu-off", "rollback" }
                });

            if (r.Count == 0 && ui.Any(t => t.HasModule(Mods.IsAcr)))
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "acr-only",
                    Title = "Зависание внутри Camera Raw, без признаков видеокарты и сети",
                    Evidence = Top(ui.First(t => t.HasModule(Mods.IsAcr))),
                    Advice = "Проверим данные Camera Raw (кэш, настройки, пресеты), затем версию плагина.",
                    Fixes = new[] { "acr-cache", "acr-userdata", "rollback" }
                });

            if (!c.AcrWindowOpen && !c.AcrModuleLoaded)
                r.Add(new Finding
                {
                    Confidence = 2,
                    Id = "before-acr",
                    Title = "Photoshop завис раньше, чем загрузился Camera Raw",
                    Evidence = "Окна Camera Raw нет, модуль Camera Raw.8bi не загружен.",
                    Advice = "Вероятно, дело не в Camera Raw, а в самом Photoshop: настройках или плагинах.",
                    Fixes = new[] { "ps-prefs", "plugins" }
                });

            if (c.StackCompared && !c.StackStable)
                r.Add(new Finding
                {
                    Confidence = 1,
                    Id = "stack-moving",
                    Title = "Поток окна не стоит на месте",
                    Evidence = "Между двумя снимками стек изменился.",
                    Advice = "Возможно, идёт долгая операция, а не мёртвое зависание. Подождите пару минут, прежде чем что-то менять."
                });

            return r.OrderByDescending(x => x.Confidence).ToList();
        }

        static bool IsFileIo(Frame f)
        {
            if (f.Symbol == null) return false;
            foreach (string s in new[] { "NtCreateFile", "NtOpenFile", "NtReadFile", "NtWriteFile", "NtQueryDirectoryFile", "NtQueryAttributesFile", "NtQueryFullAttributesFile", "NtFlushBuffersFile" })
                if (f.Symbol.StartsWith(s, StringComparison.Ordinal)) return true;
            return false;
        }

        public static string Top(ThreadStack t)
        {
            return t.Frames.Count == 0 ? "(нет данных)" : string.Join(" <- ", t.Frames.Take(6).Select(f => f.ToString()).ToArray());
        }

        public static string ConfidenceName(int c)
        {
            return c >= 3 ? "прямое свидетельство" : c == 2 ? "сильный признак" : "стоит проверить";
        }
    }
}
