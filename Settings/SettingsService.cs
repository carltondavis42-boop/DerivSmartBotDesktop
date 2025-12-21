using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DerivSmartBotDesktop.Settings
{
    public static class SettingsService
    {
        private sealed class SettingsStore
        {
            public string AppId { get; set; }
            public string Symbol { get; set; }
            public bool IsDemo { get; set; } = true;
            public bool ForwardTestEnabled { get; set; } = false;
            public string ApiTokenProtected { get; set; }
            public string ApiToken { get; set; } // legacy
        }

        private static string GetSettingsPath()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DerivSmartBotDesktop");

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            return Path.Combine(folder, "settings.json");
        }

        public static AppSettings Load()
        {
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path))
                    return new AppSettings();

                var json = File.ReadAllText(path);
                var store = JsonSerializer.Deserialize<SettingsStore>(json);
                if (store == null)
                    return new AppSettings();

                var settings = new AppSettings
                {
                    AppId = store.AppId,
                    Symbol = store.Symbol,
                    IsDemo = store.IsDemo,
                    ForwardTestEnabled = store.ForwardTestEnabled
                };

                if (!string.IsNullOrWhiteSpace(store.ApiTokenProtected))
                {
                    settings.ApiToken = UnprotectToken(store.ApiTokenProtected);
                }
                else if (!string.IsNullOrWhiteSpace(store.ApiToken))
                {
                    settings.ApiToken = store.ApiToken;
                }

                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var path = GetSettingsPath();
                var store = new SettingsStore
                {
                    AppId = settings.AppId,
                    Symbol = settings.Symbol,
                    IsDemo = settings.IsDemo,
                    ForwardTestEnabled = settings.ForwardTestEnabled,
                    ApiTokenProtected = ProtectToken(settings.ApiToken)
                };

                var json = JsonSerializer.Serialize(store, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(path, json);
            }
            catch
            {
                // ignore
            }
        }

        private static string ProtectToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(token);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string UnprotectToken(string protectedToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(protectedToken))
                    return string.Empty;

                var bytes = Convert.FromBase64String(protectedToken);
                var unprotectedBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(unprotectedBytes);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
