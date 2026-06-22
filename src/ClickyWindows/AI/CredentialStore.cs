using System.Runtime.InteropServices;
using System.Text;
using ClickyWindows.Interop;

namespace ClickyWindows.AI;

/// <summary>
/// Stores and retrieves API keys using Windows Credential Manager.
/// Keys are never written to disk in plaintext.
/// </summary>
public static class CredentialStore
{
    public const string GeminiTarget = "ClickyWindows/GeminiApiKey";
    public const string OpenAiTarget = "ClickyWindows/OpenAiApiKey";

    /// <summary>Returns the stored secret for <paramref name="target"/>, or null if not found.</summary>
    public static string? GetKey(string target)
    {
        if (!NativeMethods.CredRead(target, NativeMethods.CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            NativeMethods.CredFree(credPtr);
        }
    }

    /// <summary>Saves <paramref name="secret"/> to Windows Credential Manager under <paramref name="target"/>.</summary>
    public static void SaveKey(string target, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var cred = new NativeMethods.CREDENTIAL
            {
                Type             = NativeMethods.CRED_TYPE_GENERIC,
                TargetName       = target,
                Comment          = "ClickyWindows API key",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob   = handle.AddrOfPinnedObject(),
                Persist          = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
                UserName         = Environment.UserName
            };

            if (!NativeMethods.CredWrite(ref cred, 0))
                throw new InvalidOperationException(
                    $"CredWrite failed for '{target}': error {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Credential Manager target holding the API key for the given provider.</summary>
    public static string TargetForProvider(string provider) =>
        provider?.Trim().Equals("openai", StringComparison.OrdinalIgnoreCase) == true
            ? OpenAiTarget
            : GeminiTarget;

    /// <summary>Returns true when the selected provider's API key is present in Credential Manager.</summary>
    public static bool HasKeyForProvider(string provider) =>
        !string.IsNullOrWhiteSpace(GetKey(TargetForProvider(provider)));
}
