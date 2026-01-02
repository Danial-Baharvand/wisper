using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services;

/// <summary>
/// Manages secure storage of API keys using Windows Credential Manager.
/// Supports OpenAI, Deepgram, and Cerebras API keys.
/// </summary>
public static class CredentialManager
{
    private const string CredentialTarget = "WisperFlow_OpenAI_ApiKey";
    private const string DeepgramCredentialTarget = "WisperFlow_Deepgram_ApiKey";
    private const string CerebrasCredentialTarget = "WisperFlow_Cerebras_ApiKey";
    private const string GroqCredentialTarget = "WisperFlow_Groq_ApiKey";

    /// <summary>
    /// Saves the API key to Windows Credential Manager.
    /// </summary>
    public static bool SaveApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            DeleteApiKey();
            return true;
        }

        var credentialBlob = Encoding.Unicode.GetBytes(apiKey);

        var credential = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = CredentialTarget,
            CredentialBlobSize = (uint)credentialBlob.Length,
            CredentialBlob = Marshal.AllocHGlobal(credentialBlob.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            UserName = "WisperFlow"
        };

        try
        {
            Marshal.Copy(credentialBlob, 0, credential.CredentialBlob, credentialBlob.Length);
            return CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(credential.CredentialBlob);
        }
    }

    /// <summary>
    /// Retrieves the API key from Windows Credential Manager.
    /// </summary>
    public static string? GetApiKey()
    {
        if (!CredRead(CredentialTarget, CRED_TYPE_GENERIC, 0, out var credentialPtr))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var credentialBlob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, credentialBlob, 0, (int)credential.CredentialBlobSize);
            
            return Encoding.Unicode.GetString(credentialBlob);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    /// <summary>
    /// Deletes the API key from Windows Credential Manager.
    /// </summary>
    public static bool DeleteApiKey()
    {
        return CredDelete(CredentialTarget, CRED_TYPE_GENERIC, 0);
    }

    /// <summary>
    /// Checks if an API key is stored (either in Credential Manager or environment).
    /// </summary>
    public static bool HasApiKey()
    {
        // Check environment variable first
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return true;
        }

        // Check Credential Manager
        var storedKey = GetApiKey();
        return !string.IsNullOrWhiteSpace(storedKey);
    }

    // ===== Deepgram API Key Methods =====

    /// <summary>
    /// Saves the Deepgram API key to Windows Credential Manager.
    /// </summary>
    public static bool SaveDeepgramApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            DeleteDeepgramApiKey();
            return true;
        }

        var credentialBlob = Encoding.Unicode.GetBytes(apiKey);

        var credential = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = DeepgramCredentialTarget,
            CredentialBlobSize = (uint)credentialBlob.Length,
            CredentialBlob = Marshal.AllocHGlobal(credentialBlob.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            UserName = "WisperFlow"
        };

        try
        {
            Marshal.Copy(credentialBlob, 0, credential.CredentialBlob, credentialBlob.Length);
            return CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(credential.CredentialBlob);
        }
    }

    /// <summary>
    /// Retrieves the Deepgram API key from Windows Credential Manager.
    /// </summary>
    public static string? GetDeepgramApiKey()
    {
        if (!CredRead(DeepgramCredentialTarget, CRED_TYPE_GENERIC, 0, out var credentialPtr))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var credentialBlob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, credentialBlob, 0, (int)credential.CredentialBlobSize);
            
            return Encoding.Unicode.GetString(credentialBlob);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    /// <summary>
    /// Deletes the Deepgram API key from Windows Credential Manager.
    /// </summary>
    public static bool DeleteDeepgramApiKey()
    {
        return CredDelete(DeepgramCredentialTarget, CRED_TYPE_GENERIC, 0);
    }

    /// <summary>
    /// Checks if a Deepgram API key is stored (either in Credential Manager or environment).
    /// </summary>
    public static bool HasDeepgramApiKey()
    {
        // Check environment variable first
        var envKey = Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return true;
        }

        // Check Credential Manager
        var storedKey = GetDeepgramApiKey();
        return !string.IsNullOrWhiteSpace(storedKey);
    }

    // ===== Cerebras API Key Methods =====

    /// <summary>
    /// Saves the Cerebras API key to Windows Credential Manager.
    /// </summary>
    public static bool SaveCerebrasApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            DeleteCerebrasApiKey();
            return true;
        }

        var credentialBlob = Encoding.Unicode.GetBytes(apiKey);

        var credential = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = CerebrasCredentialTarget,
            CredentialBlobSize = (uint)credentialBlob.Length,
            CredentialBlob = Marshal.AllocHGlobal(credentialBlob.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            UserName = "WisperFlow"
        };

        try
        {
            Marshal.Copy(credentialBlob, 0, credential.CredentialBlob, credentialBlob.Length);
            return CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(credential.CredentialBlob);
        }
    }

    /// <summary>
    /// Retrieves the Cerebras API key from Windows Credential Manager.
    /// </summary>
    public static string? GetCerebrasApiKey()
    {
        if (!CredRead(CerebrasCredentialTarget, CRED_TYPE_GENERIC, 0, out var credentialPtr))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var credentialBlob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, credentialBlob, 0, (int)credential.CredentialBlobSize);
            
            return Encoding.Unicode.GetString(credentialBlob);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    /// <summary>
    /// Deletes the Cerebras API key from Windows Credential Manager.
    /// </summary>
    public static bool DeleteCerebrasApiKey()
    {
        return CredDelete(CerebrasCredentialTarget, CRED_TYPE_GENERIC, 0);
    }

    /// <summary>
    /// Checks if a Cerebras API key is stored (either in Credential Manager or environment).
    /// </summary>
    public static bool HasCerebrasApiKey()
    {
        // Check environment variable first
        var envKey = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return true;
        }

        // Check Credential Manager
        var storedKey = GetCerebrasApiKey();
        return !string.IsNullOrWhiteSpace(storedKey);
    }

    // ===== Groq API Key Management =====

    /// <summary>
    /// Saves the Groq API key to Windows Credential Manager.
    /// </summary>
    public static bool SaveGroqApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            DeleteGroqApiKey();
            return true;
        }

        var credentialBlob = Encoding.Unicode.GetBytes(apiKey);

        var credential = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = GroqCredentialTarget,
            CredentialBlobSize = (uint)credentialBlob.Length,
            CredentialBlob = Marshal.AllocHGlobal(credentialBlob.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            UserName = "WisperFlow"
        };

        try
        {
            Marshal.Copy(credentialBlob, 0, credential.CredentialBlob, credentialBlob.Length);
            return CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(credential.CredentialBlob);
        }
    }

    /// <summary>
    /// Retrieves the Groq API key from Windows Credential Manager.
    /// </summary>
    public static string? GetGroqApiKey()
    {
        if (!CredRead(GroqCredentialTarget, CRED_TYPE_GENERIC, 0, out var credentialPtr))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    /// <summary>
    /// Deletes the Groq API key from Windows Credential Manager.
    /// </summary>
    public static bool DeleteGroqApiKey()
    {
        return CredDelete(GroqCredentialTarget, CRED_TYPE_GENERIC, 0);
    }

    /// <summary>
    /// Checks if a Groq API key is stored (either in Credential Manager or environment).
    /// </summary>
    public static bool HasGroqApiKey()
    {
        // Check environment variable first
        var envKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return true;
        }

        // Check Credential Manager
        var storedKey = GetGroqApiKey();
        return !string.IsNullOrWhiteSpace(storedKey);
    }

    #region Native Methods

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr buffer);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    #endregion
}

