using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FixPhotoshop
{
    static class Report
    {
        public static void Write(Engine e)
        {
            var b = new StringBuilder();
            b.AppendLine("ОТЧЁТ FixPhotoshop");
            b.AppendLine("Создан: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            b.AppendLine();

            var snap = e.Snap;
            var cap = e.LastCapture;

            Section(b, "ГЛАВНОЕ");
            if (e.HangFindings.Count > 0) WriteFinding(b, e.HangFindings[0]);
            else if (cap == null) b.AppendLine("Зависание ещё не зафиксировано.");
            else b.AppendLine("Снимок зависания сделан, однозначной причины по нему не видно.");

            Section(b, "ВЫПОЛНЕННЫЕ ШАГИ");
            if (e.Attempts.Count == 0) b.AppendLine("Шаги не применялись.");
            foreach (var a in e.Attempts)
            {
                b.AppendLine(string.Format("{0:HH:mm:ss}  {1} -> {2}", a.When, a.Fix.Title, a.Outcome));
                foreach (string l in a.Log) b.AppendLine("    " + l);
            }

            Section(b, "ВСЕ ВЫВОДЫ ПО ЗАВИСАНИЮ");
            if (e.HangFindings.Count == 0) b.AppendLine("Нет.");
            foreach (var f in e.HangFindings) WriteFinding(b, f);

            Section(b, "ВЫВОДЫ ПО СИСТЕМЕ");
            if (e.BaselineFindings.Count == 0) b.AppendLine("Нет.");
            foreach (var f in e.BaselineFindings) WriteFinding(b, f);

            if (cap != null) WriteCapture(b, cap);

            Section(b, "ОКНА PHOTOSHOP ЗА СЕАНС");
            if (e.WindowHistory.Count == 0) b.AppendLine("Окон не было.");
            foreach (string w in e.WindowHistory.ToArray()) b.AppendLine(w);

            if (snap != null)
            {
                Section(b, "СИСТЕМА");
                b.AppendLine("ОС: " + snap.Os);
                b.AppendLine(string.Format("ОЗУ: {0:0.0} ГБ, свободно при старте {1:0.0} ГБ", snap.RamGb, snap.RamFreeGb));
                b.AppendLine("Аппаратное планирование GPU: " + snap.HwSchMode);
                foreach (string l in snap.GpuLines) b.AppendLine("Видеокарта: " + l);
                if (snap.NvidiaSmi != null) b.AppendLine("nvidia-smi: " + snap.NvidiaSmi);
                foreach (string l in snap.DiskLines) b.AppendLine("Диск: " + l);

                Section(b, "ADOBE");
                if (snap.Photoshops.Count == 0) b.AppendLine("Photoshop в стандартных папках не найден.");
                foreach (var p in snap.Photoshops) b.AppendLine(string.Format("{0} | версия {1} | создан {2:yyyy-MM-dd HH:mm} | изменён {3:yyyy-MM-dd HH:mm}", p.FilePath, p.Version, p.Created, p.Modified));
                if (snap.AcrPlugins.Count == 0) b.AppendLine("Camera Raw.8bi в стандартных папках не найден.");
                foreach (var p in snap.AcrPlugins) b.AppendLine(string.Format("{0} | версия {1} | создан {2:yyyy-MM-dd HH:mm} | изменён {3:yyyy-MM-dd HH:mm}", p.FilePath, p.Version, p.Created, p.Modified));

                Section(b, "ЧТО МЕНЯЛОСЬ В СИСТЕМЕ (ПОСЛЕДНИЕ " + Snapshot.ChangeWindowDays + " СУТОК)");
                b.AppendLine(snap.Onset.HasValue ? "Первое записанное зависание/сбой Photoshop за этот период: " + snap.Onset.Value.ToString("yyyy-MM-dd HH:mm") : "Зависаний и сбоев Photoshop за этот период в журнале нет (завершение через Диспетчер задач может не попадать в журнал).");
                if (snap.Changes.Count == 0) b.AppendLine("Изменений не найдено.");
                foreach (var c in snap.Changes) b.AppendLine(string.Format("{0:yyyy-MM-dd HH:mm}  {1}: {2}", c.When, c.Kind, c.What));

                Section(b, "ЖУРНАЛ WINDOWS: PHOTOSHOP (60 ДНЕЙ)");
                if (snap.PhotoshopEvents.Count == 0) b.AppendLine("Нет записей.");
                foreach (var ev in snap.PhotoshopEvents.Take(60)) b.AppendLine(string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}", ev.When, ev.Text));

                Section(b, "ЖУРНАЛ WINDOWS: СИСТЕМА (30 ДНЕЙ)");
                if (snap.SystemEvents.Count == 0) b.AppendLine("Нет записей об ошибках видеодрайвера, диска, WHEA и аварийных перезагрузках.");
                foreach (var ev in snap.SystemEvents.Take(60)) b.AppendLine(string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1} {2}  {3}", ev.When, ev.Source, ev.Id, ev.Text));

                Section(b, "CAMERA RAW");
                b.AppendLine("Папка настроек: " + Fixes.AcrRoot + (snap.Acr.RootExists ? "" : " (не найдена)"));
                b.AppendLine(string.Format("Кэш: {0}, файлов {1}, размер {2}", Fixes.AcrCacheDir, snap.Acr.CacheFiles, Util.Gb(snap.Acr.CacheBytes)));
                b.AppendLine("DisableGPUInference.txt: " + (snap.Acr.DisableInferenceFile ? "есть" : "нет"));
                b.AppendLine("Подпапки GPU: " + (snap.Acr.GpuAppDirs.Count == 0 ? "нет" : string.Join(", ", snap.Acr.GpuAppDirs.ToArray())));
                foreach (string l in snap.Acr.GpuFiles) b.AppendLine("GPU-файл: " + l);
                foreach (string l in snap.Acr.UserData) b.AppendLine("Данные: " + l);
                if (snap.Acr.GpuConfigText != null)
                {
                    b.AppendLine("Содержимое Camera Raw GPU Config.txt:");
                    b.AppendLine(snap.Acr.GpuConfigText);
                }
                foreach (string l in snap.Acr.LogTails) b.AppendLine(l);

                if (snap.Problems.Count > 0)
                {
                    Section(b, "ЧТО НЕ УДАЛОСЬ СОБРАТЬ");
                    foreach (string l in snap.Problems) b.AppendLine(l);
                }
            }

            File.WriteAllText(e.ReportPath, b.ToString(), new UTF8Encoding(true));
        }

        static void Section(StringBuilder b, string title)
        {
            b.AppendLine();
            b.AppendLine("=== " + title + " ===");
        }

        static void WriteFinding(StringBuilder b, Finding f)
        {
            b.AppendLine("* " + f.Title + " [" + Diagnosis.ConfidenceName(f.Confidence) + "]");
            if (!string.IsNullOrEmpty(f.Evidence)) b.AppendLine("  Основание: " + f.Evidence);
            if (!string.IsNullOrEmpty(f.Advice)) b.AppendLine("  Что делать: " + f.Advice);
        }

        static void WriteCapture(StringBuilder b, HangCapture c)
        {
            Section(b, "СНИМОК ЗАВИСАНИЯ");
            b.AppendLine("Время: " + c.When.ToString("yyyy-MM-dd HH:mm:ss") + (c.UserRequested ? " (по команде пользователя)" : ""));
            b.AppendLine("Photoshop: " + c.PhotoshopPath + " версия " + c.PhotoshopVersion);
            if (c.ErrorDialogTitle != null) b.AppendLine("Окно ошибки: «" + c.ErrorDialogTitle + "»");
            b.AppendLine("Окно Camera Raw открыто: " + (c.AcrWindowOpen ? "да" : "нет"));
            b.AppendLine("Модуль Camera Raw загружен: " + (c.AcrModuleLoaded ? c.AcrModulePath + ", версия " + c.AcrVersion : "нет"));
            b.AppendLine(string.Format("Загрузка процессора процессом: {0:0.0}%", c.ProcessCpuPercent));
            b.AppendLine(string.Format("Память процесса: {0} МБ, свободно ОЗУ: {1:0.0} ГБ", c.WorkingSetMb, c.RamFreeGb));
            if (c.StackCompared) b.AppendLine("Стек окна между снимками: " + (c.StackStable ? "не менялся" : "менялся"));
            if (c.NvidiaSmi != null) b.AppendLine("nvidia-smi в момент зависания: " + c.NvidiaSmi);
            if (!string.IsNullOrEmpty(c.Error)) b.AppendLine("Ограничения снимка: " + c.Error);

            b.AppendLine();
            b.AppendLine("Окна:");
            foreach (var w in c.Windows) b.AppendLine(string.Format("  [{0}] поток {1} «{2}» ({3})", w.Hung ? "ЗАВИС" : "ок", w.ThreadId, w.Title, w.ClassName));

            b.AppendLine();
            b.AppendLine("Потоки окон:");
            foreach (var t in c.UiThreads) WriteThread(b, t);

            b.AppendLine();
            b.AppendLine("Самые активные прочие потоки:");
            foreach (var t in c.Threads.Where(x => !x.IsUiThread).OrderByDescending(x => x.CpuSeconds).Take(5)) WriteThread(b, t);

            b.AppendLine();
            b.AppendLine("Потоки Camera Raw:");
            foreach (var t in c.Threads.Where(x => !x.IsUiThread && x.HasModule(Mods.IsAcr)).Take(8)) WriteThread(b, t);

            var third = c.Modules.Where(Mods.IsThirdParty).ToList();
            b.AppendLine();
            b.AppendLine("Сторонние модули в Photoshop (" + third.Count + "):");
            foreach (var m in third) b.AppendLine("  " + m.FilePath + " | " + m.Company + " | " + m.Version);

            b.AppendLine();
            b.AppendLine("Всего потоков: " + c.Threads.Count + ", модулей: " + c.Modules.Count);
        }

        static void WriteThread(StringBuilder b, ThreadStack t)
        {
            b.AppendLine(string.Format("  Поток {0}, процессор {1:0.00} с за 3 с{2}", t.ThreadId, t.CpuSeconds, t.KernelStuck ? ", ЗАСТРЯЛ В ЯДРЕ" : ""));
            if (!string.IsNullOrEmpty(t.Error)) b.AppendLine("    Ошибка: " + t.Error);
            if (!string.IsNullOrEmpty(t.WaitChain)) b.AppendLine("    Цепочка ожидания: " + t.WaitChain + (t.WaitCycle ? " [ЦИКЛ]" : ""));
            foreach (var f in t.Frames.Take(40)) b.AppendLine("    " + f);
        }
    }
}
