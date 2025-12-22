namespace DerivSmartBotDesktop.Settings
{
    public class AppSettings
    {
        public string AppId { get; set; }
        public string ApiToken { get; set; }
        public string Symbol { get; set; } = "R_100";

        // ✅ NEW: used by SettingsViewModel.cs
        // True  = demo account
        // False = real account
        public bool IsDemo { get; set; } = true;
        public bool ForwardTestEnabled { get; set; } = false;
        public bool RelaxEnvironmentForTesting { get; set; } = false;

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(AppId) &&
            !string.IsNullOrWhiteSpace(ApiToken) &&
            !string.IsNullOrWhiteSpace(Symbol);
    }
}
