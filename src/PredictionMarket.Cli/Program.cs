using PredictionMarket.Config;
using PredictionMarket.Services;

// ── Configuration ────────────────────────────────────────────────────────────

var settings = new AppSettings
{
    BlockfrostApiKey = EnvRequired("BLOCKFROST_API_KEY"),
    Network = Env("NETWORK", "Preview")!,
    WalletMnemonic = EnvRequired("WALLET_MNEMONIC"),
    OracleSecretKey = Env("ORACLE_SECRET_KEY", "") ?? "",
    FeedId = Env("FEED_ID", "BTC/USD") ?? "BTC/USD",
    Exponent = int.Parse(Env("EXPONENT", "-8") ?? "-8"),
    MarketDeployTxHash = Env("MARKET_DEPLOY_TX_HASH", null),
    MarketDeployIndex = ulong.Parse(Env("MARKET_DEPLOY_INDEX", "0") ?? "0"),
    OracleDeployTxHash = Env("ORACLE_DEPLOY_TX_HASH", null),
    OracleDeployIndex = ulong.Parse(Env("ORACLE_DEPLOY_INDEX", "0") ?? "0"),
    OracleNftPolicyId = Env("ORACLE_NFT_POLICY_ID", null),
    OracleStateTxHash = Env("ORACLE_STATE_TX_HASH", null),
    OracleStateIndex = ulong.Parse(Env("ORACLE_STATE_INDEX", "0") ?? "0"),
};

// ── Services ─────────────────────────────────────────────────────────────────

var wallet = new WalletService(settings);
var httpClient = new HttpClient();
var priceService = new BinancePriceService(settings, httpClient);
var deployService = new DeployService(wallet, settings);
var marketService = new MarketService(wallet, priceService, settings);
var cycleService = new MarketCycleService(marketService, wallet, settings);

Console.WriteLine($"  Wallet: {wallet.WalletBech32}");
Console.WriteLine($"  Network: {settings.Network}");

// ── Parse command ────────────────────────────────────────────────────────────

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

try
{
    switch (command)
    {
        // ── Deploy commands ──────────────────────────────────────────────
        case "deploy-market":
        {
            var (txHash, index) = await deployService.DeployMarketScript();
            Console.WriteLine($"\n  Set env: MARKET_DEPLOY_TX_HASH={txHash} MARKET_DEPLOY_INDEX={index}");
            break;
        }

        case "deploy-oracle":
        {
            string nftPolicy = RequireArg(args, 1, "oracle NFT policy ID hex");
            var (txHash, index, scriptHash) = await deployService.DeployOracleScript(nftPolicy);
            Console.WriteLine($"\n  Set env: ORACLE_DEPLOY_TX_HASH={txHash} ORACLE_DEPLOY_INDEX={index}");
            Console.WriteLine($"  Oracle script hash: {scriptHash}");
            break;
        }

        case "register-oracle":
        {
            string scriptHash = RequireArg(args, 1, "oracle script hash hex");
            string deployTx = settings.OracleDeployTxHash ?? RequireArg(args, 2, "oracle deploy tx hash");
            ulong deployIdx = args.Length > 3 ? ulong.Parse(args[3]) : settings.OracleDeployIndex;
            await deployService.RegisterOracleStakeCredential(scriptHash, deployTx, deployIdx);
            break;
        }

        case "create-oracle-state":
        {
            string feedId = args.Length > 1 ? args[1] : settings.FeedId;
            var (txHash, index, nftPolicy) = await deployService.CreateOracleState(feedId);
            Console.WriteLine($"\n  Set env: ORACLE_STATE_TX_HASH={txHash} ORACLE_STATE_INDEX={index}");
            Console.WriteLine($"  NFT Policy: {nftPolicy}");
            break;
        }

        // ── Market commands ──────────────────────────────────────────────
        case "create":
        {
            string feedId = args.Length > 1 ? args[1] : settings.FeedId;
            ulong seedAda = ulong.Parse(args.Length > 2 ? args[2] : "100") * 1_000_000;
            var (txHash, index, policyId) = await marketService.CreateMarket(feedId, seedAda);
            Console.WriteLine($"\n  Market: {txHash}#{index}");
            Console.WriteLine($"  Policy: {policyId}");
            break;
        }

        case "bet":
        {
            string marketTx = RequireArg(args, 1, "market tx hash");
            ulong marketIdx = ulong.Parse(RequireArg(args, 2, "market index"));
            string policyId = RequireArg(args, 3, "policy ID");
            string side = RequireArg(args, 4, "yes|no").ToLowerInvariant();
            ulong adaAmount = ulong.Parse(RequireArg(args, 5, "ADA amount")) * 1_000_000;

            bool betYes = side switch
            {
                "yes" => true,
                "no" => false,
                _ => throw new ArgumentException("Side must be 'yes' or 'no'")
            };

            await marketService.PlaceBet(marketTx, marketIdx, policyId, betYes, adaAmount);
            break;
        }

        case "resolve":
        {
            string marketTx = RequireArg(args, 1, "market tx hash");
            ulong marketIdx = ulong.Parse(RequireArg(args, 2, "market index"));
            string policyId = RequireArg(args, 3, "policy ID");
            await marketService.ResolveMarket(marketTx, marketIdx, policyId);
            break;
        }

        case "claim":
        {
            string marketTx = RequireArg(args, 1, "market tx hash");
            ulong marketIdx = ulong.Parse(RequireArg(args, 2, "market index"));
            string policyId = RequireArg(args, 3, "policy ID");
            long burnAmount = long.Parse(RequireArg(args, 4, "burn amount (lovelace)"));
            await marketService.Claim(marketTx, marketIdx, policyId, burnAmount);
            break;
        }

        case "price":
        {
            string feedId = args.Length > 1 ? args[1] : settings.FeedId;
            long price = await priceService.GetCurrentPrice(feedId);
            int exp = settings.Exponent;
            double displayPrice = price * Math.Pow(10, exp);
            Console.WriteLine($"  {feedId}: {displayPrice:F2} (raw: {price}, exp: {exp})");
            break;
        }

        // ── Help ─────────────────────────────────────────────────────────
        case "help":
        default:
            PrintHelp();
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\n  Error: {ex.Message}");
    if (ex.InnerException is not null)
        Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
    return 1;
}

return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────

static string? Env(string name, string? defaultValue = null)
{
    return Environment.GetEnvironmentVariable(name) ?? defaultValue;
}

static string EnvRequired(string name)
{
    return Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Environment variable {name} is required");
}

static string RequireArg(string[] args, int index, string name)
{
    if (args.Length <= index)
        throw new ArgumentException($"Missing argument: {name}");
    return args[index];
}

static void PrintHelp()
{
    Console.WriteLine("""
    Prediction Market CLI

    Deploy (one-time setup):
      deploy-market                              Deploy market reference script
      deploy-oracle <nft_policy_hex>             Deploy oracle reference script
      register-oracle <script_hash> [deploy_tx] [deploy_idx]  Register oracle stake credential
      create-oracle-state [feed_id]              Create oracle state UTxO

    Market operations:
      create [feed_id] [seed_ada]                Create market (default: BTC/USD, 100 ADA)
      bet <market_tx> <idx> <policy> yes|no <ada>  Place a bet
      resolve <market_tx> <idx> <policy>         Resolve market with oracle
      claim <market_tx> <idx> <policy> <burn_amount>  Claim winnings
      price [feed_id]                            Check current price

    Environment variables:
      BLOCKFROST_API_KEY    Blockfrost project API key (required)
      WALLET_MNEMONIC       24-word mnemonic (required)
      NETWORK               Preview|Preprod|Mainnet (default: Preview)
      ORACLE_SECRET_KEY     Ed25519 secret key hex for oracle signing
      FEED_ID               Default feed (default: BTC/USD)
      MARKET_DEPLOY_TX_HASH / MARKET_DEPLOY_INDEX
      ORACLE_DEPLOY_TX_HASH / ORACLE_DEPLOY_INDEX
      ORACLE_NFT_POLICY_ID
      ORACLE_STATE_TX_HASH  / ORACLE_STATE_INDEX
    """);
}
