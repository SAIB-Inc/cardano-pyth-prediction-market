using Chrysalis.Codec.Types;
using Chrysalis.Codec.Types.Cardano.Core.Certificates;
using Chrysalis.Codec.Types.Cardano.Core.Common;
using Chrysalis.Codec.Types.Cardano.Core.Scripts;
using Chrysalis.Codec.Types.Cardano.Core.Transaction;
using Chrysalis.Tx.Builders;
using Chrysalis.Tx.Extensions;
using Chrysalis.Tx.Models;
using Chrysalis.Tx.Models.Cbor;
using Chrysalis.Wallet.Models.Addresses;
using Chrysalis.Wallet.Models.Enums;
using PredictionMarket.Config;
using Saib.PredictionMarket.Blueprint;

namespace PredictionMarket.Services;

public class DeployService(WalletService wallet, AppSettings settings)
{
    /// Deploy market validator as reference script at a UTxO.
    /// Returns (txHash, index).
    public async Task<(string TxHash, ulong Index)> DeployMarketScript()
    {
        Console.WriteLine("══ DEPLOY MARKET REFERENCE SCRIPT ══");

        IScript marketScript = MarketMarketSpend.Script;
        string walletAddr = wallet.WalletBech32;

        var template = TransactionTemplateBuilder.Create<NoParams>(wallet.Provider)
            .SetChangeAddress(walletAddr)
            .AddOutput((options, _, _) =>
            {
                options.To = "deploy";
                options.Script = marketScript;
            })
            .AddStaticParty("deploy", walletAddr)
            .Build();

        ITransaction unsigned = await template(new NoParams(walletAddr));
        string txHash = await wallet.SignAndSubmit(unsigned);

        ResolvedInput? utxo = await wallet.WaitForUtxo(walletAddr, txHash)
            ?? throw new InvalidOperationException("Market deploy UTxO not found!");

        Console.WriteLine($"  Market deploy: {txHash}#{utxo.Outref.Index}");
        return (txHash, utxo.Outref.Index);
    }

    /// Deploy oracle withdraw validator as reference script.
    /// Oracle script is parameterized with the oracle NFT policy.
    public async Task<(string TxHash, ulong Index, string ScriptHash)> DeployOracleScript(string oracleNftPolicyHex)
    {
        Console.WriteLine("══ DEPLOY ORACLE REFERENCE SCRIPT ══");

        IScript oracleScript = OracleOracleWithdraw.Script;
        // Parameterize with oracle NFT policy
        IScript parameterized = oracleScript.ApplyParameters(
            PlutusBoundedBytes.Create(Convert.FromHexString(oracleNftPolicyHex)));

        string scriptHash = parameterized.HashHex();
        string walletAddr = wallet.WalletBech32;

        var template = TransactionTemplateBuilder.Create<NoParams>(wallet.Provider)
            .SetChangeAddress(walletAddr)
            .AddOutput((options, _, _) =>
            {
                options.To = "deploy";
                options.Script = parameterized;
            })
            .AddStaticParty("deploy", walletAddr)
            .Build();

        ITransaction unsigned = await template(new NoParams(walletAddr));
        string txHash = await wallet.SignAndSubmit(unsigned);

        ResolvedInput? utxo = await wallet.WaitForUtxo(walletAddr, txHash)
            ?? throw new InvalidOperationException("Oracle deploy UTxO not found!");

        Console.WriteLine($"  Oracle deploy: {txHash}#{utxo.Outref.Index}");
        Console.WriteLine($"  Oracle script hash: {scriptHash}");
        return (txHash, utxo.Outref.Index, scriptHash);
    }

    /// Register oracle stake credential (Conway RegCert).
    /// Required before any withdraw-0 transactions.
    public async Task<string> RegisterOracleStakeCredential(
        string oracleScriptHash, string oracleDeployTxHash, ulong oracleDeployIndex)
    {
        Console.WriteLine("══ REGISTER ORACLE STAKE CREDENTIAL ══");

        byte[] scriptHash = Convert.FromHexString(oracleScriptHash);
        Credential scriptCredential = Credential.Create(1, scriptHash); // 1 = ScriptHash
        RegCert regCert = RegCert.Create(7, scriptCredential, 2_000_000);

        string walletAddr = wallet.WalletBech32;

        var template = TransactionTemplateBuilder.Create<NoParams>(wallet.Provider)
            .SetChangeAddress(walletAddr)
            .AddReferenceInput((options, _) =>
            {
                options.UtxoRef = TransactionInput.Create(
                    Convert.FromHexString(oracleDeployTxHash), oracleDeployIndex);
            })
            .AddCertificate((options, _) =>
            {
                options.Certificate = regCert;
                // Void redeemer — publish handler always true for RegisterCredential
                options.SetRedeemer(new CborConstr0());
            })
            .Build(eval: true);

        ITransaction unsigned = await template(new NoParams(walletAddr));
        string txHash = await wallet.SignAndSubmit(unsigned);

        Console.WriteLine($"  Registered: {txHash}");
        return txHash;
    }

    /// Create oracle state UTxO with NFT + inline datum.
    /// For hackathon: mint NFT using a simple always-succeeds native script.
    /// Returns (txHash, index, nftPolicyId).
    public async Task<(string TxHash, ulong Index, string NftPolicyId)> CreateOracleState(string feedId)
    {
        Console.WriteLine("══ CREATE ORACLE STATE ══");

        byte[] oraclePubKey = BinancePriceService.GetPublicKey(settings.OracleSecretKey);
        Console.WriteLine($"  Oracle public key: {Convert.ToHexStringLower(oraclePubKey)}");

        // For hackathon: use a one-shot UTxO to make a unique NFT policy
        List<ResolvedInput> utxos = await wallet.GetWalletUtxos();
        ResolvedInput oneShot = utxos.First();
        string oneShotTxHash = Convert.ToHexStringLower(oneShot.Outref.TransactionId.Span);

        // Build oracle datum
        var oracleDatum = new CborOracleDatum(
            PlutusBoundedBytes.Create(oraclePubKey),
            PlutusBoundedBytes.Create(System.Text.Encoding.UTF8.GetBytes(feedId))
        );

        string walletAddr = wallet.WalletBech32;
        // TODO: Implement proper NFT minting for oracle state
        // For now, just create a UTxO with the datum (NFT minting will be added)
        var txBuilder = new TxBuilder(wallet.Provider);
        List<ResolvedInput> walletUtxos = await wallet.GetWalletUtxos();

        ITransaction unsigned = await txBuilder
            .AddUnspentOutputs(walletUtxos)
            .LockLovelace(walletAddr, 2_000_000, oracleDatum)
            .SetChangeAddress(walletAddr)
            .Complete();

        string txHash = await wallet.SignAndSubmit(unsigned);

        ResolvedInput? utxo = await wallet.WaitForUtxo(walletAddr, txHash)
            ?? throw new InvalidOperationException("Oracle state UTxO not found!");

        Console.WriteLine($"  Oracle state: {txHash}#{utxo.Outref.Index}");
        return (txHash, utxo.Outref.Index, "TODO_NFT_POLICY");
    }
}

/// Void redeemer for certificate registration
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(0)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborConstr0() : CborRecord;

/// Oracle datum: constr(0, [trusted_pub_key: ByteArray, feed_id: ByteArray])
[Chrysalis.Codec.Serialization.Attributes.CborSerializable]
[Chrysalis.Codec.Serialization.Attributes.CborConstr(0)]
[Chrysalis.Codec.Serialization.Attributes.CborIndefinite]
public partial record CborOracleDatum(
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(0)] PlutusBoundedBytes TrustedPubKey,
    [Chrysalis.Codec.Serialization.Attributes.CborOrder(1)] PlutusBoundedBytes FeedId
) : CborRecord;

/// Minimal ITransactionParameters for templates with no dynamic params
public record NoParams(string ChangeAddress) : ITransactionParameters
{
    public Dictionary<string, (string address, bool isChange)> Parties { get; set; } = new()
    {
        ["change"] = (ChangeAddress, true)
    };
}
