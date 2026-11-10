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
        internal bool Virtual { get; private set; }

        internal InputModes(IVirtualKeyboard keyboard) { this.keyboard = keyboard; }

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

        internal string Prepare(string text, IntPtr window, out Action<string> send)
        {
            send = null;
            if (!Virtual) return null;
            if (!keyboard.IsReady) return "虚拟键盘驱动未就绪。请检查驱动，或手动切回模拟输入。";
            foreach (char c in text)
                if ((c < ' ' || c > '~') && c != '\r' && c != '\n')
                    return "虚拟键盘仅支持英文、数字、半角标点和换行。请切回模拟输入发送这段文字。";
            send = unit => keyboard.Send(window, unit);
            return null;
        }

        public void Dispose() { if (keyboard != null) keyboard.Dispose(); }
    }
}
