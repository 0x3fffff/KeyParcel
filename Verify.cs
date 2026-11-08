using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string work = args[0];
            var inbox = new MobileInbox(work);
            CheckLayouts(() => new ParcelWindow(work), work, "desktop", form =>
            {
                var button = form.Controls.Find("ConnectPhone", true).Single();
                var summary = button.Parent.Controls.OfType<TableLayoutPanel>().Single();
                summary.Controls.OfType<Label>().Last().Text = "接收中 / 手机已配对 / 手机文字可用";
            });
            CheckLayouts(() => new PairingWindow(inbox, () => null, _ => { }, () => { }, IPAddress.Loopback),
                work, "pairing-off");
            using (var receiver = new MobileServer(IPAddress.Loopback, 0, inbox))
            {
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

        private static void Capture(Form form, string path, int percent)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            SetWindowLong(form.Handle, -20, GetWindowLong(form.Handle, -20) | 0x08000000);
            form.Show();
            Application.DoEvents();
            if (percent != 100)
            {
                // Simulate high-DPI bounds and font metrics without changing system settings.
                int width = form.ClientSize.Width;
                float factor = percent / 100f;
                var originalFonts = Descendants(form).ToDictionary(control => control, control => control.Font);
                form.Scale(new SizeF(factor, factor));
                var scaledFonts = originalFonts.Values.Distinct().ToDictionary(font => font,
                    font => new Font(font.FontFamily, font.SizeInPoints * factor, font.Style));
                form.Disposed += delegate { foreach (var font in scaledFonts.Values) font.Dispose(); };
                foreach (var entry in originalFonts) entry.Key.Font = scaledFonts[entry.Value];
                form.PerformLayout();
                Require(form.ClientSize.Width >= width * factor - 2, "Scaled window width did not change.");
            }
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
