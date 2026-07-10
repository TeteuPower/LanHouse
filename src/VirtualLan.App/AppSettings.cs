using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VirtualLan.App;

/// <summary>
/// Preferências persistidas entre execuções, em %APPDATA%\VirtualLan\settings.json.
///
/// A senha só é guardada se o usuário marcar "lembrar", e mesmo assim protegida com DPAPI
/// (chave do usuário do Windows) — o arquivo não guarda a senha em texto puro.
/// </summary>
internal sealed class AppSettings
{
    public bool HostMode { get; set; }
    public string Relay { get; set; } = "";
    public int HostPort { get; set; } = 7777;
    public string Network { get; set; } = "";
    public string Adapter { get; set; } = "";
    public bool RememberPassword { get; set; }
    public string ProtectedPassword { get; set; } = "";
    public bool Verbose { get; set; }

    private static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VirtualLan");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Arquivo corrompido/ilegível: começa do zero em vez de travar a abertura.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Não conseguir salvar preferências nunca deve impedir o uso do app.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Senha em claro recuperada do blob protegido, ou null se não há/lembra.</summary>
    public string? GetPassword()
    {
        if (!RememberPassword || string.IsNullOrEmpty(ProtectedPassword)) return null;

        try
        {
            byte[] blob = Convert.FromBase64String(ProtectedPassword);
            byte[] clear = ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch
        {
            return null; // protegido em outra máquina/usuário, ou corrompido
        }
    }

    public void SetPassword(string? password)
    {
        if (!RememberPassword || string.IsNullOrEmpty(password))
        {
            ProtectedPassword = "";
            return;
        }

        try
        {
            byte[] clear = Encoding.UTF8.GetBytes(password);
            byte[] blob = ProtectedData.Protect(clear, optionalEntropy: null, DataProtectionScope.CurrentUser);
            ProtectedPassword = Convert.ToBase64String(blob);
        }
        catch
        {
            ProtectedPassword = "";
        }
    }
}
