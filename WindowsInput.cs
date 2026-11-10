using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyParcel
{
    internal static class PasteShortcut
    {
        internal const string Phone = "phone", Clipboard = "clipboard", Resume = "resume";
    }

    internal sealed class Hotkeys : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private sealed class Binding { internal Keys Chord; internal int Id; }
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>();
        private readonly Action<string> pressed;
        private const int ProbeId = 0xbfff;
        private int nextId = 1;

        internal Hotkeys(Action<string> pressed)
        {
            this.pressed = pressed;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        internal string Set(string shortcut, Keys chord, string savePath = null)
        {
            if (!Valid(chord)) return "请用 Ctrl、Alt 或 Shift 搭配字母、数字或功能键。Ctrl + V 留给系统。";
            Binding existing;
            if (bindings.TryGetValue(shortcut, out existing) && existing.Chord == chord && existing.Id != 0) return null;
            int id = InUse(chord, shortcut) ? 0 : Register(chord);
            if (id == 0)
                return "快捷键已被占用。请录制另一组。";
            try
            {
                if (savePath != null)
                    MobileInbox.SaveSetting(savePath, ((int)chord).ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                UnregisterHotKey(Handle, id);
                return "无法保存设置。请检查用户设置目录。";
            }
            catch (UnauthorizedAccessException)
            {
                UnregisterHotKey(Handle, id);
                return "无法保存设置。请检查用户设置目录。";
            }
            Remove(shortcut);
            bindings[shortcut] = new Binding { Chord = chord, Id = id };
            return null;
        }

        internal bool InUse(Keys chord, string except = null)
        {
            return bindings.Any(item => item.Key != except && item.Value.Chord == chord);
        }

        internal bool CanRegister(Keys chord, string except = null)
        {
            if (!Valid(chord) || InUse(chord, except)) return false;
            Binding existing;
            if (except != null && bindings.TryGetValue(except, out existing) &&
                existing.Id != 0 && existing.Chord == chord) return true;
            if (!RegisterHotKey(Handle, ProbeId, Modifiers(chord), (uint)(chord & Keys.KeyCode))) return false;
            UnregisterHotKey(Handle, ProbeId);
            return true;
        }

        internal void Remove(string shortcut)
        {
            Binding binding;
            if (!bindings.TryGetValue(shortcut, out binding)) return;
            if (binding.Id != 0) UnregisterHotKey(Handle, binding.Id);
            bindings.Remove(shortcut);
        }

        internal void Suspend()
        {
            foreach (var binding in bindings.Values)
            {
                if (binding.Id != 0) UnregisterHotKey(Handle, binding.Id);
                binding.Id = 0;
            }
        }

        internal int Restore()
        {
            int failed = 0;
            foreach (var binding in bindings.Values)
            {
                if (binding.Id != 0) continue;
                binding.Id = Register(binding.Chord);
                if (binding.Id == 0) failed++;
            }
            return failed;
        }

        private int Register(Keys chord)
        {
            for (int attempt = 0; attempt < ProbeId - 1; attempt++)
            {
                int id = nextId++;
                if (nextId == ProbeId) nextId = 1;
                if (bindings.Values.Any(binding => binding.Id == id)) continue;
                return RegisterHotKey(Handle, id, Modifiers(chord), (uint)(chord & Keys.KeyCode)) ? id : 0;
            }
            return 0;
        }

        private static uint Modifiers(Keys chord)
        {
            uint mods = 0x4000; // MOD_NOREPEAT
            if ((chord & Keys.Alt) != 0) mods |= 1;
            if ((chord & Keys.Control) != 0) mods |= 2;
            if ((chord & Keys.Shift) != 0) mods |= 4;
            return mods;
        }

        internal static bool Valid(Keys chord)
        {
            Keys key = chord & Keys.KeyCode, mods = chord & Keys.Modifiers;
            return mods != Keys.None && (mods & ~(Keys.Control | Keys.Shift | Keys.Alt)) == 0 &&
                chord != (Keys.Control | Keys.V) &&
                (key >= Keys.A && key <= Keys.Z || key >= Keys.D0 && key <= Keys.D9 || key >= Keys.F1 && key <= Keys.F12);
        }

        internal static string Display(Keys chord)
        {
            if (chord == Keys.None) return "未分配";
            Keys key = chord & Keys.KeyCode;
            return ((chord & Keys.Control) != 0 ? "Ctrl + " : "") +
                ((chord & Keys.Alt) != 0 ? "Alt + " : "") +
                ((chord & Keys.Shift) != 0 ? "Shift + " : "") +
                (key >= Keys.D0 && key <= Keys.D9 ? (key - Keys.D0).ToString() : key.ToString());
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312)
            {
                int id = message.WParam.ToInt32();
                if (id != 0)
                {
                    string shortcut = bindings.FirstOrDefault(item => item.Value.Id == id).Key;
                    if (shortcut != null) { pressed(shortcut); return; }
                }
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            Suspend();
            DestroyHandle();
        }
    }

    internal sealed class PacedInput : IDisposable
    {
        private readonly Timer timer = new Timer();
        private readonly Action<string> status;
        private readonly Func<IntPtr> foreground;
        private readonly Action<string> defaultSend;
        private Action<string> send;
        private RimeInput rime;
        private bool rimeStarted;
        private string pending;
        private IntPtr target;
        private int position, interval;
        private string source;
        private bool running;

        internal PacedInput(Action<string> status, Func<IntPtr> foreground, Action<string> send)
        {
            this.status = status;
            this.foreground = foreground;
            defaultSend = send;
            timer.Tick += Tick;
        }

        internal bool Begin(string shortcut, string text, int delay, IntPtr window, Action<string> sendOverride = null, RimeInput rimeInput = null)
        {
            if (running || String.IsNullOrEmpty(text) || window == IntPtr.Zero) return false;
            send = sendOverride ?? defaultSend;
            rime = rimeInput;
            target = window;
            source = shortcut;
            pending = text;
            position = 0;
            interval = delay;
            StartTimer("正在输入。再按同一个粘贴快捷键可暂停。");
            return true;
        }

        internal bool HandleRunningPasteShortcut(string shortcut)
        {
            if (!running) return false;
            if (shortcut == source) Pause("已暂停。按「继续粘贴」从原位置接着输入。");
            return true;
        }

        internal bool Resume()
        {
            if (running) return false;
            if (pending == null)
            {
                status("没有可继续的粘贴。");
                return false;
            }
            if (foreground() != target)
            {
                status("请回到原窗口，再按「继续粘贴」。");
                return false;
            }
            StartTimer("继续输入剩余文字。");
            return true;
        }

        private void StartTimer(string message)
        {
            running = true;
            timer.Interval = 500;
            timer.Start();
            status(message);
        }

        private void Pause(string message)
        {
            timer.Stop();
            running = false;
            status(message + StopRime());
        }

        private void Finish(string message)
        {
            string warning = StopRime();
            Discard();
            status(warning.Length == 0 ? message : "输入失败。" + warning);
        }

        internal bool Discard()
        {
            bool hadPending = pending != null;
            timer.Stop();
            running = false;
            string warning = StopRime();
            if (warning.Length != 0) status("输入失败。" + warning);
            pending = null;
            send = null;
            rime = null;
            target = IntPtr.Zero;
            return hadPending;
        }

        private string StopRime()
        {
            if (!rimeStarted) return "";
            rimeStarted = false;
            try { rime.Stop(); return ""; }
            catch (Exception error) { return " " + error.Message; }
        }

        private void Tick(object sender, EventArgs args)
        {
            timer.Stop();
            if (!running) return;
            try
            {
                if (position == pending.Length)
                {
                    Finish("本次输入已完成。");
                    return;
                }
                int count = interval == 0 && rime == null ? 128 : 1;
                while (count-- > 0 && position < pending.Length)
                {
                    if (foreground() != target)
                    {
                        Pause("已切换窗口，输入已暂停。回到原窗口后可继续。");
                        return;
                    }
                    if (rime != null && !rimeStarted)
                    {
                        rimeStarted = true;
                        rime.Start();
                    }
                    int start = position;
                    int next = start + 1;
                    char c = pending[start];
                    if (c == '\r' || c == '\n')
                    {
                        if (c == '\r' && next < pending.Length && pending[next] == '\n') next++;
                        send("\n");
                    }
                    else
                    {
                        if (Char.IsHighSurrogate(c) && next < pending.Length && Char.IsLowSurrogate(pending[next]))
                            next++;
                        send(pending.Substring(start, next - start));
                    }
                    position = next;
                }
                if (position == pending.Length && rime == null) Finish("本次输入已完成。");
                else
                {
                    // The IME acknowledges composition before Windows inserts the final character.
                    timer.Interval = position == pending.Length ? 50 : Math.Max(interval, 1);
                    timer.Start();
                }
            }
            catch (RimeFocusLostException)
            {
                Pause("已切换窗口，输入已暂停。回到原窗口后可继续。");
            }
            catch (Exception ex)
            {
                Finish("输入失败。 " + ex.Message);
            }
        }

        public void Dispose() { Discard(); timer.Dispose(); }
    }

    internal static class WindowsKeyboard
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            public uint Type;
            public InputData Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputData
        {
            [FieldOffset(0)] public KeyboardData Keyboard;
            [FieldOffset(0)] public MouseData Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardData
        {
            public ushort Key, Scan;
            public uint Flags, Time;
            public UIntPtr Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseData
        {
            public int X, Y;
            public uint Data, Flags, Time;
            public UIntPtr Extra;
        }

        internal static Input[] Encode(string unit)
        {
            var events = new Input[unit.Length * 2];
            bool newline = unit == "\n";
            for (int i = 0; i < unit.Length; i++)
            {
                var key = new KeyboardData
                {
                    Key = (ushort)(newline ? 13 : 0),
                    Scan = (ushort)(newline ? 0 : unit[i]),
                    Flags = newline ? 0u : 4u
                };
                events[i * 2] = new Input { Type = 1, Data = new InputData { Keyboard = key } };
                key.Flags |= 2;
                events[i * 2 + 1] = new Input { Type = 1, Data = new InputData { Keyboard = key } };
            }
            return events;
        }

        internal static void Send(string unit)
        {
            var events = Encode(unit);
            if (SendInput((uint)events.Length, events, Marshal.SizeOf(typeof(Input))) != events.Length)
                throw new InvalidOperationException("请检查目标窗口是否允许模拟键盘输入。");
        }
    }
}
