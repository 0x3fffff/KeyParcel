using System;
using System.Threading.Tasks;

namespace KeyParcel
{
    internal interface IVirtualKeyboard : IDisposable
    {
        bool IsReady { get; }
        bool NeedsInstallConfirmation { get; }
        Task<string> Start();
        void Close();
        void Send(IntPtr window, string unit);
    }

    internal sealed class InputModes : IDisposable
    {
        private readonly IVirtualKeyboard keyboard;
        private readonly string folder;
        internal bool Virtual { get; private set; }

        internal InputModes(IVirtualKeyboard keyboard, string folder = null) { this.keyboard = keyboard; this.folder = folder; }

        internal async Task<string> Toggle(Func<bool> confirmInstall)
        {
            if (Virtual)
            {
                keyboard.Close();
                Virtual = false;
                return null;
            }
            if (keyboard == null) return "此版本缺少虚拟键盘组件，仍使用模拟输入。";
            if (!keyboard.IsReady)
            {
                if (keyboard.NeedsInstallConfirmation && !confirmInstall()) return "已取消切换，仍使用模拟输入。";
                string error = await keyboard.Start();
                if (error != null) return error;
                if (!keyboard.IsReady) return "虚拟键盘未就绪。";
            }
            Virtual = true;
            return null;
        }

        internal string Prepare(string text, IntPtr window, out Action<string> send, out RimeInput rime)
        {
            send = null;
            rime = null;
            if (!Virtual) return null;
            if (!keyboard.IsReady) return "虚拟键盘驱动未就绪。请检查驱动，或手动切回模拟输入。";
            send = unit => keyboard.Send(window, unit);
            bool unicode = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n' || (c >= ' ' && c <= '~')) continue;
                if (Char.IsControl(c) || Char.IsLowSurrogate(c) ||
                    (Char.IsHighSurrogate(c) && (i + 1 == text.Length || !Char.IsLowSurrogate(text[i + 1]))))
                {
                    send = null;
                    return "这段文字含有不支持的控制字符或不完整的 Unicode 字符。";
                }
                if (Char.IsHighSurrogate(c)) i++;
                unicode = true;
            }
            if (unicode)
            {
                if (folder == null) { send = null; return "虚拟键盘中文输入需要 KeyParcel 小狼毫扩展。"; }
                rime = new RimeInput(folder, window, send);
                send = rime.Send;
            }
            return null;
        }

        public void Dispose() { if (keyboard != null) keyboard.Dispose(); }
    }
}
