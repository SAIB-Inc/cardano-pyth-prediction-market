using PredictionMarket.Config;

namespace PredictionMarket.Services;

/// Shared resolve → claim → create logic used by both CLI and Bot.
public class MarketCycleService(MarketService marketService, WalletService wallet, AppSettings settings)
{
    public string? ActiveMarketTxHash { get; private set; }
    public ulong ActiveMarketIndex { get; private set; }
    public string? ActivePolicyId { get; private set; }

    /// Run one full cycle: resolve previous → claim → create new market.
    /// Returns the new market's policy ID.
    public async Task<string> RunCycle(string feedId, ulong seedLovelace)
    {
        // 1. If previous market exists, resolve + claim
        if (ActiveMarketTxHash is not null && ActivePolicyId is not null)
        {
            Console.WriteLine("\n── Resolving previous market ──");
            string resolveTxHash = await marketService.ResolveMarket(
                ActiveMarketTxHash, ActiveMarketIndex, ActivePolicyId);

            // Wait for resolve to confirm, then find the resolved UTxO
            var resolvedUtxo = await wallet.WaitForUtxo(
                GetScriptAddress(ActivePolicyId), resolveTxHash)
                ?? throw new InvalidOperationException("Resolved UTxO not found!");

            Console.WriteLine("\n── Claiming creator winnings ──");
            // TODO: Determine actual burn amount from wallet token balance
            // For now claim all creator tokens (seed amount)
            await marketService.Claim(
                resolveTxHash, resolvedUtxo.Outref.Index, ActivePolicyId,
                (long)seedLovelace);
        }

        // 2. Create new market
        Console.WriteLine("\n── Creating new market ──");
        var (txHash, index, policyId) = await marketService.CreateMarket(feedId, seedLovelace);

        ActiveMarketTxHash = txHash;
        ActiveMarketIndex = index;
        ActivePolicyId = policyId;

        return policyId;
    }

    /// Get the next 5-minute boundary from now.
    public static DateTimeOffset GetNext5MinBoundary(DateTimeOffset now)
    {
        int minutes = (now.Minute / 5 + 1) * 5;
        var baseTime = new DateTimeOffset(now.Year, now.Month, now.Day,
            now.Hour, 0, 0, now.Offset);
        return baseTime.AddMinutes(minutes);
    }

    private string GetScriptAddress(string policyId)
    {
        var networkType = Enum.Parse<Chrysalis.Wallet.Models.Enums.NetworkType>(settings.Network);
        return Chrysalis.Wallet.Models.Addresses.Address.FromScriptHash(networkType, policyId).ToBech32();
    }
}
