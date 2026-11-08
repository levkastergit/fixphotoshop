// Checks of the pure logic, using data seen in a real Photoshop session.
using System;
using System.Collections.Generic;
using System.Linq;
using FixPhotoshop;

namespace FixPhotoshopTests
{
    static class UnitTests
    {
        static int failed;

        static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
            if (!ok) failed++;
        }

        static bool Dialog(string title)
        {
            return StackCapture.IsErrorDialog(new WinInfo { Title = title });
        }

        static Change Installed(string what, DateTime when)
        {
            return new Change { When = when, Kind = "Установлена программа", What = what };
        }

        static int Main()
        {
            Check("title: real Camera Raw error window", Dialog("Произошла ошибка."));
            Check("title: Camera Raw window is not an error", !Dialog("Camera Raw 18.7"));
            Check("title: profile mismatch prompt is not an error", !Dialog("Несоответствие внедренного профиля"));
            Check("title: document window is not an error", !Dialog("ошибка.jpg @ 16,7% (Слой 0, RGB/8*) *"));
            Check("title: main window is not an error", !Dialog("Adobe Photoshop 2025"));

            var day = new DateTime(2026, 11, 3);
            var onset = new DateTime(2026, 11, 3, 17, 35, 0);
            var changes = new List<Change>
            {
                Installed("Google Chrome 156.0.8078.80", day.AddDays(1)),
                Installed("NVIDIA App 11.0.9.585", day),
                Installed("NVIDIA Графический драйвер 617.42", day),
                Installed("NVIDIA Container 1.51", day),
                Installed("NVIDIA ShadowPlay 11.0.9.0", day)
            };
            var timeline = Diagnosis.Timeline(changes, onset);
            Check("timeline: NVIDIA parts folded into one finding", timeline.Count == 1 && timeline[0].Id == "timeline-gpu-driver");
            Check("timeline: names the driver version", timeline.Count == 1 && timeline[0].Evidence.Contains("617.42"));
            Check("timeline: counts the components", timeline.Count == 1 && timeline[0].Evidence.Contains("4"));
            Check("timeline: says it is the only nearby change", timeline.Count == 1 && timeline[0].Advice.StartsWith("Это единственное"));

            changes.Add(new Change { When = day, Kind = "Camera Raw", What = "Плагин Camera Raw версии 18.7" });
            timeline = Diagnosis.Timeline(changes, onset);
            Check("timeline: Adobe update reported separately", timeline.Count == 2 && timeline.Any(f => f.Id == "timeline-Camera Raw"));
            Check("timeline: no longer claims to be the only change", timeline.First(f => f.Id == "timeline-gpu-driver").Advice.StartsWith("Это совпадение"));

            var log = new List<string>();
            log.AddRange(Enumerable.Repeat("*** Error: Unexpected re-entrancy in RedrawMonitor -- skipping ***", 2000));
            log.Add("2026-11-08T12:49:38+03:00: *** Warning: falling back to baseline get_ml_model_preinstall_root_paths: (depthmap) perhaps models not properly installed ***");
            log.Add("*** Error: ML model not loaded ***");
            log.Add("2026-11-08T12:49:39+03:00: Windows cr_gpu_view backing scale: h=  1.2500, v=  1.2500");
            log.AddRange(Enumerable.Repeat("*** Error: Unexpected re-entrancy in RedrawMonitor -- skipping ***", 2000));
            var digest = LogDigest.Analyze(log);
            Check("log: repeated line folded with a count", digest.Lines.Any(l => l.StartsWith("[×4000] ") && l.Contains("RedrawMonitor")));
            Check("log: buried model warning found", digest.ModelTrouble.Any(l => l.Contains("(depthmap)")));
            Check("log: model error found", digest.ModelTrouble.Contains("*** Error: ML model not loaded ***"));
            Check("log: spam is not a model error", !digest.ModelTrouble.Any(l => l.Contains("RedrawMonitor")));
            Check("log: uninteresting line dropped", !digest.Lines.Any(l => l.Contains("backing scale")));

            Check("gpu value: from nothing", Fixes.WithHighPerformance(null) == "GpuPreference=2;");
            Check("gpu value: replaces power saving", Fixes.WithHighPerformance("GpuPreference=1;") == "GpuPreference=2;");
            Check("gpu value: keeps other flags", Fixes.WithHighPerformance("AutoHDREnable=1;GpuPreference=1;") == "AutoHDREnable=1;GpuPreference=2;");

            return failed;
        }
    }
}
