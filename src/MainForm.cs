using System;
using System.Drawing;
using System.Media;
using System.Threading;
using System.Windows.Forms;

namespace FixPhotoshop
{
    sealed class MainForm : Form
    {
        readonly Engine engine = new Engine(null);
        readonly Label status = new Label();
        readonly Label hint = new Label();
        readonly TextBox log = new TextBox();
        readonly Button fixButton = new Button();
        readonly Button workedButton = new Button();
        readonly Button captureButton = new Button();
        readonly Button reportButton = new Button();
        readonly Button undoButton = new Button();
        bool busy;

        public MainForm()
        {
            Text = "FixPhotoshop: диагностика зависаний Camera Raw";
            Font = new Font("Segoe UI", 9.75f);
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(600, 400);
            TopMost = true; // A frozen full-screen Photoshop must not hide the result.
            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);

            status.AutoSize = true;
            status.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            status.Dock = DockStyle.Fill;
            status.Text = "Запуск…";
            status.Margin = new Padding(10, 10, 10, 2);

            hint.AutoSize = true;
            hint.Dock = DockStyle.Fill;
            hint.Margin = new Padding(10, 0, 10, 8);

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Dock = DockStyle.Fill;
            log.BackColor = SystemColors.Window;
            log.Margin = new Padding(10, 0, 10, 8);

            fixButton.Text = "Нет шага для применения";
            fixButton.AutoSize = true;
            fixButton.Enabled = false;
            fixButton.Click += OnFix;

            workedButton.Text = "Заработало";
            workedButton.AutoSize = true;
            workedButton.Visible = false;
            workedButton.Click += (s, e) => RunBackground(engine.ConfirmWorked);

            captureButton.Text = "Зависло сейчас";
            captureButton.AutoSize = true;
            captureButton.Click += (s, e) => engine.RequestCapture();

            reportButton.Text = "Открыть отчёт";
            reportButton.AutoSize = true;
            reportButton.Click += OnOpenReport;

            undoButton.Text = "Откатить все изменения";
            undoButton.AutoSize = true;
            undoButton.Click += OnUndo;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(6, 0, 6, 8), WrapContents = true };
            buttons.Controls.AddRange(new Control[] { fixButton, workedButton, captureButton, reportButton, undoButton });

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(status, 0, 0);
            root.Controls.Add(hint, 0, 1);
            root.Controls.Add(log, 0, 2);
            root.Controls.Add(buttons, 0, 3);
            Controls.Add(root);

            engine.StatusChanged += (t, h, l) => Ui(() => SetStatus(t, h, l));
            engine.Logged += line => Ui(() => log.AppendText(line + Environment.NewLine));
            engine.StateChanged += () => Ui(RefreshButtons);

            Shown += (s, e) =>
            {
                var worker = new Thread(() => engine.Run(0, false));
                worker.IsBackground = true;
                worker.Start();
            };
            FormClosing += (s, e) => engine.Stop();
        }

        void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (Exception) { }
        }

        void SetStatus(string title, string hintText, Level level)
        {
            status.Text = title;
            hint.Text = hintText;
            switch (level)
            {
                case Level.Ok: status.ForeColor = Color.ForestGreen; break;
                case Level.Warn: status.ForeColor = Color.DarkOrange; break;
                case Level.Error:
                    status.ForeColor = Color.Firebrick;
                    SystemSounds.Exclamation.Play();
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    break;
                default: status.ForeColor = SystemColors.ControlText; break;
            }
        }

        void RefreshButtons()
        {
            Fix next = engine.CaptureDone ? engine.NextFix : null;
            fixButton.Enabled = next != null && !busy;
            fixButton.Text = next != null ? "Применить: " + next.Title : "Нет шага для применения";
            workedButton.Visible = engine.AwaitingConfirmation && !busy;
        }

        void RunBackground(Action work)
        {
            busy = true;
            RefreshButtons();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { work(); }
                finally
                {
                    Ui(() =>
                    {
                        busy = false;
                        RefreshButtons();
                    });
                }
            });
        }

        void OnFix(object sender, EventArgs e)
        {
            Fix next = engine.NextFix;
            if (next == null) return;
            string text = next.Title + "\n\n" + next.Explain;
            if (engine.PhotoshopRunning) text += "\n\nPhotoshop будет закрыт принудительно. Несохранённая работа пропадёт.";
            text += "\n\nПродолжить?";
            if (MessageBox.Show(this, text, "FixPhotoshop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            RunBackground(() =>
            {
                engine.ApplyNext();
                if (next.Manual) Ui(() => MessageBox.Show(this, next.Instructions, next.Title, MessageBoxButtons.OK, MessageBoxIcon.Information));
            });
        }

        void OnOpenReport(object sender, EventArgs e)
        {
            try { System.Diagnostics.Process.Start(engine.ReportPath); }
            catch (Exception ex) { MessageBox.Show(this, "Отчёт пока не создан: " + ex.Message, "FixPhotoshop"); }
        }

        void OnUndo(object sender, EventArgs e)
        {
            if (Backups.Count == 0)
            {
                MessageBox.Show(this, "Программа ещё ничего не меняла.", "FixPhotoshop");
                return;
            }
            string text = "Вернуть всё, что изменила программа (" + Backups.Count + " записей)? Photoshop должен быть закрыт.";
            if (MessageBox.Show(this, text, "FixPhotoshop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                RunBackground(engine.UndoAll);
        }
    }
}
