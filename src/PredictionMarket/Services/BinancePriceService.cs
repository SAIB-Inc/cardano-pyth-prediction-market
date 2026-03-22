using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Chrysalis.Codec.Serialization;
using Chrysalis.Codec.Serialization.Attributes;
using Chrysalis.Codec.Types;
using Chrysalis.Codec.Types.Cardano.Core.Common;
using Chrysalis.Crypto;
using PredictionMarket.Config;

namespace PredictionMarket.Services;

/// On-chain PriceFeed type: constr(0, [price: Int, name: ByteArray, timestamp: Int])
/// Must match Aiken's cbor.serialise(PriceFeed { price, name, timestamp })
[CborSerializable]
[CborConstr(0)]
[CborIndefinite]
public partial record CborPriceFeed(
    [CborOrder(0)] IPlutusBigInt Price,
    [CborOrder(1)] PlutusBoundedBytes Name,
    [CborOrder(2)] IPlutusBigInt Timestamp
) : CborRecord;

/// On-chain SignedPriceFeed: constr(0, [data: PriceFeed, signature: ByteArray])
[CborSerializable]
[CborConstr(0)]
[CborIndefinite]
public partial record CborSignedPriceFeed(
    [CborOrder(0)] CborPriceFeed Data,
    [CborOrder(1)] PlutusBoundedBytes Signature
) : CborRecord;

public class BinancePriceService(AppSettings settings, HttpClient httpClient) : IPriceService
{
    private static readonly Dictionary<string, string> FeedToBinanceSymbol = new()
    {
        ["BTC/USD"] = "BTCUSDT",
        ["ETH/USD"] = "ETHUSDT",
        ["ADA/USD"] = "ADAUSDT",
    };

    private static readonly Dictionary<string, int> FeedExponents = new()
    {
        ["BTC/USD"] = -8,
        ["ETH/USD"] = -8,
        ["ADA/USD"] = -6,
    };

    public async Task<long> GetCurrentPrice(string feedName)
    {
        if (!FeedToBinanceSymbol.TryGetValue(feedName, out string? symbol))
            throw new ArgumentException($"Unknown feed: {feedName}");

        string url = $"https://api.binance.com/api/v3/ticker/price?symbol={symbol}";
        var response = await httpClient.GetFromJsonAsync<BinancePriceResponse>(url)
            ?? throw new InvalidOperationException("Failed to fetch price from Binance");

        int exponent = FeedExponents.GetValueOrDefault(feedName, -8);
        double multiplier = Math.Pow(10, -exponent);
        long rawPrice = (long)(double.Parse(response.Price) * multiplier);
        return rawPrice;
    }

    public async Task<SignedPriceFeedData> GetSignedPriceFeed(string feedName)
    {
        long price = await GetCurrentPrice(feedName);
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Build CBOR PriceFeed matching Aiken's on-chain type
        byte[] feedNameBytes = Encoding.UTF8.GetBytes(feedName);
        var cborFeed = new CborPriceFeed(
            PlutusInt64.Create(price),
            PlutusBoundedBytes.Create(feedNameBytes),
            PlutusInt64.Create(timestamp)
        );

        // Sign CBOR-serialized feed (same as Aiken: cbor.serialise(data))
        byte[] cborBytes = CborSerializer.Serialize(cborFeed);
        byte[] secretKey = Convert.FromHexString(settings.OracleSecretKey);
        byte[] expandedKey = Ed25519.ExpandedPrivateKeyFromSeed(secretKey);
        byte[] signature = Ed25519.Sign(cborBytes, expandedKey);

        return new SignedPriceFeedData(
            new PriceFeedData(price, feedName, timestamp),
            signature
        );
    }

    public static byte[] GetPublicKey(string secretKeyHex)
    {
        byte[] secretKey = Convert.FromHexString(secretKeyHex);
        byte[] expandedKey = Ed25519.ExpandedPrivateKeyFromSeed(secretKey);
        return Ed25519.GetPublicKey(expandedKey);
    }

    private record BinancePriceResponse(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("price")] string Price
    );
}
