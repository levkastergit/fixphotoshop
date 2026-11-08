using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FixPhotoshop
{
    // Camera Raw logs can repeat one line thousands of times, which pushes the real error out of any plain tail.
    // Identical lines are folded into one with a count, and the lines worth reading are picked out.
    static class LogDigest
    {
        const int MaxLines = 80;
        static readonly Regex Timestamp = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}:\s*");
        static readonly Regex Interesting = new Regex(@"model|provision|depth|inference|onnx|directml|winml|download|unable|fail|\*\*\*", RegexOptions.IgnoreCase);
        // Wording quoted from real Camera Raw logs and dialogs on the Adobe forum.
        static readonly Regex ModelTrouble = new Regex(@"models? not properly installed|ml model not loaded|not provisioned|models? failed to (download|load)|unable to (load|download) (ai )?models?", RegexOptions.IgnoreCase);

        public sealed class Result
        {
            public List<string> Lines = new List<string>();
            public List<string> ModelTrouble = new List<string>();
        }

        public static Result Analyze(IEnumerable<string> rawLines)
        {
            var counts = new Dictionary<string, int>();
            var lastSeen = new Dictionary<string, int>();
            int index = 0;
            foreach (string raw in rawLines)
            {
                string line = Timestamp.Replace(raw.Trim(), "");
                if (line.Length == 0) continue;
                int n;
                counts.TryGetValue(line, out n);
                counts[line] = n + 1;
                lastSeen[line] = index++;
            }

            var picked = counts.Keys.Where(l => Interesting.IsMatch(l)).OrderBy(l => lastSeen[l]).ToList();
            var result = new Result();
            foreach (string l in picked.Skip(Math.Max(0, picked.Count - MaxLines)))
                result.Lines.Add((counts[l] > 1 ? "[×" + counts[l] + "] " : "") + l);
            result.ModelTrouble = counts.Keys.Where(l => ModelTrouble.IsMatch(l)).OrderBy(l => lastSeen[l]).ToList();
            return result;
        }

        public static List<string> ReadLastLines(string path, int maxBytes)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(Math.Max(0, fs.Length - maxBytes), SeekOrigin.Begin);
                using (var reader = new StreamReader(fs))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            return lines;
        }
    }
}
