namespace DerivSmartBotDesktop.Settings
{
    public class AppSettings
    {
        public string AppId { get; set; }
        public string ApiToken { get; set; }
        public string Symbol { get; set; } = "R_100";
        public string WatchlistCsv { get; set; } =
            "R_10, R_25, R_50, R_75, R_100, 1HZ10V, 1HZ15V, 1HZ25V, 1HZ30V, 1HZ90V, 1HZ100V, 1HZ75V, " +
            "STPRNG, STPRNG2, STPRNG3, STPRNG4, STPRNG5, JD10, JD25, JD50, JD75, JD100";
        public double DailyDrawdownPercent { get; set; } = -1;
        public double MaxDailyLossAmount { get; set; } = -1;
        public int MaxConsecutiveLosses { get; set; } = -1;
        public int TradeCooldownSeconds { get; set; } = -1;

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
