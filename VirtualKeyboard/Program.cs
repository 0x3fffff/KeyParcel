using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using HIDMaestro;

internal static partial class Program
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(IntPtr pipe, out uint process);
    [LibraryImport("user32.dll")] private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [LibraryImport("user32.dll")] private static partial IntPtr GetKeyboardLayout(uint thread);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll")] private static partial short GetKeyState(int key);
    [LibraryImport("kernel32.dll")] private static partial IntPtr OpenProcess(uint access, int inherit, uint process);
    [LibraryImport("advapi32.dll")] private static partial int OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [LibraryImport("advapi32.dll")] private static partial int GetTokenInformation(IntPtr token, int kind, out int value, int size, out int actualSize);
    [LibraryImport("kernel32.dll")] private static partial int CloseHandle(IntPtr handle);

    private const string Profile = "keychron-q6";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--probe"])
            {
                using var probe = new HMContext();
                probe.LoadDefaultProfiles();
                if (probe.GetProfile(Profile) == null || !Enumerable.Range(32, 95).All(c => TryMap((char)c, false, out _, out _))) return 1;
                return IsUsKeyboardLayout(0x04090409u) && IsUsKeyboardLayout(0x04090804u) &&
                    !IsUsKeyboardLayout(0x08040804u) && !IsUsKeyboardLayout(0xF0020409u) ? 0 : 3;
            }
            if (args.Length != 2 || !Guid.TryParseExact(args[0], "N", out var nonce) ||
                !uint.TryParse(args[1], out var callerPid) || callerPid == 0) return 2;

            using var caller = Process.GetProcessById((int)callerPid);
            if (caller.SessionId != Process.GetCurrentProcess().SessionId) return 2;
            using var identity = WindowsIdentity.GetCurrent();
            var pipeSecurity = new PipeSecurity();
            pipeSecurity.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create("KeyParcel.Keyboard." + nonce.ToString("N"), PipeDirection.InOut,
                1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await pipe.WaitForConnectionAsync(timeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var client) ||
                client != callerPid || caller.HasExited) return 2;

            using var context = new HMContext();
            context.LoadDefaultProfiles();
            context.InstallDriver();
            using var keyboard = context.CreateController(context.GetProfile(Profile) ?? throw new InvalidOperationException("Keyboard profile missing."));
            pipe.WriteByte(1);
            using var input = new BinaryReader(pipe);
            while (true)
            {
                long target;
                try { target = input.ReadInt64(); }
                catch (EndOfStreamException) { return 0; }
                if (caller.HasExited) return 0;
                var character = (char)input.ReadUInt16();
                bool rimeKey = character is >= '\uE000' and <= '\uE00B';
                var result = CheckTarget(new IntPtr(target), rimeKey);
                if (result == 1 && TryMap(character, (GetKeyState(0x14) & 1) != 0, out var key, out var shift))
                {
                    try { keyboard.SubmitKeyboard(shift ? HMKeyModifiers.LeftShift : HMKeyModifiers.None, [key]); }
                    finally { keyboard.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty); }
                }
                else if (result == 1) result = 0;
                pipe.WriteByte(result);
            }
        }
        catch (Exception) { return 1; }
    }

    private static byte CheckTarget(IntPtr window, bool rimeKey)
    {
        if (window == IntPtr.Zero || GetForegroundWindow() != window) return 0;
        var thread = GetWindowThreadProcessId(window, out var pid);
        var process = OpenProcess(0x1000, 0, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return 0;
        try
        {
            if (OpenProcessToken(process, 8, out var token) == 0) return 0; // TOKEN_QUERY
            try
            {
                if (GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) == 0 || elevated != 0) return 0;
                using var targetUser = new WindowsIdentity(token);
                using var user = WindowsIdentity.GetCurrent();
                if (!Equals(targetUser.User, user.User)) return 0;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
        if (!rimeKey && !IsUsKeyboardLayout(unchecked((uint)GetKeyboardLayout(thread).ToInt64()))) return 2;
        foreach (var modifier in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(modifier) & 0x8000) != 0) return 3;
        return GetForegroundWindow() == window ? (byte)1 : (byte)0;
    }

    // The low word is the input language. Chinese can use the same US physical layout.
    private static bool IsUsKeyboardLayout(uint layout) => (layout >> 16) == 0x0409u;

    private static bool TryMap(char ch, bool caps, out HMKey key, out bool shift)
    {
        shift = false;
        if (ch is >= '\uE000' and <= '\uE00B')
        {
            key = (HMKey)(0x68 + ch - '\uE000');
            return true;
        }
        if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
        {
            shift = char.IsUpper(ch) != caps;
            key = (HMKey)(4 + char.ToLowerInvariant(ch) - 'a');
            return true;
        }
        const string plain = "1234567890\n    -=[]\\ ;'`,./";
        const string shifted = "!@#$%^&*()     _+{}| :\"~<>?";
        if (ch == ' ') { key = (HMKey)0x2c; return true; }
        var index = plain.IndexOf(ch);
        if (index < 0) { index = shifted.IndexOf(ch); shift = index >= 0; }
        key = (HMKey)(0x1e + index);
        return index >= 0;
    }
}
