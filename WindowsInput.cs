using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyParcel
{
    internal enum PasteShortcut { Phone, Clipboard, Resume }

    internal sealed class Hotkeys : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private readonly int[] ids = new int[3];
        private readonly Keys[] keys = new Keys[3];
        private readonly Action<PasteShortcut> pressed;

        internal Hotkeys(Action<PasteShortcut> pressed)
        {
            this.pressed = pressed;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        internal string Set(PasteShortcut shortcut, Keys chord, string savePath = null)
        {
            int slot = (int)shortcut;
            if (!Valid(chord)) return "请用 Ctrl、Alt 或 Shift 搭配字母、数字或功能键。Ctrl + V 留给系统。";
            if (keys[slot] == chord) return null;
            uint mods = 0x4000; // MOD_NOREPEAT
            if ((chord & Keys.Alt) != 0) mods |= 1;
            if ((chord & Keys.Control) != 0) mods |= 2;
            if ((chord & Keys.Shift) != 0) mods |= 4;
            int firstId = 1 + slot * 2;
            int next = ids[slot] == firstId ? firstId + 1 : firstId;
            if (!RegisterHotKey(Handle, next, mods, (uint)(chord & Keys.KeyCode)))
                return "快捷键已被占用。请录制另一组。";
            try
            {
                if (savePath != null)
                    MobileInbox.SaveSetting(savePath, ((int)chord).ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                UnregisterHotKey(Handle, next);
                return "无法保存设置。请检查用户设置目录。";
            }
            catch (UnauthorizedAccessException)
            {
                UnregisterHotKey(Handle, next);
                return "无法保存设置。请检查用户设置目录。";
            }
            if (ids[slot] != 0) UnregisterHotKey(Handle, ids[slot]);
            keys[slot] = chord;
            ids[slot] = next;
            return null;
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
            Keys key = chord & Keys.KeyCode;
            return ((chord & Keys.Control) != 0 ? "Ctrl + " : "") +
                ((chord & Keys.Alt) != 0 ? "Alt + " : "") +
                ((chord & Keys.Shift) != 0 ? "Shift + " : "") +
                (key >= Keys.D0 && key <= Keys.D9 ? (key - Keys.D0).ToString() : key.ToString());
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312)
                for (int i = 0; i < ids.Length; i++)
                    if (ids[i] != 0 && message.WParam.ToInt32() == ids[i])
                    {
                        pressed((PasteShortcut)i);
                        return;
                    }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            foreach (int id in ids) if (id != 0) UnregisterHotKey(Handle, id);
            DestroyHandle();
        }
    }

    internal sealed class PacedInput : IDisposable
    {
        private readonly Timer timer = new Timer();
        private readonly Action<string> status;
        private readonly Func<IntPtr> foreground;
        private readonly Action<string> send;
        private string pending;
        private IntPtr target;
        private int position, interval;
        private PasteShortcut source;
        private bool running;

        internal PacedInput(Action<string> status, Func<IntPtr> foreground, Action<string> send)
        {
            this.status = status;
            this.foreground = foreground;
            this.send = send;
            timer.Tick += Tick;
        }

        internal bool Begin(PasteShortcut shortcut, string text, int delay, IntPtr window)
        {
            if (running || String.IsNullOrEmpty(text) || window == IntPtr.Zero) return false;
            target = window;
            source = shortcut;
            pending = text;
            position = 0;
            interval = delay;
            StartTimer("正在输入。再按同一个粘贴快捷键可暂停。");
            return true;
        }

        internal bool HandleRunningPasteShortcut(PasteShortcut shortcut)
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
            status(message);
        }

        private void Finish(string message)
        {
            pending = null;
            target = IntPtr.Zero;
            Pause(message);
        }

        private void Tick(object sender, EventArgs args)
        {
            timer.Stop();
            if (!running) return;
            try
            {
                int count = interval == 0 ? 128 : 1;
                while (count-- > 0 && position < pending.Length)
                {
                    if (foreground() != target)
                    {
                        Pause("已切换窗口，输入已暂停。回到原窗口后可继续。");
                        return;
                    }
                    int start = position++;
                    char c = pending[start];
                    if (c == '\r' || c == '\n')
                    {
                        if (c == '\r' && position < pending.Length && pending[position] == '\n') position++;
                        send("\n");
                    }
                    else
                    {
                        if (Char.IsHighSurrogate(c) && position < pending.Length && Char.IsLowSurrogate(pending[position]))
                            position++;
                        send(pending.Substring(start, position - start));
                    }
                }
                if (position == pending.Length) Finish("本次输入已完成。");
                else
                {
                    timer.Interval = Math.Max(interval, 1);
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                Finish("输入失败。 " + ex.Message);
            }
        }

        public void Dispose() { timer.Dispose(); }
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
