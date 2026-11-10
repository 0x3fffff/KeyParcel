using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KeyParcel
{
    internal static class Verify
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Run(args);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        private static void Run(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string work = args[0];
            var inbox = new MobileInbox(work);
            CheckLayouts(() => new ParcelWindow(work, new TestKeyboard()), work, "desktop", form =>
            {
                var button = form.Controls.Find("ConnectPhone", true).Single();
                var summary = button.Parent.Controls.OfType<TableLayoutPanel>().Single();
                summary.Controls.OfType<Label>().Last().Text = "接收中 / 手机已配对 / 手机文字可用";
            });
            CheckLayouts(() => new PairingWindow(inbox, () => null, _ => { }, () => { }, IPAddress.Loopback),
                work, "pairing-off");
            int testPort;
            using (var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                occupied.ExclusiveAddressUse = true;
                occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                occupied.Listen(1);
                testPort = ((IPEndPoint)occupied.LocalEndPoint).Port;
                VerifyReceiver(work, inbox, testPort);
            }
            using (var preferred = new MobileServer(IPAddress.Loopback, testPort, inbox))
                Require(preferred.ListeningPort == testPort, "A free preferred port was not used.");
            VerifyInput();
            VerifyInputModes();
            VerifyRimeImport(work);
            VerifyShortcutRecording();
            VerifyQuickText(work);
        }

        private static void VerifyRimeImport(string work)
        {
            var schemes = RimeSetup.EnabledSchemes("schema_list:\r\n  - schema: luna_pinyin_simp\r\n  - schema: luna_pinyin\r\nother:\r\n  - schema: ignored\r\n");
            Require(schemes.SequenceEqual(new[] { "luna_pinyin_simp", "luna_pinyin" }), "Import did not isolate enabled Rime schemes.");
            string existing = "customization:\r\n  version: 1\r\npatch: # my options\r\n  \"menu/page_size\": 7\r\n";
            string patched, repeated;
            const string insertion = "  \"engine/processors/@before 0\": lua_processor@*keyparcel_input\r\n";
            Require(RimeSetup.TryAddProcessor(existing, out patched) && patched.Replace(insertion, "") == existing,
                "Import changed unrelated user settings or line endings.");
            Require(RimeSetup.TryAddProcessor(patched, out repeated) && repeated == patched,
                "Repeated import duplicated the processor.");
            Require(!RimeSetup.TryAddProcessor("patch: {foo: 1}", out patched) &&
                !RimeSetup.TryAddProcessor("patch:\n  engine/processors/@before 0: other\n", out patched),
                "Import rewrote a custom patch it could not safely merge.");
            Require(RimeSetup.TryAddProcessor("", out patched) && patched.StartsWith("patch:\n"),
                "Import could not prepare a new schema patch.");

            string rime = Path.Combine(work, "rime-status");
            MobileInbox.SaveSetting(Path.Combine(rime, "lua", "keyparcel_input.lua"), "return {}\n");
            MobileInbox.SaveSetting(Path.Combine(rime, "build", "default.yaml"), "schema_list:\n  - schema: stroke\n");
            Require(!RimeSetup.HasDeployedExtension(rime), "Setup was marked complete before the processor was deployed.");
            MobileInbox.SaveSetting(Path.Combine(rime, "build", "stroke.schema.yaml"),
                "engine:\n  processors:\n    - \"lua_processor@*keyparcel_input\"\n");
            Require(RimeSetup.HasDeployedExtension(rime), "The deployed extension was not detected.");

            string reply = Path.Combine(work, "rime-test-reply.txt");
            File.WriteAllText(reply, "sample:3");
            using (File.Open(reply, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                Require(RimeInput.ConfirmationReceived(reply, "sample:3"), "The reply could not be read while Rime had it open for writing.");
                Require(!RimeInput.ConfirmationReceived(reply, "sample:4"), "An older reply was accepted for the next character.");
            }
        }

        private sealed class TestKeyboard : IVirtualKeyboard
        {
            public bool IsReady { get; set; }
            public bool NeedsInstallConfirmation { get; set; }
            internal bool CanStart;
            internal int SetupCalls;
            internal readonly List<string> Units = new List<string>();
            public Task<string> Start()
            {
                SetupCalls++;
                IsReady = CanStart;
                return Task.FromResult(IsReady ? null : "Driver unavailable.");
            }
            public void Send(IntPtr window, string unit) { Units.Add(unit); }
            public void Close() { IsReady = false; }
            public void Dispose() { }
        }

        private static void VerifyInputModes()
        {
            var unavailable = new InputModes(null);
            Require(unavailable.Toggle(() => { throw new InvalidOperationException("No package to install."); }).Result != null &&
                !unavailable.Virtual, "The portable build advertised a missing driver as ready.");

            var driver = new TestKeyboard { NeedsInstallConfirmation = true };
            var modes = new InputModes(driver);
            var window = new IntPtr(1);
            Action<string> send;
            RimeInput rime;
            Require(modes.Prepare("中文🙂", window, out send, out rime) == null && send == null && rime == null && driver.SetupCalls == 0,
                "Default input is no longer Unicode simulation.");
            Require(modes.Toggle(() => false).Result != null && !modes.Virtual && driver.SetupCalls == 0,
                "Declining the install changed input mode.");
            Require(modes.Toggle(() => true).Result != null && !modes.Virtual && driver.SetupCalls == 1,
                "A failed startup changed input mode.");
            driver.CanStart = true;
            Require(modes.Toggle(() => true).Result == null && modes.Virtual && driver.SetupCalls == 2,
                "A successful startup did not enable virtual mode.");

            foreach (string invalid in new[] { "A中", "A🙂", "A。", "A\t", "A\u007f" })
                Require(modes.Prepare(invalid, window, out send, out rime) != null && send == null && driver.Units.Count == 0,
                    "Unsupported text was accepted in virtual mode.");
            string text = "Az09 !?\r\n";
            Require(modes.Prepare(text, window, out send, out rime) == null && send != null && rime == null,
                "Supported text was rejected in virtual mode.");
            using (var pace = new PacedInput(_ => { }, () => window,
                _ => { throw new InvalidOperationException("Virtual input fell back to simulation."); }))
            {
                Require(pace.Begin(PasteShortcut.Phone, text, 0, window, send), "Virtual input did not start.");
                Pump(() => driver.Units.Count == 8, 1800);
                Require(String.Concat(driver.Units) == "Az09 !?\n", "Virtual input was not routed or paced correctly.");
                Require(pace.Begin(PasteShortcut.Phone, "next", 0, window, send) && pace.Discard() && !pace.Resume(),
                    "Switching input modes could resume a previous job.");
            }
            driver.IsReady = false;
            Require(modes.Prepare(text, window, out send, out rime) != null && send == null,
                "The unavailable virtual keyboard silently fell back to simulation.");
            Require(modes.Toggle(() => false).Result == null && !modes.Virtual, "Could not switch to simulation.");
            driver.NeedsInstallConfirmation = false;
            Require(modes.Toggle(() => { throw new InvalidOperationException("Installed driver prompted for setup again."); }).Result == null &&
                modes.Virtual && driver.SetupCalls == 3, "Installed driver could not restart without the setup dialog.");
            var unicodeModes = new InputModes(driver, ".");
            Require(unicodeModes.Toggle(() => false).Result == null, "Virtual input is unavailable for Unicode preflight.");
            Require(unicodeModes.Prepare("不会吧。不会吧 A1🙂", window, out send, out rime) == null && rime != null,
                "Mixed Unicode was not routed to the Rime session.");
            Require(unicodeModes.Prepare("中\ud800", window, out send, out rime) != null && send == null,
                "An incomplete surrogate was accepted for virtual input.");
            foreach (int scalar in new[] { 10, 'A', '。', '不', 0x20000, 0x1f642, 0x10ffff })
            {
                var keys = RimeInput.Encode(scalar);
                Require(keys.Length == 6 && keys.All(key => key >= '\uE000' && key <= '\uE00A') &&
                    keys.Aggregate(0, (value, key) => value * 11 + key - '\uE000') == scalar,
                    "A Unicode scalar did not round trip through the virtual keyboard encoding.");
            }
        }

        private static void VerifyQuickText(string work)
        {
            string folder = Path.Combine(work, "quick-text-test");
            var store = new QuickTextStore(folder);
            Require(!store.Enabled && store.Entries.Count == 0, "New quick-text settings should start empty.");
            using (var dialog = new QuickTextWindow(store, _ => true))
            {
                Capture(dialog, Path.Combine(work, "quick-text-empty.png"), 100);
                var controls = Descendants(dialog).ToList();
                var add = controls.OfType<Button>().Single(button => button.Text == "新增条目");
                var delete = controls.OfType<Button>().Single(button => button.Text == "删除");
                var save = controls.OfType<Button>().Single(button => button.Text == "保存条目");
                var list = controls.OfType<ListBox>().Single();
                add.PerformClick();
                add.PerformClick();
                Require(store.Entries.Select(entry => entry.Chord).SequenceEqual(
                    new[] { Keys.Control | Keys.D1, Keys.Control | Keys.D2 }), "Automatic allocation did not start with Ctrl+1 and Ctrl+2. " +
                    String.Join(" | ", controls.OfType<Label>().Select(label => label.Text)));
                string secondId = store.Entries[1].Id;
                controls.OfType<TextBox>().Single(box => box.AccessibleName == "条目标题").Text = "常用回复";
                controls.OfType<TextBox>().Single(box => box.AccessibleName == "条目内容").Text = "第一行。\r\n第二行🙂";
                save.PerformClick();
                list.SelectedIndex = 0;
                delete.PerformClick();
                Require(store.Entries.Count == 1 && store.Entries[0].Id == secondId &&
                    store.Entries[0].Chord == (Keys.Control | Keys.D2), "Deleting an entry shifted another shortcut.");
                add.PerformClick();
                Require(store.Entries[1].Chord == (Keys.Control | Keys.D1), "A new entry did not fill the available gap.");
                controls.OfType<CheckBox>().Single().Checked = true;
                Require(store.Enabled, "The quick-text switch was not saved.");
            }
            var restored = new QuickTextStore(folder);
            Require(restored.Enabled && restored.Entries.Count == 2 &&
                restored.Entries[0].Title == "常用回复" && restored.Entries[0].Text == "第一行。\r\n第二行🙂",
                "The quick-text text or enabled state did not survive restart.");
            Require(restored.NextChord(chord => chord != (Keys.Control | Keys.D3)) == (Keys.Control | Keys.D4),
                "Allocation did not skip an unavailable hotkey.");
            CheckLayouts(() => new QuickTextWindow(restored, _ => true), work, "quick-text");
            using (var compact = new QuickTextWindow(restored, _ => true))
            {
                Capture(compact, Path.Combine(work, "quick-text-compact-150.png"), 150, form =>
                {
                    var controls = Descendants(form).ToList();
                    var list = controls.OfType<ListBox>().Single();
                    var body = controls.OfType<TextBox>().Single(box => box.AccessibleName == "条目内容");
                    int listHeight = list.Height, bodyHeight = body.Height;
                    form.ClientSize = new Size((int)(form.ClientSize.Width * .88), (int)(form.ClientSize.Height * .74));
                    form.PerformLayout();
                    Require(list.Height < listHeight && body.Height < bodyHeight && !form.VerticalScroll.Visible,
                        "The compact quick-text window did not shrink its editors.");
                });
                CheckChildren(compact, "compact quick-text 150%");
            }
            restored.Save(false, restored.Entries);
            Require(!new QuickTextStore(folder).Enabled, "Disabled state did not survive restart.");
            Console.WriteLine("Quick-text add, edit, delete, hotkey allocation and restart state passed.");
        }

        private static void VerifyShortcutRecording()
        {
            using (var field = new ShortcutField(Keys.Control | Keys.Shift | Keys.V, "测试快捷键"))
            {
                var recorded = new List<Keys>();
                field.Recorded += recorded.Add;
                var command = typeof(ShortcutField).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (Keys key in new[] { Keys.Z, Keys.A, Keys.D1, Keys.V })
                {
                    var message = Message.Create(field.Handle, 0x0100, (IntPtr)(int)key, IntPtr.Zero);
                    command.Invoke(field, new object[] { message, Keys.Control | key });
                }
                Require(recorded.SequenceEqual(new[] { Keys.Control | Keys.Z, Keys.Control | Keys.A, Keys.Control | Keys.D1 })
                    && field.Text == "Ctrl + 1", "Recorder lost a command key or accepted the reserved Ctrl+V.");
            }
        }

        private static void VerifyReceiver(string work, MobileInbox inbox, int occupiedPort)
        {
            using (var receiver = new MobileServer(IPAddress.Loopback, occupiedPort, inbox))
            {
                Require(receiver.ListeningPort != occupiedPort &&
                    new Uri(receiver.Address).Port == receiver.ListeningPort,
                    "An occupied port did not fall back to an available port in the advertised URL.");
                inbox.NewCode();
                var code = inbox.PairCode;
                CheckLayouts(() => new PairingWindow(inbox, () => receiver, _ => { }, () => { }, IPAddress.Loopback),
                    work, "pairing");
                string url = receiver.Address;
                string response;
                Require(Request(url, "/", null, null, out response) == 200 &&
                    response.Contains("KeyParcel") && response.Contains("By:0x3fffff") &&
                    response.Contains("href=\"https://wenhao.li/\""), "Phone page or author link missing.");
                Require(Request(url, "/pair", code, "", out response) == 200, "Pairing failed.");
                string token = response;
                Require(Request(url, "/pair", code, "", out response) == 403, "Pair code replay accepted.");
                Require(Request(url, "/text", token, "一🙂", out response) == 200 && inbox.Latest == "一🙂",
                    "Phone text did not reach the inbox.");
                Require(Request(url, "/text", token, "新文字", out response) == 200 && inbox.Latest == "新文字",
                    "Phone text was not replaced.");
                var restarted = new MobileInbox(work);
                Require(restarted.Latest == null && restarted.Accept(token, null), "Restart state was incorrect.");
                inbox.Forget();
                Require(Request(url, "/check", token, "", out response) == 403, "Revoked phone could still connect.");
            }
        }

        private static void VerifyInput()
        {
            var units = new List<string>();
            var window = new IntPtr(1);
            var timer = Stopwatch.StartNew();
            var times = new List<long>();
            string inputStatus = "";
            using (var pace = new PacedInput(value => inputStatus = value, () => window, unit =>
            {
                units.Add(unit);
                times.Add(timer.ElapsedMilliseconds);
            }))
            {
                Require(pace.Begin(PasteShortcut.Phone, "中\r\n🙂", 80, window), "Input did not start.");
                Pump(() => units.Count == 3, 2000);
                Require(String.Join("|", units) == "中|\n|🙂" && times[0] >= 450 && times[2] - times[0] >= 130,
                    "Text units or pacing were incorrect.");
                units.Clear();
                Require(pace.Begin(PasteShortcut.Phone, "A\r\n🙂B", 200, window), "Could not start manual pause scenario.");
                Pump(() => units.Count == 1, 1500);
                Require(units.Count == 1 && pace.HandleRunningPasteShortcut(PasteShortcut.Clipboard),
                    "A different shortcut was not consumed while input was running.");
                Pump(() => units.Count == 2, 1500);
                Require(units.Count == 2 && pace.HandleRunningPasteShortcut(PasteShortcut.Phone),
                    "Original shortcut did not pause input.");
                Pump(() => units.Count > 2, 350);
                Require(String.Concat(units) == "A\n", "Input continued after manual pause.");
                window = new IntPtr(2);
                Require(!pace.Resume(), "Manual pause could resume in another window.");
                window = new IntPtr(1);
                Require(pace.Resume(), "Manual pause could not resume in its original window.");
                Pump(() => units.Count == 4, 1800);
                Require(String.Join("|", units) == "A|\n|🙂|B", "Resume skipped or repeated a unit.");
                Require(!pace.Resume(), "Completed input could be replayed by Resume.");

                units.Clear();
                pace.Begin(PasteShortcut.Clipboard, "AB", 200, window);
                Pump(() => units.Count != 0, 1500);
                window = new IntPtr(2);
                Pump(() => inputStatus.StartsWith("已切换窗口"), 800);
                Require(units.Count == 1 && inputStatus.StartsWith("已切换窗口"),
                    "Focus change did not pause input.");
                Require(!pace.Resume(), "Automatic pause could resume in another window.");
                window = new IntPtr(1);
                Require(pace.Resume(), "Automatic pause lost its progress.");
                Pump(() => units.Count == 2, 1500);
                Require(String.Concat(units) == "AB", "Automatic pause did not resume from the next character.");

                units.Clear();
                Require(pace.Begin(PasteShortcut.Clipboard, "old", 250, window) &&
                    pace.HandleRunningPasteShortcut(PasteShortcut.Clipboard), "Could not pause before the first character.");
                Require(!pace.HandleRunningPasteShortcut(PasteShortcut.Clipboard) &&
                    pace.Begin(PasteShortcut.Clipboard, "new", 0, window), "A paused paste blocked a fresh paste.");
                Pump(() => units.Count >= 3, 1500);
                Require(String.Concat(units) == "new" && !pace.Resume(), "A fresh paste did not replace the paused one.");
            }
            Require(Marshal.SizeOf(typeof(WindowsKeyboard.Input)) == (IntPtr.Size == 8 ? 40 : 28),
                "Native input structure size is incorrect.");
            var encoded = WindowsKeyboard.Encode("🙂");
            Require(encoded.Length == 4 && encoded[0].Data.Keyboard.Scan == "🙂"[0] &&
                encoded[2].Data.Keyboard.Scan == "🙂"[1] && encoded[3].Data.Keyboard.Flags == 6,
                "Unicode keyboard events are incorrect.");
            Require(WindowsKeyboard.Encode("\n")[0].Data.Keyboard.Key == 13, "Return event missing.");
            Require(!Hotkeys.Valid(Keys.Control | Keys.V), "System paste should be reserved.");
            using (var keys = new Hotkeys(_ => { }))
            {
                var first = Keys.Control | Keys.Alt | Keys.Shift | Keys.F9;
                Require(keys.Set(PasteShortcut.Phone, first) == null &&
                    keys.Set(PasteShortcut.Clipboard, first) != null &&
                    keys.Set(PasteShortcut.Resume, first) != null, "Hotkey conflict handling failed.");
                Require(keys.Set(PasteShortcut.Resume, Keys.Control | Keys.Alt | Keys.Shift | Keys.F10) == null,
                    "Could not register a separate resume shortcut.");
                string snippet = "quick:verification";
                var snippetChord = Keys.Control | Keys.Alt | Keys.Shift | Keys.F11;
                Require(keys.Set(snippet, snippetChord) == null, "Dynamic shortcut registration failed.");
                keys.Suspend();
                using (var other = new Hotkeys(_ => { }))
                    Require(other.Set(snippet, snippetChord) == null, "The shortcut remained registered while suspended.");
                Require(keys.Restore() == 0, "Suspended shortcuts could not be restored.");
                keys.Remove(snippet);
                Require(keys.CanRegister(snippetChord), "Removing an entry did not release the global shortcut.");
            }
            Console.WriteLine("Desktop layouts, receiver, pairing, Unicode input, pacing, pause/resume and shortcuts passed.");
        }

        private static void CheckLayouts(Func<Form> create, string folder, string name, Action<Form> setup = null)
        {
            foreach (int percent in new[] { 100, 125, 150, 200 })
            {
                using (var form = create())
                {
                    if (setup != null) setup(form);
                    Capture(form, Path.Combine(folder, name + "-" + percent + ".png"), percent);
                    CheckChildren(form, name + " " + percent + "%");
                    var author = Descendants(form).OfType<LinkLabel>().Single();
                    Require(author.Text == "By:0x3fffff" && author.TabStop && author.Links.Count == 1 &&
                        (string)author.Links[0].LinkData == "https://wenhao.li/", "Author link is missing or incorrect.");
                }
            }
        }

        private static void CheckChildren(Control parent, string context)
        {
            var controls = parent.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
            for (int i = 0; i < controls.Length; i++)
            {
                var control = controls[i];
                string label = String.IsNullOrEmpty(control.Text) ? control.GetType().Name : control.Text;
                Require(parent.ClientRectangle.Contains(control.Bounds), context + " clips " + label +
                    " " + control.Bounds + " in " + parent.ClientRectangle);
                for (int j = 0; j < i; j++)
                    Require(!control.Bounds.IntersectsWith(controls[j].Bounds),
                        context + " overlaps " + label + " with " + controls[j].Text);
                var button = control as Button;
                if (button != null)
                {
                    Require(control.Width >= control.PreferredSize.Width && control.Height >= control.PreferredSize.Height,
                        context + " clips the button text for " + label);
                    CheckButtonCorners(button, context);
                }
                if (control is TableLayoutPanel) CheckChildren(control, context);
            }
        }

        private static void CheckButtonCorners(Button button, string context)
        {
            bool inCard = button.Text == "连接手机" || button.Text == "复制" ||
                button.Text == "生成新配对码" || button.Text == "解除配对";
            int expected = (inCard ? Theme.Tint : Theme.Canvas).ToArgb();
            var paint = button.GetType().GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var bitmap = new Bitmap(button.Width, button.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var args = new PaintEventArgs(graphics, button.ClientRectangle))
            {
                // Reproduce an uninitialized or reused paint buffer without the parent painting first.
                graphics.Clear(Color.Black);
                paint.Invoke(button, new object[] { args });
                foreach (int x in new[] { 0, bitmap.Width - 1 })
                    foreach (int y in new[] { 0, bitmap.Height - 1 })
                        Require(bitmap.GetPixel(x, y).ToArgb() == expected,
                            context + " has an unpainted corner on " + button.Text);
            }
        }

        private static void Capture(Form form, string path, int percent, Action<Form> resize = null)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.MaximumSize = new Size(4096, 4096);
            SetWindowLong(form.Handle, -20, GetWindowLong(form.Handle, -20) | 0x08000000);
            form.Show();
            Application.DoEvents();
            if (percent != 100)
            {
                // Simulate high-DPI bounds and font metrics without changing system settings.
                var size = form.ClientSize;
                float factor = percent / 100f;
                var originalFonts = Descendants(form).ToDictionary(control => control, control => control.Font);
                form.Scale(new SizeF(factor, factor));
                var scaledFonts = originalFonts.Values.Distinct().ToDictionary(font => font,
                    font => new Font(font.FontFamily, font.SizeInPoints * factor, font.Style));
                form.Disposed += delegate { foreach (var font in scaledFonts.Values) font.Dispose(); };
                foreach (var entry in originalFonts) entry.Key.Font = scaledFonts[entry.Value];
                form.ClientSize = new Size((int)Math.Round(size.Width * factor), (int)Math.Round(size.Height * factor));
                form.PerformLayout();
                Require(form.ClientSize.Width >= size.Width * factor - 2, form.Text + " at " + percent +
                    "% did not scale from " + size.Width + " to " + (size.Width * factor) + " (actual " + form.ClientSize.Width + ").");
            }
            if (resize != null) resize(form);
            using (var image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height));
                image.Save(path);
            }
        }

        private static IEnumerable<Control> Descendants(Control control)
        {
            yield return control;
            foreach (Control child in control.Controls)
                foreach (var descendant in Descendants(child))
                    yield return descendant;
        }

        private static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
        }

        private static void Pump(Func<bool> done, int timeout)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeout)
            {
                Application.DoEvents();
                Thread.Sleep(2);
            }
        }

        private static int Request(string url, string route, string token, string text, out string result)
        {
            var request = (HttpWebRequest)WebRequest.Create(url + route);
            request.Proxy = null;
            request.Timeout = 3000;
            request.ServicePoint.Expect100Continue = false;
            if (text != null)
            {
                request.Method = "POST";
                request.Headers["Origin"] = url;
                request.Headers["Authorization"] = "Bearer " + token;
                var bytes = Encoding.UTF8.GetBytes(text);
                request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            HttpWebResponse response;
            try { response = (HttpWebResponse)request.GetResponse(); }
            catch (WebException error)
            {
                response = error.Response as HttpWebResponse;
                if (response == null) throw;
            }
            using (response)
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                result = reader.ReadToEnd();
                return (int)response.StatusCode;
            }
        }
    }
}
