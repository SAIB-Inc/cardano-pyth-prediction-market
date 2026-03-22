namespace PredictionMarket.Services;

public record PriceFeedData(long Price, string FeedName, long Timestamp);

public record SignedPriceFeedData(PriceFeedData Data, byte[] Signature);

public interface IPriceService
{
    Task<long> GetCurrentPrice(string feedName);
    Task<SignedPriceFeedData> GetSignedPriceFeed(string feedName);
}
