using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WowVR.Net;

/// <summary>
/// Keeps a remembered password where only this user can read it, in each system's own store:
/// DPAPI on Windows, the Keychain on macOS, and the Secret Service (GNOME Keyring, KWallet)
/// through <c>secret-tool</c> on Linux. Without one, <see cref="IsAvailable"/> is false and
/// nothing is kept. Each password is found by a key, such as the server and account.
/// </summary>
public abstract class CredentialStore
{
    public const string Service = "wow-vr";

    /// <summary>The store for this system. Windows keeps its encrypted files in <paramref name="windowsFolder"/>.</summary>
    public static CredentialStore ForThisSystem(string windowsFolder)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsStore(windowsFolder);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacStore();
        }

        return OperatingSystem.IsLinux() ? new SecretToolStore() : new NoStore();
    }

    public abstract bool IsAvailable { get; }

    /// <summary>A name for the store, to say where a password goes.</summary>
    public abstract string Name { get; }

    public abstract void Save(string key, string password);

    public abstract string? Load(string key);

    public abstract void Delete(string key);

    private sealed class NoStore : CredentialStore
    {
        public override bool IsAvailable => false;

        public override string Name => "nowhere";

        public override void Save(string key, string password)
        {
        }

        public override string? Load(string key) => null;

        public override void Delete(string key)
        {
        }
    }

    /// <summary>DPAPI, bound to the Windows user, with the encrypted bytes in a file per key.</summary>
    private sealed class WindowsStore(string folder) : CredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(Service);

        public override bool IsAvailable => true;

        public override string Name => "Windows' data protection";

        private string PathOf(string key) =>
            Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".bin");

        public override void Save(string key, string password)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(PathOf(key), Protect(Encoding.UTF8.GetBytes(password), true));
        }

        public override string? Load(string key)
        {
            var path = PathOf(key);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(Protect(File.ReadAllBytes(path), false));
            }
            catch (CryptographicException)
            {
                // Saved by another user or machine.
                return null;
            }
        }

        public override void Delete(string key)
        {
            var path = PathOf(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static byte[] Protect(byte[] data, bool encrypt)
        {
            var input = Blob.From(data);
            var entropy = Blob.From(Entropy);
            try
            {
                var ok = encrypt
                    ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok)
                {
                    throw new CryptographicException(Marshal.GetLastPInvokeError());
                }

                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, result.Length);
                    return result;
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                input.Free();
                entropy.Free();
            }
        }

        private const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Data;

            public static Blob From(byte[] bytes)
            {
                var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1)) };
                Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
                return blob;
            }

            public readonly void Free() => Marshal.FreeHGlobal(Data);
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }

    /// <summary>The login keychain, through the Security framework's generic passwords.</summary>
    private sealed class MacStore : CredentialStore
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int ItemNotFound = -25300;

        public override bool IsAvailable => true;

        public override string Name => "the macOS Keychain";

        public override void Save(string key, string password)
        {
            Delete(key);
            var service = Encoding.UTF8.GetBytes(Service);
            var account = Encoding.UTF8.GetBytes(key);
            var secret = Encoding.UTF8.GetBytes(password);
            var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, (uint)secret.Length, secret, IntPtr.Zero);
            if (status != 0)
            {
                throw new IOException($"The Keychain didn't keep the password (status {status}).");
            }
        }

        public override string? Load(string key)
        {
            var service = Encoding.UTF8.GetBytes(Service);
            var account = Encoding.UTF8.GetBytes(key);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out var length, out var data, out var item);
            if (status != 0)
            {
                return null;
            }

            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                CFRelease(item);
            }
        }

        public override void Delete(string key)
        {
            var service = Encoding.UTF8.GetBytes(Service);
            var account = Encoding.UTF8.GetBytes(key);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out _, out var data, out var item);
            if (status == ItemNotFound || status != 0)
            {
                return;
            }

            SecKeychainItemFreeContent(IntPtr.Zero, data);
            SecKeychainItemDelete(item);
            CFRelease(item);
        }

        [DllImport(Security)]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr item);

        [DllImport(Security)]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, out IntPtr item);

        [DllImport(Security)]
        private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

        [DllImport(Security)]
        private static extern int SecKeychainItemDelete(IntPtr item);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr item);
    }

    /// <summary>
    /// The Secret Service, through libsecret's <c>secret-tool</c>. The password goes on its input,
    /// so it's never on a command line other processes can see.
    /// </summary>
    private sealed class SecretToolStore : CredentialStore
    {
        private bool? _available;

        public override bool IsAvailable => _available ??= Run(["lookup", "service", Service, "probe", "probe"], null, out _) is not null;

        public override string Name => "the system keyring";

        public override void Save(string key, string password)
        {
            if (Run(["store", $"--label={Service}: {key}", "service", Service, "account", key], password, out var error) != 0)
            {
                throw new IOException($"The keyring didn't keep the password: {error}");
            }
        }

        public override string? Load(string key) =>
            Run(["lookup", "service", Service, "account", key], null, out var output) == 0 && output.Length > 0 ? output.TrimEnd('\n') : null;

        public override void Delete(string key) => Run(["clear", "service", Service, "account", key], null, out _);

        /// <summary>The exit code, or null when secret-tool isn't installed. Output is stdout on success, stderr otherwise.</summary>
        private static int? Run(string[] arguments, string? input, out string output)
        {
            output = "";
            var start = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(start)!;
                if (input is not null)
                {
                    process.StandardInput.Write(input);
                }

                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10_000))
                {
                    process.Kill();
                    output = "it didn't answer";
                    return -1;
                }

                output = process.ExitCode == 0 ? stdout.Result : stderr.Result.Trim();
                return process.ExitCode;
            }
            catch (Win32Exception)
            {
                return null;
            }
        }
    }
}
