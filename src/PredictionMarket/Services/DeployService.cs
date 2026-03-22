using Chrysalis.Codec.Extensions.Cardano.Core.Common;
using Chrysalis.Codec.Extensions.Cardano.Core.Transaction;
using Chrysalis.Codec.Serialization;
using Chrysalis.Codec.Serialization.Attributes;
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
    /// Deploy a script as reference script at the wallet address.
    /// Uses TransactionTemplateBuilder (same pattern as Wizard V2 deploy).
    private async Task<(string TxHash, ulong Index)> DeployReferenceScript(IScript script, string label)
    {
        string walletAddr = wallet.WalletBech32;

        var template = TransactionTemplateBuilder.Create<DeployParams>(wallet.Provider)
            .SetChangeAddress(walletAddr)
            .AddOutput((options, _, _) =>
            {
                options.To = "deployTarget";
                options.Script = script;
            })
            .Build();

        ITransaction unsigned = await template(new DeployParams(walletAddr, walletAddr));
        string txHash = await wallet.SignAndSubmit(unsigned);

        ResolvedInput? utxo = await wallet.WaitForUtxo(walletAddr, txHash)
            ?? throw new InvalidOperationException($"{label} UTxO not found!");

        Console.WriteLine($"  {label}: {txHash}#{utxo.Outref.Index}");
        return (txHash, utxo.Outref.Index);
    }

    /// Deploy market validator as reference script.
    public async Task<(string TxHash, ulong Index)> DeployMarketScript()
    {
        Console.WriteLine("══ DEPLOY MARKET REFERENCE SCRIPT ══");
        return await DeployReferenceScript(MarketMarketSpend.Script, "Market deploy");
    }

    /// Deploy oracle withdraw validator as reference script.
    /// Oracle script is parameterized with the oracle NFT policy.
    public async Task<(string TxHash, ulong Index, string ScriptHash)> DeployOracleScript(string oracleNftPolicyHex)
    {
        Console.WriteLine("══ DEPLOY ORACLE REFERENCE SCRIPT ══");

        IScript oracleScript = OracleOracleWithdraw.Script;
        IScript parameterized = oracleScript.ApplyParameters(
            PlutusBoundedBytes.Create(Convert.FromHexString(oracleNftPolicyHex)));

        string scriptHash = parameterized.HashHex();
        Console.WriteLine($"  Oracle script hash: {scriptHash}");

        var (txHash, index) = await DeployReferenceScript(parameterized, "Oracle deploy");
        return (txHash, index, scriptHash);
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
                options.SetRedeemer(new PlutusVoid());
            })
            .Build(eval: true);

        ITransaction unsigned = await template(new NoParams(walletAddr));
        string txHash = await wallet.SignAndSubmit(unsigned);

        Console.WriteLine($"  Registered: {txHash}");
        return txHash;
    }

    /// Deploy pyth_test validator + register stake credential.
    /// Returns (deployTxHash, deployIndex, scriptHash).
    public async Task<(string TxHash, ulong Index, string ScriptHash)> DeployPythTest()
    {
        Console.WriteLine("══ DEPLOY PYTH TEST ══");
        string pythPolicyHex = settings.PythPolicyId
            ?? throw new InvalidOperationException("PYTH_POLICY_ID not set");

        IScript pythTestScript = PythTestPythTestWithdraw.Script;
        IScript parameterized = pythTestScript.ApplyParameters(
            PlutusBoundedBytes.Create(Convert.FromHexString(pythPolicyHex)));

        string testScriptHash = parameterized.HashHex();
        Console.WriteLine($"  pyth_test script hash: {testScriptHash}");

        var (deployTxHash, deployIndex) = await DeployReferenceScript(parameterized, "pyth_test deploy");
        await Task.Delay(TimeSpan.FromSeconds(5));

        Console.WriteLine("  Registering pyth_test stake credential...");
        await RegisterStakeCredential(testScriptHash, deployTxHash, deployIndex);

        Console.WriteLine($"\n  Set env: PYTH_TEST_DEPLOY_TX={deployTxHash} PYTH_TEST_DEPLOY_IDX={deployIndex}");
        return (deployTxHash, deployIndex, testScriptHash);
    }

    /// Test Pyth withdraw-0 using already-deployed pyth_test validator.
    public async Task TestPythContract(
        PythPriceService pythService, string feedName,
        string testDeployTxHash, ulong testDeployIndex)
    {
        Console.WriteLine("══ TEST PYTH CONTRACT ══");
        string pythPolicyHex = settings.PythPolicyId
            ?? throw new InvalidOperationException("PYTH_POLICY_ID not set");
        var networkType = Enum.Parse<NetworkType>(settings.Network);

        // Parameterize pyth_test to get script hash
        IScript parameterized = PythTestPythTestWithdraw.Script.ApplyParameters(
            PlutusBoundedBytes.Create(Convert.FromHexString(pythPolicyHex)));
        string testScriptHash = parameterized.HashHex();
        Console.WriteLine($"  pyth_test script hash: {testScriptHash}");

        // Fetch signed update from Pyth API
        Console.WriteLine("  Fetching signed price from Pyth Lazer...");
        PythUpdateResult update = await pythService.GetLatestUpdate(feedName);

        // Find Pyth State UTxO
        Console.WriteLine("  Finding Pyth State UTxO...");
        string pythStateTokenHex = pythPolicyHex + Convert.ToHexStringLower(
            System.Text.Encoding.UTF8.GetBytes("Pyth State"));
        ResolvedInput pythStateUtxo = await FindUtxoByAsset(pythStateTokenHex)
            ?? throw new InvalidOperationException("Pyth State UTxO not found on-chain!");
        string pythStateTxHash = Convert.ToHexStringLower(pythStateUtxo.Outref.TransactionId.Span);
        Console.WriteLine($"  Pyth State: {pythStateTxHash}#{pythStateUtxo.Outref.Index}");

        // Read Pyth withdraw script hash from reference script on Pyth State UTxO
        ReadOnlyMemory<byte>? refScriptMem = pythStateUtxo.Output.ScriptRef();
        if (refScriptMem is null)
            throw new InvalidOperationException("Pyth State UTxO has no reference script!");
        string pythWithdrawHash = IScript.Read(refScriptMem.Value.ToArray()).HashHex();
        Console.WriteLine($"  Pyth withdraw script hash: {pythWithdrawHash}");

        // Build reward addresses (bech32 for template params)
        Chrysalis.Wallet.Models.Addresses.Address pythRewardAddr = new(
            networkType, AddressType.ScriptDelegation,
            [], Convert.FromHexString(pythWithdrawHash));
        Chrysalis.Wallet.Models.Addresses.Address testRewardAddr = new(
            networkType, AddressType.ScriptDelegation,
            [], Convert.FromHexString(testScriptHash));
        string pythRewardBech32 = pythRewardAddr.ToBech32();
        string testRewardBech32 = testRewardAddr.ToBech32();

        Console.WriteLine($"  Pyth reward addr: {pythRewardBech32}");
        Console.WriteLine($"  Test reward addr: {testRewardBech32}");

        // Pyth redeemer: List<ByteArray> as Plutus data
        byte[] updateBytes = Convert.FromHexString(update.SolanaHex);
        var pythRedeemer = PlutusList.Create(
            CborDefList<IPlutusData>.Create([PlutusBoundedBytes.Create(updateBytes)]));

        // Get current slot for tight validity window (Pyth requires it)
        ulong currentSlot = await GetCurrentSlot();
        Console.WriteLine($"  Current slot: {currentSlot}");

        // Build transaction using TransactionTemplateBuilder (resolves scripts from ref inputs)
        Console.WriteLine("  Building test transaction...");
        string walletAddr = wallet.WalletBech32;

        var template = TransactionTemplateBuilder.Create<PythTestParams>(wallet.Provider)
            .SetChangeAddress(walletAddr)
            // Reference input: Pyth State UTxO (has Pyth withdraw script as ref script)
            .AddReferenceInput((options, _) =>
            {
                options.UtxoRef = TransactionInput.Create(
                    Convert.FromHexString(pythStateTxHash), pythStateUtxo.Outref.Index);
            })
            // Reference input: pyth_test deploy UTxO (has our test script as ref script)
            .AddReferenceInput((options, _) =>
            {
                options.UtxoRef = TransactionInput.Create(
                    Convert.FromHexString(testDeployTxHash), testDeployIndex);
            })
            // Withdrawal from Pyth's withdraw script (verifies signatures)
            .AddWithdrawal((options, _) =>
            {
                options.From = "pythWithdraw";
                options.Amount = 0;
                options.SetRedeemerBuilder<PlutusList>((_, _, _) => pythRedeemer);
            })
            // Withdrawal from our pyth_test script (reads Pyth updates)
            .AddWithdrawal((options, _) =>
            {
                options.From = "testWithdraw";
                options.Amount = 0;
                options.SetRedeemerBuilder<PlutusVoid>((_, _, _) => new PlutusVoid());
            })
            .SetValidFrom(currentSlot)
            .SetValidTo(currentSlot + 300)
            .Build(eval: true);

        ITransaction unsigned = await template(new PythTestParams(
            walletAddr, pythRewardBech32, testRewardBech32));
        string txHash = await wallet.SignAndSubmit(unsigned);
        Console.WriteLine($"\n  Pyth test SUCCEEDED: {txHash}");
    }

    /// Register a script's stake credential (Conway RegCert).
    public async Task<string> RegisterStakeCredential(
        string scriptHashHex, string deployTxHash, ulong deployIndex)
    {
        byte[] scriptHash = Convert.FromHexString(scriptHashHex);
        Credential scriptCredential = Credential.Create(1, scriptHash);
        RegCert regCert = RegCert.Create(7, scriptCredential, 2_000_000);

        string walletAddr = wallet.WalletBech32;

        var template = TransactionTemplateBuilder.Create<NoParams>(wallet.Provider)
            .SetChangeAddress(walletAddr)
            .AddReferenceInput((options, _) =>
            {
                options.UtxoRef = TransactionInput.Create(
                    Convert.FromHexString(deployTxHash), deployIndex);
            })
            .AddCertificate((options, _) =>
            {
                options.Certificate = regCert;
                options.SetRedeemer(new PlutusVoid());
            })
            .Build(eval: true);

        ITransaction unsigned = await template(new NoParams(walletAddr));
        string txHash = await wallet.SignAndSubmit(unsigned);

        Console.WriteLine($"  Registered: {txHash}");
        return txHash;
    }

    /// Get current slot from Blockfrost.
    private async Task<ulong> GetCurrentSlot()
    {
        using var httpClient = new HttpClient();
        string network = settings.Network.ToLowerInvariant();
        string url = $"https://cardano-{network}.blockfrost.io/api/v0/blocks/latest";
        httpClient.DefaultRequestHeaders.Add("project_id", settings.BlockfrostApiKey);
        string json = await httpClient.GetStringAsync(url);
        // Simple parse — extract "slot" field
        int slotIdx = json.IndexOf("\"slot\":");
        string slotStr = json[(slotIdx + 7)..];
        slotStr = slotStr[..slotStr.IndexOfAny([',', '}'])];
        return ulong.Parse(slotStr);
    }

    /// Find a UTxO containing a specific asset (policy+name hex).
    private async Task<ResolvedInput?> FindUtxoByAsset(string assetHex)
    {
        string policyId = assetHex[..56];
        string assetName = assetHex[56..];

        // Query all UTxOs at the known Pyth State address
        string pythStateAddr = "addr_test1wrm3tr5zpw9k2nefjtsz66wfzn6flnphr5kd6ak9ufrl3wcqqfyn8";
        List<ResolvedInput> utxos = await wallet.Provider.GetUtxosAsync([pythStateAddr]);

        foreach (ResolvedInput utxo in utxos)
        {
            ulong? qty = utxo.Output.Amount().QuantityOf(policyId, assetName);
            if (qty.HasValue && qty.Value > 0) return utxo;
        }
        return null;
    }
}

/// Oracle datum: constr(0, [trusted_pub_key: ByteArray, feed_id: ByteArray])
[CborSerializable]
[CborConstr(0)]
[CborIndefinite]
public partial record CborOracleDatum(
    [CborOrder(0)] PlutusBoundedBytes TrustedPubKey,
    [CborOrder(1)] PlutusBoundedBytes FeedId
) : CborRecord;

/// Deploy params: wallet + deploy target (same pattern as Wizard V2)
public record DeployParams(string ChangeAddress, string DeployTargetAddress) : ITransactionParameters
{
    public Dictionary<string, (string address, bool isChange)> Parties { get; set; } = new()
    {
        ["change"] = (ChangeAddress, true),
        ["deployTarget"] = (DeployTargetAddress, false),
    };
}

/// Minimal ITransactionParameters for templates with no dynamic params
public record NoParams(string ChangeAddress) : ITransactionParameters
{
    public Dictionary<string, (string address, bool isChange)> Parties { get; set; } = new()
    {
        ["change"] = (ChangeAddress, true)
    };
}

/// Params for Pyth test transaction: wallet + two withdraw reward addresses
public record PythTestParams(
    string ChangeAddress, string PythWithdrawReward, string TestWithdrawReward
) : ITransactionParameters
{
    public Dictionary<string, (string address, bool isChange)> Parties { get; set; } = new()
    {
        ["change"] = (ChangeAddress, true),
        ["pythWithdraw"] = (PythWithdrawReward, false),
        ["testWithdraw"] = (TestWithdrawReward, false),
    };
}
