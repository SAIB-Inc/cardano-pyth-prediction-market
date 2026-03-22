use cardano/assets.{PolicyId}
use cardano/transaction.{OutputReference, Transaction}
use prediction_market/market_validation.{validate_bet, validate_burn_tokens,
  validate_cancel, validate_claim, validate_mint_tokens, validate_resolve}
use prediction_market/types.{Bet, BurnTokens, Cancel, Claim, MarketAction,
  MarketDatum, MarketParams, MintAction, MintTokens, Resolve}

// ─── Market validator (dual spend + mint) ────────────────────────────────────

validator market(params: MarketParams) {
  spend(
    datum: Option<MarketDatum>,
    redeemer: MarketAction,
    spend_out_ref: OutputReference,
    self: Transaction,
  ) {
    expect Some(market_datum) = datum

    when redeemer is {
      Bet { direction, amount } ->
        validate_bet(market_datum, direction, amount, spend_out_ref, self)
      Resolve -> validate_resolve(market_datum, spend_out_ref, self)
      Claim { burn_amount } ->
        validate_claim(market_datum, burn_amount, spend_out_ref, self)
      Cancel -> validate_cancel(market_datum, self)
    }
  }

  mint(redeemer: MintAction, policy_id: PolicyId, self: Transaction) {
    when redeemer is {
      MintTokens -> validate_mint_tokens(params.one_shot, policy_id, self)
      BurnTokens -> validate_burn_tokens(policy_id, self)
    }
  }

  else(_) {
    fail
  }
}
