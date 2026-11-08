using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FixPhotoshop
{
    sealed class Attempt
    {
        public Fix Fix;
        public DateTime When;
        public string Outcome = "ожидает проверки";
        public List<string> Log = new List<string>();
    }

    // Watches Photoshop, captures a hang, diagnoses it and walks through the fixes one at a time.
    sealed class Engine
    {
        const int HangThresholdSec = 20;
        const int AcrOkSec = 30;

        public event Action<string, string, Level> StatusChanged = delegate { };
        public event Action<string> Logged = delegate { };
        public event Action StateChanged = delegate { };

        readonly object sync = new object();
        volatile bool stop;
        volatile bool captureRequested;
        volatile bool applying;
        Attempt failedByCapture;

        public Snapshot Snap { get; private set; }
        public HangCapture LastCapture { get; private set; }
        public List<Finding> BaselineFindings { get; private set; }
        public List<Finding> HangFindings { get; private set; }
        public List<string> Notes { get; private set; }
        public List<Attempt> Attempts { get; private set; }
        public List<string> WindowHistory { get; private set; }
        public string ReportPath { get; private set; }
        public bool CaptureDone { get; private set; }
        public bool PhotoshopRunning { get; private set; }
        public bool Solved { get; private set; }

        string HeartbeatPath { get { return Path.Combine(Util.DataDir, "heartbeat.txt"); } }

        public Engine(string reportPath)
        {
            BaselineFindings = new List<Finding>();
            HangFindings = new List<Finding>();
            Notes = new List<string>();
            Attempts = new List<Attempt>();
            WindowHistory = new List<string>();
            ReportPath = reportPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "FixPhotoshop-report-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
        }

        public void Stop()
        {
            stop = true;
            Util.Try(() => { File.Delete(HeartbeatPath); return true; }, false);
        }

        public void RequestCapture() { captureRequested = true; }

        void Status(string title, string hint, Level level) { StatusChanged(title, hint, level); }

        void Log(string text)
        {
            Logged(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
        }

        void SaveReport()
        {
            try { Report.Write(this); }
            catch (Exception ex) { Log("Не удалось сохранить отчёт: " + ex.Message); }
        }

        public Fix NextFix
        {
            get
            {
                return Util.Try(() =>
                {
                    var done = new HashSet<string>(Attempts.Select(a => a.Fix.Id));
                    return Fixes.Build(LastCapture, BaselineFindings.Concat(HangFindings), done).FirstOrDefault();
                }, null);
            }
        }

        static Process FindPhotoshop()
        {
            return Process.GetProcessesByName("Photoshop").FirstOrDefault();
        }

        void CheckPreviousSession()
        {
            if (!File.Exists(HeartbeatPath)) return;
            string[] parts = Util.Try(() => File.ReadAllText(HeartbeatPath).Split('\t'), new string[0]);
            if (parts.Length < 2 || parts[1] != "1") return;
            DateTime last;
            if (!DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out last)) return;
            last = last.ToLocalTime();

            var reboot = Snap.SystemEvents.FirstOrDefault(e => e.Source == "Microsoft-Windows-Kernel-Power" && e.Id == 41 && e.When >= last);
            if (reboot != null)
            {
                BaselineFindings.Insert(0, new Finding
                {
                    Confidence = 3,
                    Id = "system-freeze",
                    Title = "В прошлый раз зависла вся система, а не только Photoshop",
                    Evidence = string.Format("Программа следила за Photoshop до {0:yyyy-MM-dd HH:mm:ss}, после чего Windows записала неожиданную перезагрузку (Kernel-Power 41, {1:yyyy-MM-dd HH:mm:ss}).", last, reboot.When),
                    Advice = "Когда замирает весь компьютер, причина обычно ниже уровня программ: видеодрайвер, перегрев, блок питания, память. Посмотрите раздел «Система» в отчёте.",
                    Fixes = new[] { "gpu-reset", "gpu-off" }
                });
                Log("Обнаружено: прошлый сеанс оборвался из-за зависания или перезагрузки всей системы.");
            }
        }

        readonly HashSet<string> seenWindows = new HashSet<string>();

        // Every window Photoshop opens goes into the report, so an unrecognised one can still be identified afterwards.
        void TrackWindows(List<WinInfo> windows)
        {
            foreach (var w in windows)
            {
                if (WindowHistory.Count >= 300 || !seenWindows.Add(w.Hwnd.ToInt64() + "|" + w.Title)) continue;
                WindowHistory.Add(string.Format("{0:HH:mm:ss}  «{1}» ({2})", DateTime.Now, w.Title, w.ClassName));
            }
        }

        void Beat(bool psRunning)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("o") + "\t" + (psRunning ? "1" : "0"));
                using (var fs = new FileStream(HeartbeatPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                    fs.Write(data, 0, data.Length);
            }
            catch (Exception) { }
        }

        // Runs until Stop(). With exitAfterCapture (headless mode) it returns after the first diagnosis.
        public void Run(int timeoutSec, bool exitAfterCapture)
        {
            Status("Проверяю систему…", "Это займёт до минуты. Photoshop уже можно запускать.", Level.Info);
            Log("Собираю сведения о системе, журналы Windows и настройки Camera Raw.");
            Snap = Snapshot.Collect();
            BaselineFindings = Diagnosis.Baseline(Snap);
            CheckPreviousSession();
            foreach (var f in BaselineFindings) Log("Найдено: " + f.Title + ". " + f.Evidence);
            if (BaselineFindings.Count == 0) Log("Явных проблем в системе и журналах не найдено.");
            SaveReport();
            StateChanged();

            var clock = Stopwatch.StartNew();
            DateTime? hungSince = null, acrSince = null, dialogSince = null;
            bool captured = false, acrAnnounced = false;
            int seenPid = 0;

            while (!stop)
            {
                if (timeoutSec > 0 && clock.Elapsed.TotalSeconds > timeoutSec) break;
                if (applying) { Thread.Sleep(300); continue; }

                Process ps = FindPhotoshop();
                Beat(ps != null);
                if (ps == null)
                {
                    if (seenPid != 0)
                    {
                        Log("Photoshop закрыт.");
                        seenPid = 0;
                        hungSince = acrSince = dialogSince = null;
                        captured = acrAnnounced = false;
                        PhotoshopRunning = false;
                        StateChanged();
                    }
                    if (!CaptureDone && !Solved) Status("Запустите Photoshop", WaitingHint(), Level.Info);
                    Thread.Sleep(1000);
                    continue;
                }

                if (ps.Id != seenPid)
                {
                    seenPid = ps.Id;
                    hungSince = acrSince = dialogSince = null;
                    captured = acrAnnounced = false;
                    PhotoshopRunning = true;
                    Log("Photoshop запущен (PID " + ps.Id + ").");
                    StateChanged();
                }

                var windows = Util.Try(() => StackCapture.ListWindows(ps.Id), new List<WinInfo>());
                bool acrOpen = windows.Any(w => StackCapture.IsAcrTitle(w.Title));
                var hung = windows.Where(w => w.Hung).ToList();
                DateTime now = DateTime.Now;
                TrackWindows(windows);

                WinInfo dialog = windows.FirstOrDefault(StackCapture.IsErrorDialog);
                if (dialog == null) dialogSince = null;
                else if (dialogSince == null)
                {
                    dialogSince = now;
                    Log("Photoshop показал окно ошибки: «" + dialog.Title + "».");
                }
                bool dialogStayed = dialogSince != null && (now - dialogSince.Value).TotalSeconds >= 1;

                if (hung.Count > 0)
                {
                    if (hungSince == null)
                    {
                        hungSince = now;
                        Log("Окно Photoshop перестало отвечать, отсчитываю время.");
                    }
                }
                else
                {
                    if (hungSince != null)
                    {
                        Log(string.Format("Photoshop снова отвечает (не отвечал {0:0} с).", (now - hungSince.Value).TotalSeconds));
                        hungSince = null;
                        if (captured) OnRecovered();
                    }
                    if (dialog == null) captured = false;
                }

                if (acrOpen)
                {
                    if (acrSince == null)
                    {
                        acrSince = now;
                        Log("Окно Camera Raw открылось.");
                    }
                }
                else
                {
                    acrSince = null;
                    acrAnnounced = false;
                }

                double hungSec = hungSince == null ? 0 : (now - hungSince.Value).TotalSeconds;
                bool manual = captureRequested;
                if (manual || (!captured && (hungSec >= HangThresholdSec || dialogStayed)))
                {
                    captureRequested = false;
                    captured = true;
                    try
                    {
                        DoCapture(ps, windows, manual, dialog);
                    }
                    catch (Exception ex)
                    {
                        // Typically Photoshop was closed while it was being read.
                        Log("Снимок не удался: " + ex.Message);
                        Status("Не удалось снять данные", "Если Photoshop всё ещё завис, нажмите «Зависло сейчас».", Level.Warn);
                    }
                    if (exitAfterCapture) break;
                }
                else if (hungSince != null && !captured)
                {
                    Status(string.Format("Photoshop не отвечает: {0:0} с", hungSec), "Если это зависание, через " + HangThresholdSec + " с программа сама снимет данные.", Level.Warn);
                }
                else if (!captured)
                {
                    if (acrOpen && hung.Count == 0 && (now - acrSince.Value).TotalSeconds >= AcrOkSec)
                    {
                        if (!acrAnnounced)
                        {
                            acrAnnounced = true;
                            OnAcrWorking();
                        }
                    }
                    else if (acrOpen && !CaptureDone && !acrAnnounced && !Solved)
                    {
                        Status("Camera Raw открывается", "Слежу. Работайте как обычно, чтобы воспроизвести зависание.", Level.Info);
                    }
                    else if (!acrOpen && !CaptureDone && !Solved)
                    {
                        Status("Photoshop запущен: откройте Camera Raw", "Откройте фото и вызовите Camera Raw так же, как обычно.", Level.Info);
                    }
                }

                Thread.Sleep(1000);
            }
            SaveReport();
        }

        void DoCapture(Process ps, List<WinInfo> windows, bool manual, WinInfo dialog)
        {
            Status("Снимаю данные…", "Не закрывайте Photoshop, это займёт до минуты.", Level.Warn);
            Log(manual ? "Снимаю данные по вашей команде." : dialog != null ? "Снимаю данные об ошибке." : "Зависание подтверждено, снимаю данные о потоках и модулях.");

            var cap = StackCapture.Capture(ps, windows, manual);
            if (dialog != null) cap.ErrorDialogTitle = dialog.Title;
            cap.NvidiaSmi = Snapshot.QueryNvidiaSmi();
            var findings = Diagnosis.Hang(cap);

            Snap.RefreshAcr();
            if (!BaselineFindings.Any(f => f.Id == "tempdisable"))
                BaselineFindings.AddRange(Diagnosis.Baseline(Snap).Where(f => f.Id == "tempdisable"));

            lock (sync)
            {
                LastCapture = cap;
                HangFindings = findings;
                CaptureDone = true;
                Solved = false;
                failedByCapture = null;
                var last = Attempts.LastOrDefault();
                if (last != null && last.Outcome == "ожидает проверки")
                {
                    last.Outcome = "не помогло";
                    failedByCapture = last;
                    Log("Шаг «" + last.Fix.Title + "» не помог: зависание повторилось.");
                }
            }

            if (findings.Count > 0)
            {
                Log("ВЫВОД: " + findings[0].Title + " [" + Diagnosis.ConfidenceName(findings[0].Confidence) + "]");
                Log("Основание: " + findings[0].Evidence);
                Log("Что это значит: " + findings[0].Advice);
                foreach (var f in findings.Skip(1).Take(4)) Log("Также: " + f.Title + " [" + Diagnosis.ConfidenceName(f.Confidence) + "]");
            }
            else
            {
                Log("Однозначной причины по снимку не видно. Подробности в отчёте.");
            }
            if (!string.IsNullOrEmpty(cap.Error)) Log("Ограничение: " + cap.Error);

            SaveReport();
            Log("Отчёт: " + ReportPath);

            var next = NextFix;
            string title = findings.Count > 0 ? findings[0].Title : "Причина не определена";
            Status(title, next != null ? "Следующий шаг: " + next.Title : "Все шаги испробованы. Отправьте отчёт разработчику или в поддержку Adobe.", Level.Error);
            StateChanged();
        }

        string WaitingHint()
        {
            var last = Attempts.LastOrDefault();
            if (last == null || last.Outcome != "ожидает проверки") return "Программа ждёт и начнёт следить автоматически.";
            string retest = AwaitingConfirmation ? "откройте Camera Raw и повторите действие, вызвавшее ошибку" : "откройте Camera Raw";
            return last.Fix.Manual
                ? "Выполните действия из инструкции (она есть и в журнале ниже), затем " + retest + "."
                : "Шаг применён: " + last.Fix.Title + ". Запустите Photoshop, " + retest + ".";
        }

        // The window came back on its own, so this was a long wait rather than a dead hang.
        void OnRecovered()
        {
            lock (sync)
            {
                CaptureDone = false;
                if (failedByCapture != null) failedByCapture.Outcome = "ожидает проверки";
                failedByCapture = null;
            }
            Status("Photoshop снова отвечает", "Это было долгое ожидание, а не мёртвое зависание. Слежу дальше.", Level.Info);
            SaveReport();
            StateChanged();
        }

        void OnAcrWorking()
        {
            Log("Camera Raw открылся и отвечает уже " + AcrOkSec + " с.");
            var last = Attempts.LastOrDefault();
            if (AwaitingConfirmation)
            {
                // Camera Raw staying open proves nothing when the error appears only on a specific action.
                Status("Повторите действие, вызывавшее ошибку", "Например, выберите размытие. Если ошибки нет, нажмите кнопку «Заработало».", Level.Info);
                StateChanged();
            }
            else if (last != null && last.Outcome == "ожидает проверки")
            {
                MarkHelped(last);
            }
            else if (!CaptureDone && !Solved)
            {
                Status("Camera Raw работает нормально", "Зависания нет. Если оно случится, программа заметит; можно также нажать «Зависло сейчас».", Level.Ok);
            }
        }

        public bool AwaitingConfirmation
        {
            get
            {
                var last = Attempts.LastOrDefault();
                return last != null && last.Outcome == "ожидает проверки" && !CaptureDone && LastCapture != null && LastCapture.ErrorDialogTitle != null;
            }
        }

        public void ConfirmWorked()
        {
            var last = Attempts.LastOrDefault();
            if (last != null && last.Outcome == "ожидает проверки") MarkHelped(last);
        }

        void MarkHelped(Attempt attempt)
        {
            attempt.Outcome = "помогло";
            Solved = true;
            Log("ПОМОГЛО: " + attempt.Fix.Title);
            Status("Заработало! Помог шаг: " + attempt.Fix.Title, AfterSuccessHint(attempt.Fix), Level.Ok);
            Log(AfterSuccessHint(attempt.Fix));
            SaveReport();
            StateChanged();
        }

        static string AfterSuccessHint(Fix fix)
        {
            switch (fix.Id)
            {
                case "ai-cpu":
                    return "Ошибка связана с работой ИИ-функций на видеокарте. Файл DisableGPUInference.txt оставьте на месте и учитывайте ограничения из инструкции.";
                case "gpu-off":
                    return "Зависание связано с видеокартой. Откройте настройки Camera Raw → Производительность и выберите «Выкл», чтобы закрепить результат. Затем можно попробовать драйвер NVIDIA Studio и снова включить GPU.";
                case "acr-userdata":
                    return "Причина в пресетах или профилях. Копии лежат рядом с папкой настроек Camera Raw (папки fixphotoshop-bak-…). Возвращайте их по частям.";
                case "ps-prefs":
                    return "Причина была в настройках Photoshop. Старая папка настроек сохранена с префиксом fixphotoshop-bak-.";
                default:
                    return "Если зависание вернётся, запустите программу снова.";
            }
        }

        // Called from a background thread after the user has confirmed in the UI.
        public void ApplyNext()
        {
            applying = true;
            try
            {
                Fix fix = NextFix;
                if (fix == null) return;

                if (!ClosePhotoshop()) return;

                var previous = Attempts.LastOrDefault();
                if (previous != null && previous.Outcome == "не помогло" && previous.Fix.RevertIfNoHelp)
                {
                    foreach (string line in Backups.Undo(previous.Fix.Id)) Log("Возврат: " + line);
                }

                var attempt = new Attempt { Fix = fix, When = DateTime.Now };
                Log("Шаг: " + fix.Title);
                if (fix.Manual)
                {
                    Log(fix.Instructions.Replace("\n", " "));
                }
                else
                {
                    try
                    {
                        attempt.Log = fix.Apply();
                        foreach (string line in attempt.Log) Log(line);
                    }
                    catch (Exception ex)
                    {
                        attempt.Outcome = "ошибка: " + ex.Message;
                        Log("Не удалось применить шаг: " + ex.Message);
                    }
                }
                lock (sync)
                {
                    Attempts.Add(attempt);
                    CaptureDone = false;
                }
                SaveReport();
                Status("Шаг применён. Проверяем", fix.Manual ? "Выполните действия из инструкции, затем откройте Camera Raw." : "Запустите Photoshop и откройте Camera Raw ещё раз.", Level.Info);
                StateChanged();
            }
            finally { applying = false; }
        }

        bool ClosePhotoshop()
        {
            foreach (var p in Process.GetProcessesByName("Photoshop"))
            {
                try
                {
                    p.Kill();
                    if (!p.WaitForExit(15000))
                    {
                        Log("Photoshop не закрылся за 15 с. Такой процесс застрял в драйвере; помогает только перезагрузка компьютера.");
                        Status("Photoshop не закрывается", "Перезагрузите компьютер, запустите программу снова и повторите.", Level.Error);
                        return false;
                    }
                    Log("Зависший Photoshop закрыт.");
                }
                catch (Exception ex)
                {
                    Log("Не удалось закрыть Photoshop: " + ex.Message);
                    Status("Не удалось закрыть Photoshop", "Закройте его вручную через Диспетчер задач и повторите.", Level.Error);
                    return false;
                }
            }
            PhotoshopRunning = false;
            return true;
        }

        public void UndoAll()
        {
            applying = true;
            try
            {
                if (Process.GetProcessesByName("Photoshop").Length > 0)
                {
                    Log("Закройте Photoshop перед откатом.");
                    return;
                }
                var lines = Backups.Undo(null);
                foreach (string l in lines) Log(l);
                Log(lines.Count == 0 ? "Откатывать нечего." : "Откат завершён.");
                SaveReport();
                StateChanged();
            }
            finally { applying = false; }
        }
    }
}
