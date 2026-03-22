namespace PredictionMarket.Config;

public class AppSettings
{
    public string BlockfrostApiKey { get; set; } = "";
    public string Network { get; set; } = "Preview"; // Preview, Preprod, Mainnet
    public string WalletMnemonic { get; set; } = "";
    public string OracleSecretKey { get; set; } = ""; // Ed25519 secret key hex (32 bytes)
    public string FeedId { get; set; } = "BTC/USD";
    public int Exponent { get; set; } = -8;

    // Pyth Lazer
    public string? PythPolicyId { get; set; }
    public string PythApiKey { get; set; } = "";

    // Deploy references (populated after deploy commands)
    public string? MarketDeployTxHash { get; set; }
    public ulong MarketDeployIndex { get; set; }
    public string? OracleDeployTxHash { get; set; }
    public ulong OracleDeployIndex { get; set; }
    public string? OracleNftPolicyId { get; set; }
    public string? OracleStateTxHash { get; set; }
    public ulong OracleStateIndex { get; set; }
}
