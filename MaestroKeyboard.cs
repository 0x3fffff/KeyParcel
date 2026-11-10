using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KeyParcel
{
    internal sealed class MaestroKeyboard : IVirtualKeyboard
    {
        private readonly string executable;
        private NamedPipeClientStream connection;
        private bool disposed;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint process);

        private MaestroKeyboard(string executable) { this.executable = executable; }

        internal static MaestroKeyboard Create()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keyboard", "KeyParcel.Keyboard.exe");
            return File.Exists(path) ? new MaestroKeyboard(path) : null;
        }

        public bool IsReady { get { return connection != null && connection.IsConnected; } }

        public bool NeedsInstallConfirmation
        {
            get
            {
                try
                {
                    using (var driver = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\HIDMaestro"))
                        return driver == null;
                }
                catch (System.Security.SecurityException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
                catch (IOException) { return true; }
            }
        }

        public async Task<string> Start()
        {
            try
            {
                NamedPipeClientStream pipe = await Task.Run(() => Connect());
                if (disposed) { pipe.Dispose(); return "虚拟键盘已关闭。"; }
                Close();
                connection = pipe;
                return null;
            }
            catch (Win32Exception error)
            {
                return error.NativeErrorCode == 1223 ? "已取消管理员授权，仍使用模拟输入。" : "无法启动虚拟键盘助手。";
            }
            catch (IOException) { return "虚拟键盘未就绪，请检查驱动安装。"; }
            catch (TimeoutException) { return "虚拟键盘启动超时，请重试。"; }
            catch (UnauthorizedAccessException) { return "无法连接虚拟键盘助手，请使用当前账号授权。"; }
        }

        private NamedPipeClientStream Connect()
        {
            string nonce = Guid.NewGuid().ToString("N");
            int caller;
            using (var current = Process.GetCurrentProcess()) caller = current.Id;
            var launch = new ProcessStartInfo(executable, nonce + " " + caller)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(executable),
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var helper = Process.Start(launch))
            {
                if (helper == null) throw new IOException("Keyboard helper did not start.");
                var pipe = new NamedPipeClientStream(".", "KeyParcel.Keyboard." + nonce, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                try
                {
                    pipe.Connect(10000);
                    uint server;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out server) || server != (uint)helper.Id)
                        throw new IOException("Keyboard helper identity did not match.");
                    if (ReadReply(pipe, 90000) != 1) throw new IOException("Keyboard helper failed to create a device.");
                    return pipe;
                }
                catch { pipe.Dispose(); throw; }
            }
        }

        private static int ReadReply(NamedPipeClientStream pipe, int timeout)
        {
            byte[] reply = new byte[1];
            Task<int> read = pipe.ReadAsync(reply, 0, 1);
            if (Task.WhenAny(read, Task.Delay(timeout)).GetAwaiter().GetResult() != read) throw new TimeoutException();
            return read.GetAwaiter().GetResult() == 1 ? reply[0] : -1;
        }

        public void Send(IntPtr window, string unit)
        {
            if (!IsReady) throw new InvalidOperationException("虚拟键盘已断开，请重新切换输入方式。");
            byte[] request = new byte[10];
            Array.Copy(BitConverter.GetBytes(window.ToInt64()), request, 8);
            Array.Copy(BitConverter.GetBytes((ushort)unit[0]), 0, request, 8, 2);
            int result;
            try
            {
                connection.Write(request, 0, request.Length);
                result = ReadReply(connection, 2000);
            }
            catch (IOException) { result = -1; }
            catch (TimeoutException) { result = -1; }
            if (result == 1) return;
            if (result == 2) throw new InvalidOperationException("请在目标窗口按 Win + 空格，选择美式键盘。");
            if (result == 3) throw new InvalidOperationException("请松开 Ctrl、Alt、Shift 和 Win 键再试。");
            if (result < 0) Close();
            throw new InvalidOperationException("虚拟键盘无法向当前窗口输入。");
        }

        public void Close()
        {
            if (connection == null) return;
            connection.Dispose();
            connection = null;
        }

        public void Dispose() { disposed = true; Close(); }
    }
}
