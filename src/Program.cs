using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace FixPhotoshop
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }
            return RunCommandLine(args.ToList());
        }

        static string Option(List<string> args, string name)
        {
            int i = args.IndexOf(name);
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        }

        // Command-line mode for scripted runs: diagnose without touching anything, or apply/undo a single step.
        static int RunCommandLine(List<string> args)
        {
            // A windowed exe has no console, so Console.OutputEncoding would throw here.
            var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            stdout.AutoFlush = true;
            Console.SetOut(stdout);

            if (args.Contains("--undo"))
            {
                foreach (string l in Backups.Undo(null)) Console.WriteLine(l);
                return 0;
            }

            string highPerformanceExe = Option(args, "--high-performance");
            if (highPerformanceExe != null)
            {
                foreach (string l in Fixes.HighPerformanceFix(highPerformanceExe).Apply()) Console.WriteLine(l);
                return 0;
            }

            string applyId = Option(args, "--apply");
            if (applyId != null || args.Contains("--list"))
            {
                var fixes = Fixes.Build(null, new Finding[0], new HashSet<string>());
                if (applyId == null)
                {
                    foreach (var f in fixes) Console.WriteLine(f.Id + "\t" + (f.Manual ? "вручную" : "авто") + "\t" + f.Title);
                    return 0;
                }
                var fix = fixes.FirstOrDefault(f => f.Id == applyId);
                if (fix == null || fix.Manual)
                {
                    Console.WriteLine("Шаг недоступен: " + applyId);
                    return 2;
                }
                foreach (string l in fix.Apply()) Console.WriteLine(l);
                return 0;
            }

            if (args.Contains("--headless"))
            {
                int timeout;
                if (!int.TryParse(Option(args, "--timeout"), out timeout)) timeout = 300;
                var engine = new Engine(Option(args, "--report"));
                engine.Logged += Console.WriteLine;
                engine.StatusChanged += (t, h, l) => Console.WriteLine("[" + l + "] " + t);
                engine.Run(timeout, true);
                engine.Stop();
                return engine.CaptureDone ? 0 : 3;
            }

            Console.WriteLine("Без аргументов запускается окно программы. Аргументы: --headless [--timeout N] [--report файл] | --list | --apply ID | --high-performance EXE | --undo");
            return 1;
        }
    }
}
