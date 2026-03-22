using System.Text;
using Chrysalis.Codec.Serialization;
using Chrysalis.Codec.Types;
using Chrysalis.Codec.Types.Cardano.Core.Common;
using Chrysalis.Codec.Types.Cardano.Core.Scripts;
using Chrysalis.Codec.Types.Cardano.Core.Transaction;
using Chrysalis.Tx.Builders;
using Chrysalis.Tx.Extensions;
using Chrysalis.Tx.Models;
using Chrysalis.Tx.Models.Cbor;
using Chrysalis.Codec.Extensions.Cardano.Core.Transaction;
using Chrysalis.Wallet.Models.Enums;
using WalletAddress = Chrysalis.Wallet.Models.Addresses.Address;
using PredictionMarket.Config;
using Saib.PredictionMarket.Blueprint;

namespace PredictionMarket.Services;

public class MarketService(WalletService wallet, IPriceService priceService, AppSettings settings)
{
    /// Create a new prediction market with seed ADA.
    /// Returns (txHash, marketUtxoIndex, marketScriptHash).
    public async Task<(string TxHash, ulong Index, string PolicyId)> CreateMarket(
        string feedId, ulong seedLovelace)
    {
        Console.WriteLine($"══ CREATE MARKET ({feedId}, {seedLovelace / 1_000_000} ADA) ══");

        long currentPrice = await priceService.GetCurrentPrice(feedId);
        Console.WriteLine($"  Current price: {currentPrice}");

        // Pick a one-shot UTxO from wallet
        List<ResolvedInput> utxos = await wallet.GetWalletUtxos();
        ResolvedInput oneShot = utxos.First();

        // Apply parameters to get unique policy ID per market
        var oneShotRef = OutputReference.Create(
            PlutusBoundedBytes.Create(oneShot.Outref.TransactionId.ToArray()),
            PlutusInt64.Create((long)oneShot.Outref.Index)
        );
        var marketParams = MarketParams.Create(oneShotRef);

        IScript marketScript = MarketMarketSpend.Script.ApplyParameters(marketParams);
        string policyId = marketScript.HashHex();
        var networkType = Enum.Parse<NetworkType>(settings.Network);
        string scriptAddress = WalletAddress.FromScriptHash(networkType, policyId).ToBech32();

        Console.WriteLine($"  Policy ID: {policyId}");
        Console.WriteLine($"  Script address: {scriptAddress}");

        long seed = (long)seedLovelace;
        long resolutionTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 300_000; // 5 min

        // Build market datum
        var datum = new CborMarketDatum(
            Creator: PlutusBoundedBytes.Create(wallet.PaymentKeyHash),
            OracleScriptHash: PlutusBoundedBytes.Create(Convert.FromHexString(settings.OracleDeployTxHash is not null
                ? GetOracleScriptHash() : "00")),
            OracleNftPolicy: PlutusBoundedBytes.Create(Convert.FromHexString(settings.OracleNftPolicyId ?? "00")),
            FeedId: PlutusBoundedBytes.Create(Encoding.UTF8.GetBytes(feedId)),
            TargetPrice: PlutusInt64.Create(currentPrice),
            Exponent: PlutusInt64.Create(settings.Exponent),
            ResolutionTime: PlutusInt64.Create(resolutionTime),
            TokenPolicy: PlutusBoundedBytes.Create(Convert.FromHexString(policyId)),
            YesReserve: PlutusInt64.Create(seed),
            NoReserve: PlutusInt64.Create(seed),
            K: PlutusInt64.Create(seed * seed),
            TotalYesMinted: PlutusInt64.Create(seed),
            TotalNoMinted: PlutusInt64.Create(seed),
            TotalAda: PlutusInt64.Create(seed),
            Resolved: new CborFalse(),
            WinningSide: new CborNone()
        );

        // Build mint: seed YES + seed NO tokens to creator
        var mintAssets = new Dictionary<string, long>
        {
            ["YES"] = seed,
            ["NO"] = seed,
        };

        // Build the mint redeemer (MintTokens = constr(0))
        var mintRedeemer = new PlutusVoid();

        var txBuilder = new TxBuilder(wallet.Provider);

        ITransaction unsigned = await txBuilder
            .AddUnspentOutputs(utxos)
            .AddInput(oneShot) // consume one-shot for unique policy
            .LockLovelace(scriptAddress, seedLovelace, datum)
            .AddMint(policyId, mintAssets, marketScript, mintRedeemer)
            .SetChangeAddress(wallet.WalletBech32)
            .Complete();

        string txHash = await wallet.SignAndSubmit(unsigned);

        ResolvedInput? marketUtxo = await wallet.WaitForUtxo(scriptAddress, txHash)
            ?? throw new InvalidOperationException("Market UTxO not found!");

        Console.WriteLine($"  Market created: {txHash}#{marketUtxo.Outref.Index}");
        return (txHash, marketUtxo.Outref.Index, policyId);
    }

    /// Place a bet on an existing market.
    public async Task<string> PlaceBet(
        string marketTxHash, ulong marketIndex, string policyId,
        bool betYes, ulong betLovelace)
    {
        string direction = betYes ? "YES" : "NO";
        Console.WriteLine($"══ BET {direction} ({betLovelace / 1_000_000} ADA) ══");

        // Fetch market UTxO
        ResolvedInput marketUtxo = await wallet.Provider.GetUtxoByOutRefAsync(marketTxHash, marketIndex)
            ?? throw new InvalidOperationException("Market UTxO not found!");

        CborMarketDatum datum = marketUtxo.Output.InlineDatum<CborMarketDatum>()
            ?? throw new InvalidOperationException("Market UTxO has no inline datum!");

        long yesReserve = ((PlutusInt64)datum.YesReserve).Value;
        long noReserve = ((PlutusInt64)datum.NoReserve).Value;
        long k = ((PlutusInt64)datum.K).Value;
        long amount = (long)betLovelace;

        // Constant product formula
        long tokensOut;
        CborMarketDatum newDatum;

        if (betYes)
        {
            tokensOut = yesReserve - k / (noReserve + amount);
            newDatum = datum with
            {
                YesReserve = PlutusInt64.Create(yesReserve - tokensOut),
                NoReserve = PlutusInt64.Create(noReserve + amount),
                TotalYesMinted = PlutusInt64.Create(((PlutusInt64)datum.TotalYesMinted).Value + tokensOut),
                TotalAda = PlutusInt64.Create(((PlutusInt64)datum.TotalAda).Value + amount),
            };
        }
        else
        {
            tokensOut = noReserve - k / (yesReserve + amount);
            newDatum = datum with
            {
                YesReserve = PlutusInt64.Create(yesReserve + amount),
                NoReserve = PlutusInt64.Create(noReserve - tokensOut),
                TotalNoMinted = PlutusInt64.Create(((PlutusInt64)datum.TotalNoMinted).Value + tokensOut),
                TotalAda = PlutusInt64.Create(((PlutusInt64)datum.TotalAda).Value + amount),
            };
        }

        Console.WriteLine($"  Tokens out: {tokensOut}");

        // Reconstruct market script for this policy
        IScript marketScript = GetMarketScriptForPolicy(policyId);
        var networkType = Enum.Parse<NetworkType>(settings.Network);
        string scriptAddress = WalletAddress.FromScriptHash(networkType, policyId).ToBech32();

        // Spend redeemer: Bet { direction, amount }
        var spendRedeemer = betYes
            ? (ICborType)new CborBetYes(PlutusInt64.Create(amount))
            : new CborBetNo(PlutusInt64.Create(amount));

        // Mint tokens
        string tokenName = betYes ? "YES" : "NO";
        var mintAssets = new Dictionary<string, long> { [tokenName] = tokensOut };
        var mintRedeemer = new PlutusVoid(); // MintTokens

        long newTotalAda = ((PlutusInt64)datum.TotalAda).Value + amount;

        var txBuilder = new TxBuilder(wallet.Provider);
        List<ResolvedInput> utxos = await wallet.GetWalletUtxos();

        // Set validity before resolution time
        long resolutionTime = ((PlutusInt64)datum.ResolutionTime).Value;
        ulong validUntilSlot = PosixToSlot(resolutionTime);

        ITransaction unsigned = await txBuilder
            .AddUnspentOutputs(utxos)
            .AddInput(marketUtxo, spendRedeemer)
            .LockLovelace(scriptAddress, (ulong)newTotalAda, newDatum)
            .AddMint(policyId, mintAssets, marketScript, mintRedeemer)
            .SetChangeAddress(wallet.WalletBech32)
            .SetValidUntil(validUntilSlot)
            .Complete();

        string txHash = await wallet.SignAndSubmit(unsigned);
        Console.WriteLine($"  Bet placed: {txHash}");
        return txHash;
    }

    /// Resolve a market using oracle price feed.
    public async Task<string> ResolveMarket(string marketTxHash, ulong marketIndex, string policyId)
    {
        Console.WriteLine("══ RESOLVE MARKET ══");

        ResolvedInput marketUtxo = await wallet.Provider.GetUtxoByOutRefAsync(marketTxHash, marketIndex)
            ?? throw new InvalidOperationException("Market UTxO not found!");

        CborMarketDatum datum = marketUtxo.Output.InlineDatum<CborMarketDatum>()
            ?? throw new InvalidOperationException("Market UTxO has no inline datum!");

        string feedId = Encoding.UTF8.GetString(datum.FeedId.Value.Span);

        // Get signed price feed
        SignedPriceFeedData signedFeed = await priceService.GetSignedPriceFeed(feedId);
        Console.WriteLine($"  Oracle price: {signedFeed.Data.Price}");
        Console.WriteLine($"  Target price: {((PlutusInt64)datum.TargetPrice).Value}");

        bool yesWins = signedFeed.Data.Price > ((PlutusInt64)datum.TargetPrice).Value;
        Console.WriteLine($"  Winner: {(yesWins ? "YES" : "NO")}");

        // Build resolved datum
        var winningSide = yesWins ? (ICborType)new CborSomeYes() : new CborSomeNo();
        var resolvedDatum = datum with
        {
            Resolved = new CborTrue(),
            WinningSide = winningSide,
        };

        // Build SignedPriceFeed CBOR for oracle redeemer
        var oracleRedeemer = new CborSignedPriceFeed(
            new CborPriceFeed(
                PlutusInt64.Create(signedFeed.Data.Price),
                PlutusBoundedBytes.Create(Encoding.UTF8.GetBytes(signedFeed.Data.FeedName)),
                PlutusInt64.Create(signedFeed.Data.Timestamp)
            ),
            PlutusBoundedBytes.Create(signedFeed.Signature)
        );

        var spendRedeemer = new CborResolveAction(); // Resolve = constr(1)

        var networkType = Enum.Parse<NetworkType>(settings.Network);
        string scriptAddress = WalletAddress.FromScriptHash(networkType, policyId).ToBech32();

        // Oracle reward address (script stake credential)
        string oracleScriptHash = Convert.ToHexStringLower(datum.OracleScriptHash.Value.Span);
        string oracleRewardAddr = BuildRewardAddress(networkType, oracleScriptHash);

        // Get oracle deploy UTxO for reference input
        ResolvedInput oracleDeployUtxo = await wallet.Provider.GetUtxoByOutRefAsync(
            settings.OracleDeployTxHash!, settings.OracleDeployIndex)
            ?? throw new InvalidOperationException("Oracle deploy UTxO not found!");

        // Get oracle state UTxO for reference input
        ResolvedInput oracleStateUtxo = await wallet.Provider.GetUtxoByOutRefAsync(
            settings.OracleStateTxHash!, settings.OracleStateIndex)
            ?? throw new InvalidOperationException("Oracle state UTxO not found!");

        long resolutionTime = ((PlutusInt64)datum.ResolutionTime).Value;
        ulong validFromSlot = PosixToSlot(resolutionTime);

        var txBuilder = new TxBuilder(wallet.Provider);
        List<ResolvedInput> utxos = await wallet.GetWalletUtxos();

        ITransaction unsigned = await txBuilder
            .AddUnspentOutputs(utxos)
            .AddInput(marketUtxo, spendRedeemer)
            .AddReferenceInput(oracleStateUtxo)
            .AddReferenceInput(oracleDeployUtxo)
            .LockLovelace(scriptAddress, (ulong)((PlutusInt64)datum.TotalAda).Value, resolvedDatum)
            .AddWithdrawal(oracleRewardAddr, 0, IScript.Read(oracleDeployUtxo.Output.ScriptRef()!.Value), oracleRedeemer)
            .AddRequiredSigner(wallet.PaymentKeyHashHex)
            .SetChangeAddress(wallet.WalletBech32)
            .SetValidFrom(validFromSlot)
            .Complete();

        string txHash = await wallet.SignAndSubmit(unsigned);
        Console.WriteLine($"  Resolved: {txHash}");
        return txHash;
    }

    /// Claim winnings by burning winning tokens.
    public async Task<string> Claim(
        string marketTxHash, ulong marketIndex, string policyId, long burnAmount)
    {
        Console.WriteLine($"══ CLAIM ({burnAmount} tokens) ══");

        ResolvedInput marketUtxo = await wallet.Provider.GetUtxoByOutRefAsync(marketTxHash, marketIndex)
            ?? throw new InvalidOperationException("Market UTxO not found!");

        CborMarketDatum datum = marketUtxo.Output.InlineDatum<CborMarketDatum>()
            ?? throw new InvalidOperationException("Market UTxO has no inline datum!");

        // Determine winning side
        bool yesWins = datum.WinningSide is CborSomeYes;
        string winningToken = yesWins ? "YES" : "NO";
        long totalWinning = yesWins ? ((PlutusInt64)datum.TotalYesMinted).Value : ((PlutusInt64)datum.TotalNoMinted).Value;
        long totalAda = ((PlutusInt64)datum.TotalAda).Value;
        long payout = burnAmount * totalAda / totalWinning;

        Console.WriteLine($"  Winning side: {winningToken}");
        Console.WriteLine($"  Payout: {payout / 1_000_000} ADA");

        // Build claim redeemer: Claim { burn_amount } = constr(2, [burn_amount])
        var spendRedeemer = new CborClaimAction(PlutusInt64.Create(burnAmount));

        // Burn tokens
        var mintAssets = new Dictionary<string, long> { [winningToken] = -burnAmount };
        var mintRedeemer = new CborBurnAction(); // BurnTokens = constr(1)

        IScript marketScript = GetMarketScriptForPolicy(policyId);
        var networkType = Enum.Parse<NetworkType>(settings.Network);
        string scriptAddress = WalletAddress.FromScriptHash(networkType, policyId).ToBech32();

        bool isLastClaim = totalWinning == burnAmount;

        var txBuilder = new TxBuilder(wallet.Provider);
        List<ResolvedInput> utxos = await wallet.GetWalletUtxos();

        txBuilder
            .AddUnspentOutputs(utxos)
            .AddInput(marketUtxo, spendRedeemer)
            .AddMint(policyId, mintAssets, marketScript, mintRedeemer)
            .SetChangeAddress(wallet.WalletBech32);

        if (!isLastClaim)
        {
            // Continue market UTxO with reduced values
            var newDatum = yesWins
                ? datum with
                {
                    TotalAda = PlutusInt64.Create(totalAda - payout),
                    TotalYesMinted = PlutusInt64.Create(totalWinning - burnAmount),
                }
                : datum with
                {
                    TotalAda = PlutusInt64.Create(totalAda - payout),
                    TotalNoMinted = PlutusInt64.Create(totalWinning - burnAmount),
                };

            txBuilder.LockLovelace(scriptAddress, (ulong)(totalAda - payout), newDatum);
        }

        ITransaction unsigned = await txBuilder.Complete();
        string txHash = await wallet.SignAndSubmit(unsigned);
        Console.WriteLine($"  Claimed: {txHash}");
        return txHash;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string GetOracleScriptHash()
    {
        IScript oracleScript = OracleOracleWithdraw.Script;
        IScript parameterized = oracleScript.ApplyParameters(
            PlutusBoundedBytes.Create(Convert.FromHexString(settings.OracleNftPolicyId ?? "")));
        return parameterized.HashHex();
    }

    private static IScript GetMarketScriptForPolicy(string _policyId)
    {
        // The market script is already parameterized — we use the codegen base script
        // and apply params at creation time. For subsequent txs we need the reference script.
        // TODO: Store parameterized script or use reference inputs
        return MarketMarketSpend.Script;
    }

    private static string BuildRewardAddress(NetworkType network, string scriptHashHex)
    {
        // Reward address: header byte (0xF0 for mainnet script, 0xF1 for testnet script) + script hash
        byte header = network == NetworkType.Mainnet ? (byte)0xF0 : (byte)0xF1;
        byte[] scriptHash = Convert.FromHexString(scriptHashHex);
        byte[] addressBytes = new byte[1 + scriptHash.Length];
        addressBytes[0] = header;
        scriptHash.CopyTo(addressBytes, 1);
        return Convert.ToHexStringLower(addressBytes);
    }

    /// Convert POSIX milliseconds to Cardano slot number.
    /// Preview/Preprod: slot = (posix_ms / 1000) - 1_654_041_600 + 86_400
    /// This is approximate — use protocol params for exact conversion.
    private static ulong PosixToSlot(long posixMs)
    {
        long posixSec = posixMs / 1000;
        // Preview network shelley start
        const long shelleyStart = 1_654_041_600;
        const long slotOffset = 86_400;
        long slot = posixSec - shelleyStart + slotOffset;
        return slot > 0 ? (ulong)slot : 0;
    }
}

// ── CBOR types matching Aiken redeemers ──────────────────────────────────

/// MarketDatum as CBOR record
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(0)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborMarketDatum(
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(0)] PlutusBoundedBytes Creator,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(1)] PlutusBoundedBytes OracleScriptHash,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(2)] PlutusBoundedBytes OracleNftPolicy,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(3)] PlutusBoundedBytes FeedId,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(4)] IPlutusBigInt TargetPrice,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(5)] IPlutusBigInt Exponent,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(6)] IPlutusBigInt ResolutionTime,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(7)] PlutusBoundedBytes TokenPolicy,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(8)] IPlutusBigInt YesReserve,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(9)] IPlutusBigInt NoReserve,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(10)] IPlutusBigInt K,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(11)] IPlutusBigInt TotalYesMinted,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(12)] IPlutusBigInt TotalNoMinted,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(13)] IPlutusBigInt TotalAda,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(14)] ICborType Resolved,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(15)] ICborType WinningSide
) : CborRecord;

/// Aiken Bool: False = constr(0, []), True = constr(1, [])
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(0)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborFalse() : CborRecord;

[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(1)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborTrue() : CborRecord;

/// Option<BetDirection>: None = constr(1, []), Some(Yes) = constr(0, [constr(0,[])]), Some(No) = constr(0, [constr(1,[])])
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(1)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborNone() : CborRecord;

[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(0)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborSomeYes() : CborRecord; // Some(Yes) = constr(0, [constr(0,[])])

[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(0)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborSomeNo() : CborRecord; // Some(No) = constr(0, [constr(1,[])])

/// Bet YES: constr(0, [constr(0,[]), amount]) — Bet { direction: Yes, amount }
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(0)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborBetYes(
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(0)] IPlutusBigInt Amount
) : CborRecord;

/// Bet NO: constr(0, [constr(1,[]), amount]) — Bet { direction: No, amount }
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(0)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborBetNo(
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(0)] IPlutusBigInt Amount
) : CborRecord;

/// Resolve: constr(1, [])
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(1)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborResolveAction() : CborRecord;

/// Claim { burn_amount }: constr(2, [burn_amount])
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(2)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborClaimAction(
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(0)] IPlutusBigInt BurnAmount
) : CborRecord;

/// Cancel: constr(3, [])
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(3)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborCancelAction() : CborRecord;

/// BurnTokens: constr(1, [])
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(1)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborBurnAction() : CborRecord;
