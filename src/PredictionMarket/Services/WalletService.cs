using Chrysalis.Codec.Types.Cardano.Core.Transaction;
using Chrysalis.Tx.Extensions;
using Chrysalis.Tx.Models.Cbor;
using Chrysalis.Tx.Providers;
using Chrysalis.Wallet.Models.Addresses;
using Chrysalis.Wallet.Models.Enums;
using Chrysalis.Wallet.Models.Keys;
using Chrysalis.Wallet.Words;
using PredictionMarket.Config;

namespace PredictionMarket.Services;

public class WalletService
{
    private readonly PrivateKey _paymentKey;
    private readonly PrivateKey _stakeKey;
    private readonly Address _walletAddress;
    private readonly Blockfrost _provider;

    public string WalletBech32 => _walletAddress.ToBech32();
    public byte[] PaymentKeyHash => _walletAddress.GetPaymentKeyHash()!;
    public string PaymentKeyHashHex => Convert.ToHexStringLower(PaymentKeyHash);
    public Blockfrost Provider => _provider;

    public WalletService(AppSettings settings)
    {
        var networkType = Enum.Parse<NetworkType>(settings.Network);

        _provider = new Blockfrost(settings.BlockfrostApiKey, networkType);

        Mnemonic mnemonic = Mnemonic.Restore(settings.WalletMnemonic, English.Words);
        PrivateKey rootKey = mnemonic.GetRootKey("");
        PrivateKey accountKey = rootKey
            .Derive(PurposeType.Shelley, DerivationType.HARD)
            .Derive(CoinType.Ada, DerivationType.HARD)
            .Derive(0, DerivationType.HARD);

        _paymentKey = accountKey.Derive(RoleType.ExternalChain).Derive(0);
        _stakeKey = accountKey.Derive(RoleType.Staking).Derive(0);

        _walletAddress = Address.FromPublicKeys(
            networkType, AddressType.Base,
            _paymentKey.GetPublicKey(), _stakeKey.GetPublicKey());
    }

    public async Task<string> SignAndSubmit(ITransaction unsigned)
    {
        ITransaction signed = unsigned.Sign(_paymentKey);
        string txId = await _provider.SubmitTransactionAsync(signed);
        Console.WriteLine($"  TxHash: {txId}");
        return txId;
    }

    public async Task<ResolvedInput?> WaitForUtxo(string address, string txHash, int maxWaitSeconds = 120)
    {
        for (int elapsed = 0; elapsed < maxWaitSeconds; elapsed += 4)
        {
            List<ResolvedInput> utxos = await _provider.GetUtxosAsync([address]);
            foreach (ResolvedInput utxo in utxos)
            {
                if (Convert.ToHexStringLower(utxo.Outref.TransactionId.Span) == txHash)
                    return utxo;
            }
            await Task.Delay(TimeSpan.FromSeconds(4));
            Console.Write(".");
        }
        Console.WriteLine();
        return null;
    }

    public async Task<List<ResolvedInput>> GetWalletUtxos()
    {
        return await _provider.GetUtxosAsync([WalletBech32]);
    }
}
