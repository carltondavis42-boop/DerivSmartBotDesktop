namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Optional explainability payload for each trade.
    /// You can extend and surface this in the UI later.
    /// </summary>
    public sealed class TradeExplanation
    {
        public string StrategyName { get; set; } = string.Empty;
        public MarketRegime Regime { get; set; } = MarketRegime.Unknown;
        public double Confidence { get; set; }
        public string[] TopFeatures { get; set; } = System.Array.Empty<string>();
        public string ReasonText { get; set; } = string.Empty;
    }
}
