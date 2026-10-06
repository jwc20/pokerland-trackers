using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pokerland.Tracker.App;

/// <summary>User settings. The token is encrypted with DPAPI, so only this Windows user can read it.</summary>
internal sealed class AppSettings
{
    /// <summary>The server used until the user saves another: POKERLAND_API_BASE_URL from the .env file the build used (see the .csproj).</summary>
    public static readonly string DefaultApiBaseUrl =
        typeof(AppSettings).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PokerlandApiBaseUrl")?.Value
        ?? throw new InvalidOperationException("Built without PokerlandApiBaseUrl metadata; see Pokerland.Tracker.App.csproj.");

    [JsonPropertyName("api_base_url")] public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
    [JsonPropertyName("token_protected")] public string? TokenProtected { get; set; }
    [JsonPropertyName("extra_roots")] public List<string> ExtraRoots { get; set; } = new();
    [JsonPropertyName("run_at_startup")] public bool RunAtStartup { get; set; } = true;
    [JsonPropertyName("paused")] public bool Paused { get; set; }

    [JsonIgnore]
    public string Token
    {
        get
        {
            if (string.IsNullOrEmpty(TokenProtected)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(TokenProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException)
            {
                return ""; // settings copied from another user or machine
            }
        }
        set => TokenProtected = value == ""
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        if (!File.Exists(Paths.Settings)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings), Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.DataDir);
        var tmp = Paths.Settings + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, Paths.Settings, overwrite: true);
    }

    public IReadOnlyList<string> Roots() =>
        Paths.DefaultRoots().Concat(ExtraRoots).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
