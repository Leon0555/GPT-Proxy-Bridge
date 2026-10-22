using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GPTProxyBridge.Core;

public sealed class ConfigurationStore(string directory)
{
    public string DirectoryPath => directory;
    readonly JsonSerializerOptions options = new() { WriteIndented = true };
    public Configuration Load()
    {
        var path = Path.Combine(directory, "profiles.json");
        if (!File.Exists(path)) return new();
        var data = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path), options) ?? throw new IOException("配置文件为空。");
        if (data.Version != 1) throw new IOException("配置格式版本不兼容，请保留原文件。");
        return data;
    }
    public void Save(Configuration data)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profiles.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, options), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
    public string SavePassword(Guid id, string password)
    {
        Directory.CreateDirectory(directory);
        string reference = id.ToString("N") + ".secret";
        var path = Path.Combine(directory, reference);
        File.WriteAllBytes(path + ".tmp", Protect(Encoding.UTF8.GetBytes(password), true));
        File.Move(path + ".tmp", path, true);
        return reference;
    }
    public string ReadPassword(ProxyProfile profile)
    {
        if (!profile.Authentication) return "";
        if (profile.CredentialReference != profile.Id.ToString("N") + ".secret") throw new IOException("配置的凭据引用无效。");
        return Encoding.UTF8.GetString(Protect(File.ReadAllBytes(Path.Combine(directory, profile.CredentialReference)), false));
    }
    public void DeletePassword(Guid id)
    {
        var path = Path.Combine(directory, id.ToString("N") + ".secret");
        if (File.Exists(path)) File.Delete(path);
    }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr ptr);
    static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = encrypt ? CryptProtectData(ref input, "GPT Proxy Bridge", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法访问当前 Windows 用户的加密凭据。");
            byte[] result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        } finally { Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); if(output.Data != IntPtr.Zero) LocalFree(output.Data); Array.Clear(bytes); }
    }
}
