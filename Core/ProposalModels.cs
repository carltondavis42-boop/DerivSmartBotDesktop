using System.Threading;
using System.Threading.Tasks;

namespace DerivSmartBotDesktop.Core
{
    public sealed class ProposalRequest
    {
        public string Symbol { get; set; } = string.Empty;
        public string ContractType { get; set; } = string.Empty;
        public double Stake { get; set; }
        public int Duration { get; set; } = 1;
        public string DurationUnit { get; set; } = "t";
        public string Currency { get; set; } = "USD";
    }

    public sealed class ProposalQuote
    {
        public string Id { get; set; } = string.Empty;
        public double? Payout { get; set; }
        public double? Profit { get; set; }
        public double? AskPrice { get; set; }
    }

    public interface IProposalProvider
    {
        Task<ProposalQuote> GetProposalAsync(ProposalRequest request, CancellationToken cancellationToken = default);
    }
}
