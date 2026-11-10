using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyParcel
{
    internal sealed class RimeFocusLostException : InvalidOperationException { }

    internal sealed class RimeInput
    {
        private readonly Action<string> send;
        private readonly IntPtr target;
        private readonly string request, reply;
        private Mutex gate;
        private InputProfiles.Profile original;
        private bool restore;
        private string nonce;
        private int sequence;

        internal RimeInput(string folder, IntPtr target, Action<string> send)
        {
            this.target = target;
            this.send = send;
            request = Path.Combine(folder, "rime-request.txt");
            reply = Path.Combine(folder, "rime-reply.txt");
        }

        internal void Start()
        {
            gate = new Mutex(false, @"Local\KeyParcel.RimeInput");
            bool acquired;
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                gate.Dispose();
                gate = null;
                throw new InvalidOperationException("另一轮中文输入正在进行。");
            }
            using (var profiles = new InputProfiles())
            {
                original = profiles.Active();
                var weasel = profiles.Weasel();
                if (original.SameAs(weasel) == false)
                {
                    restore = true;
                    profiles.Activate(weasel);
                }
                if (!profiles.Active().SameAs(weasel))
                    throw new InvalidOperationException("无法确认小狼毫已启用。请检查 Windows 输入法设置。");
            }
            nonce = Guid.NewGuid().ToString("N");
            sequence = 0;
            MobileInbox.SaveSetting(request, nonce);
            RequireFocus();
            send("\uE00B"); // F24 arms the Rime processor and asks for an acknowledgement.
            WaitForReply(true);
        }

        internal void Send(string unit)
        {
            using (var profiles = new InputProfiles())
                if (!profiles.Active().SameAs(profiles.Weasel()))
                    throw new InvalidOperationException("当前输入法已改变，已停止中文输入。");
            foreach (char key in Encode(Char.ConvertToUtf32(unit, 0)))
            {
                RequireFocus();
                send(key.ToString());
            }
            sequence++;
            // Wait for the last key's receipt even if focus changes after submission.
            // Without that receipt the character cannot safely be retried.
            WaitForReply(false);
        }

        // Six base-11 digits fit every Unicode scalar and use only F13 through F23.
        internal static char[] Encode(int scalar)
        {
            var keys = new char[6];
            for (int i = keys.Length - 1; i >= 0; i--)
            {
                keys[i] = (char)(0xE000 + scalar % 11);
                scalar /= 11;
            }
            return keys;
        }

        private void RequireFocus()
        {
            if (WindowsKeyboard.GetForegroundWindow() != target)
                throw new RimeFocusLostException();
        }

        private void WaitForReply(bool requireFocus)
        {
            string expected = nonce + ":" + sequence.ToString(CultureInfo.InvariantCulture);
            var elapsed = Stopwatch.StartNew();
            do
            {
                if (requireFocus) RequireFocus();
                try { if (ConfirmationReceived(reply, expected)) return; }
                catch (IOException) { }
                Thread.Sleep(10);
            } while (elapsed.ElapsedMilliseconds < 500);
            throw new InvalidOperationException(sequence == 0
                ? "未收到小狼毫确认。请安装并部署 KeyParcel 中文扩展，结束当前输入法组字后重试。"
                : "小狼毫未确认上一字符，输入已停止。");
        }

        internal static bool ConfirmationReceived(string path, string expected)
        {
            // Weasel rewrites this file after committing each character. Polling must allow that write.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd() == expected;
        }

        internal void Stop()
        {
            if (gate == null) return;
            try
            {
                if (nonce != null)
                {
                    File.Delete(request);
                    File.Delete(reply);
                    nonce = null;
                }
            }
            finally
            {
                try
                {
                    if (restore)
                    {
                        restore = false;
                        using (var profiles = new InputProfiles())
                        {
                            if (profiles.Active().SameAs(profiles.Weasel()))
                            {
                                profiles.Activate(original);
                                if (!profiles.Active().SameAs(original))
                                    throw new InvalidOperationException("未能确认原输入法已恢复，请用 Win + 空格检查。");
                            }
                        }
                    }
                }
                finally
                {
                    gate.ReleaseMutex();
                    gate.Dispose();
                    gate = null;
                }
            }
        }
    }

    internal sealed class InputProfiles : IDisposable
    {
        private static readonly Guid ProfilesClass = new Guid("33C53A50-F456-4884-B049-85FD643ECFED");
        private static readonly Guid KeyboardCategory = new Guid("34745C63-B2F0-4784-8B67-5E12C8701A31");
        private static readonly Guid WeaselClass = new Guid("A3F4CDED-B1E9-41EE-9CA6-7B4D0DE6CB0A");
        private static readonly Guid WeaselProfile = new Guid("3D02CAB6-2B8E-4781-BA20-1C9267529467");
        private readonly IProfiles manager = (IProfiles)Activator.CreateInstance(Type.GetTypeFromCLSID(ProfilesClass));

        [StructLayout(LayoutKind.Sequential)]
        internal struct Profile
        {
            internal uint Type;
            internal ushort Language;
            internal Guid ClassId, ProfileId, Category;
            internal IntPtr Substitute;
            internal uint Caps;
            internal IntPtr Layout;
            internal uint Flags;

            internal bool SameAs(Profile other)
            {
                return Type == other.Type && Language == other.Language &&
                    (Type == 1 ? ClassId == other.ClassId && ProfileId == other.ProfileId : Layout == other.Layout);
            }
        }

        [ComImport, Guid("71C6E74C-0F28-11D8-A82A-00065B84435C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IProfiles
        {
            [PreserveSig] int ActivateProfile(uint type, ushort language, ref Guid clsid, ref Guid profile, IntPtr layout, uint flags);
            void DeactivateProfile();
            [PreserveSig] int GetProfile(uint type, ushort language, ref Guid clsid, ref Guid profile, IntPtr layout, out Profile value);
            void EnumProfiles();
            void ReleaseInputProcessor();
            void RegisterProfile();
            void UnregisterProfile();
            [PreserveSig] int GetActiveProfile(ref Guid category, out Profile value);
        }

        internal Profile Active()
        {
            var category = KeyboardCategory;
            Profile value;
            if (manager.GetActiveProfile(ref category, out value) != 0 || (value.Type != 1 && value.Type != 2))
                throw new InvalidOperationException("无法读取当前输入法，已停止中文输入。");
            return value;
        }

        internal Profile Weasel()
        {
            var clsid = WeaselClass;
            var guid = WeaselProfile;
            Profile value;
            if (manager.GetProfile(1, 0x0804, ref clsid, ref guid, IntPtr.Zero, out value) != 0 || (value.Flags & 2) == 0)
                throw new InvalidOperationException("请先安装并启用小狼毫（简体中文），再部署 KeyParcel 中文扩展。");
            return value;
        }

        internal void Activate(Profile profile)
        {
            IntPtr layout = profile.Type == 2 ? profile.Layout : IntPtr.Zero;
            int result = manager.ActivateProfile(profile.Type, profile.Language, ref profile.ClassId, ref profile.ProfileId, layout, 0x20000000);
            if (result != 0) throw new InvalidOperationException("Windows 未能切换输入法，请用 Win + 空格检查。");
        }

        public void Dispose() { Marshal.FinalReleaseComObject(manager); }
    }
}
