use cardano/assets.{PolicyId}
use cardano/transaction.{OutputReference}

/// price: raw integer price
/// name: feed identifier (e.g., "BTC/USD")
/// timestamp: POSIX ms
pub type PriceFeed {
  price: Int,
  name: ByteArray,
  timestamp: Int,
}

/// Signed price feed for Ed25519 oracle verification
pub type SignedPriceFeed {
  data: PriceFeed,
  signature: ByteArray,
}

// ── Oracle state datum ───────────────────────────────────────────────────────

/// Datum for the oracle state UTxO (holds trusted public key)
pub type OracleDatum {
  trusted_pub_key: ByteArray,
  feed_id: ByteArray,
}

// ── Market types ─────────────────────────────────────────────────────────────

pub type BetDirection {
  Yes
  No
}

pub type MarketDatum {
  creator: ByteArray,
  oracle_script_hash: ByteArray,
  oracle_nft_policy: PolicyId,
  feed_id: ByteArray,
  target_price: Int,
  exponent: Int,
  resolution_time: Int,
  token_policy: PolicyId,
  yes_reserve: Int,
  no_reserve: Int,
  k: Int,
  total_yes_minted: Int,
  total_no_minted: Int,
  total_ada: Int,
  resolved: Bool,
  winning_side: Option<BetDirection>,
}

// ── Redeemers ────────────────────────────────────────────────────────────────

pub type MarketAction {
  Bet { direction: BetDirection, amount: Int }
  Resolve
  Claim { burn_amount: Int }
}

pub type MintAction {
  MintTokens
  BurnTokens
}

// ── One-shot parameter ───────────────────────────────────────────────────────

/// Parameter for the market validator (makes each market's policy ID unique)
pub type MarketParams {
  one_shot: OutputReference,
}
