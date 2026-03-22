using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PredictionMarket.Config;

namespace PredictionMarket.Services;

public class PythPriceService(AppSettings settings, HttpClient httpClient)
{
    private const string BaseUrl = "https://pyth-lazer.dourolabs.app";

    private static readonly Dictionary<string, int> FeedNameToId = new()
    {
        ["BTC/USD"] = 1,
        ["ETH/USD"] = 2,
        ["ADA/USD"] = 16,
    };

    /// Fetch the latest signed price update from Pyth Lazer REST API.
    /// Returns the raw hex bytes (solana format) for the withdraw redeemer.
    public async Task<PythUpdateResult> GetLatestUpdate(string feedName)
    {
        int feedId = FeedNameToId.TryGetValue(feedName, out int id)
            ? id
            : int.Parse(feedName); // allow raw feed ID

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/latest_price");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.PythApiKey);
        request.Content = JsonContent.Create(new PythLatestPriceRequest
        {
            Channel = "fixed_rate@200ms",
            Formats = ["solana"],
            PriceFeedIds = [feedId],
            Properties = ["price", "exponent"],
            JsonBinaryEncoding = "hex",
            Parsed = true,
        });

        HttpResponseMessage response = await httpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Pyth API error ({response.StatusCode}): {body}");

        PythLatestPriceResponse result = JsonSerializer.Deserialize<PythLatestPriceResponse>(body)
            ?? throw new InvalidOperationException("Failed to deserialize Pyth response");

        string solanaHex = result.Solana?.Data
            ?? throw new InvalidOperationException("No solana data in Pyth response");

        // Extract parsed price if available
        long price = 0;
        int exponent = 0;
        if (result.Parsed?.PriceFeeds?.Count > 0)
        {
            PythParsedFeed feed = result.Parsed.PriceFeeds[0];
            price = long.Parse(feed.Price ?? "0");
            exponent = feed.Exponent;
        }

        Console.WriteLine($"  Pyth {feedName} (feed {feedId}): price={price}, exp={exponent}");
        Console.WriteLine($"  Solana update: {solanaHex[..Math.Min(80, solanaHex.Length)]}...");

        return new PythUpdateResult(
            SolanaHex: solanaHex,
            Price: price,
            Exponent: exponent,
            FeedId: feedId
        );
    }
}

public record PythUpdateResult(string SolanaHex, long Price, int Exponent, int FeedId);

// ── JSON request/response models ──

public class PythLatestPriceRequest
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("formats")]
    public List<string> Formats { get; set; } = [];

    [JsonPropertyName("priceFeedIds")]
    public List<int> PriceFeedIds { get; set; } = [];

    [JsonPropertyName("properties")]
    public List<string> Properties { get; set; } = [];

    [JsonPropertyName("jsonBinaryEncoding")]
    public string JsonBinaryEncoding { get; set; } = "hex";

    [JsonPropertyName("parsed")]
    public bool Parsed { get; set; } = true;
}

public class PythLatestPriceResponse
{
    [JsonPropertyName("solana")]
    public PythBinaryData? Solana { get; set; }

    [JsonPropertyName("parsed")]
    public PythParsedData? Parsed { get; set; }
}

public class PythBinaryData
{
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }
}

public class PythParsedData
{
    [JsonPropertyName("priceFeeds")]
    public List<PythParsedFeed>? PriceFeeds { get; set; }
}

public class PythParsedFeed
{
    [JsonPropertyName("priceFeedId")]
    public int PriceFeedId { get; set; }

    [JsonPropertyName("price")]
    public string? Price { get; set; }

    [JsonPropertyName("exponent")]
    public int Exponent { get; set; }
}
