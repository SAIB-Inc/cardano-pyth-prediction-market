use aiken/collection/dict
use aiken/collection/list
use aiken/collection/pairs
use aiken/interval.{Finite}
use cardano/address.{Script}
use cardano/assets
use cardano/transaction.{InlineDatum, OutputReference, Transaction, Withdraw,
  find_input}
use prediction_market/types.{MarketDatum, No, SignedPriceFeed, Yes}

// Token names
pub const yes_token: ByteArray = "YES"

pub const no_token: ByteArray = "NO"

// ─── Bet validation ──────────────────────────────────────────────────────────

pub fn validate_bet(
  datum: MarketDatum,
  direction: types.BetDirection,
  amount: Int,
  spend_out_ref: OutputReference,
  tx: Transaction,
) -> Bool {
  // Market must not be resolved
  expect !datum.resolved

  // Amount must be positive
  expect amount > 0

  // Must be before resolution time
  expect Finite(upper) = tx.validity_range.upper_bound.bound_type
  expect upper <= datum.resolution_time

  // Calculate tokens out via constant product formula
  let (tokens_out, new_datum) =
    when direction is {
      Yes -> {
        let tokens = datum.yes_reserve - datum.k / (datum.no_reserve + amount)
        expect tokens > 0
        let new =
          MarketDatum {
            ..datum,
            yes_reserve: datum.yes_reserve - tokens,
            no_reserve: datum.no_reserve + amount,
            total_yes_minted: datum.total_yes_minted + tokens,
            total_ada: datum.total_ada + amount,
          }
        (tokens, new)
      }
      No -> {
        let tokens = datum.no_reserve - datum.k / (datum.yes_reserve + amount)
        expect tokens > 0
        let new =
          MarketDatum {
            ..datum,
            yes_reserve: datum.yes_reserve + amount,
            no_reserve: datum.no_reserve - tokens,
            total_no_minted: datum.total_no_minted + tokens,
            total_ada: datum.total_ada + amount,
          }
        (tokens, new)
      }
    }

  // Find continuing output at script address with updated datum
  expect Some(spend_input) = find_input(tx.inputs, spend_out_ref)
  let script_address = spend_input.output.address

  expect Some(continuing_output) =
    tx.outputs
      |> list.find(fn(output) { output.address == script_address })

  // Verify continuing output has correct datum
  expect InlineDatum(out_datum) = continuing_output.datum
  expect output_market_datum: MarketDatum = out_datum
  expect output_market_datum == new_datum

  // Verify correct tokens are minted
  let expected_token_name =
    when direction is {
      Yes -> yes_token
      No -> no_token
    }

  let minted =
    tx.mint
      |> assets.tokens(datum.token_policy)
      |> dict.get(expected_token_name)

  expect Some(minted_qty) = minted
  expect minted_qty == tokens_out

  // Verify ADA increased by bet amount
  let input_lovelace = assets.lovelace_of(spend_input.output.value)
  let output_lovelace = assets.lovelace_of(continuing_output.value)
  expect output_lovelace >= input_lovelace + amount

  True
}

// ─── Resolve validation ──────────────────────────────────────────────────────

pub fn validate_resolve(
  datum: MarketDatum,
  spend_out_ref: OutputReference,
  tx: Transaction,
) -> Bool {
  // Market must not already be resolved
  expect !datum.resolved

  // Must be after resolution time
  expect Finite(lower) = tx.validity_range.lower_bound.bound_type
  expect lower >= datum.resolution_time

  // Oracle withdrawal must exist (withdraw-0 pattern)
  expect
    pairs.has_key(tx.withdrawals, Script(datum.oracle_script_hash))

  // Oracle state NFT must be in reference inputs
  expect
    tx.reference_inputs
      |> list.any(
          fn(input) {
            assets.quantity_of(
              input.output.value,
              datum.oracle_nft_policy,
              "Oracle State",
            ) > 0
          },
        )

  // Extract price from oracle withdrawal redeemer
  expect Some(oracle_redeemer) =
    tx.redeemers
      |> pairs.get_first(Withdraw(Script(datum.oracle_script_hash)))
  expect signed_feed: SignedPriceFeed = oracle_redeemer
  let oracle_price = signed_feed.data.price

  // Determine winning side
  let winning_side =
    if oracle_price > datum.target_price {
      Yes
    } else {
      No
    }

  // Creator must sign (hackathon simplification)
  expect
    tx.extra_signatories |> list.has(datum.creator)

  // Verify continuing output
  expect Some(spend_input) = find_input(tx.inputs, spend_out_ref)
  let script_address = spend_input.output.address

  let expected_datum =
    MarketDatum {
      ..datum,
      resolved: True,
      winning_side: Some(winning_side),
    }

  expect Some(continuing_output) =
    tx.outputs
      |> list.find(fn(output) { output.address == script_address })

  expect InlineDatum(out_datum) = continuing_output.datum
  expect output_market_datum: MarketDatum = out_datum
  expect output_market_datum == expected_datum

  // Value must be preserved (no ADA removed during resolve)
  let input_lovelace = assets.lovelace_of(spend_input.output.value)
  let output_lovelace = assets.lovelace_of(continuing_output.value)
  expect output_lovelace >= input_lovelace

  True
}

// ─── Claim validation ────────────────────────────────────────────────────────

pub fn validate_claim(
  datum: MarketDatum,
  burn_amount: Int,
  spend_out_ref: OutputReference,
  tx: Transaction,
) -> Bool {
  // Market must be resolved
  expect datum.resolved
  expect Some(winning_side) = datum.winning_side
  expect burn_amount > 0

  // Determine winning token name and total minted
  let (winning_token, total_winning_minted) =
    when winning_side is {
      Yes -> (yes_token, datum.total_yes_minted)
      No -> (no_token, datum.total_no_minted)
    }

  // Calculate payout
  let payout = burn_amount * datum.total_ada / total_winning_minted

  // Verify winning tokens are burned
  let burned =
    tx.mint
      |> assets.tokens(datum.token_policy)
      |> dict.get(winning_token)

  expect Some(burned_amount) = burned
  expect burned_amount == -burn_amount

  // Verify continuing output with reduced values
  expect Some(spend_input) = find_input(tx.inputs, spend_out_ref)
  let script_address = spend_input.output.address

  let expected_datum =
    when winning_side is {
      Yes ->
        MarketDatum {
          ..datum,
          total_ada: datum.total_ada - payout,
          total_yes_minted: datum.total_yes_minted - burn_amount,
        }
      No ->
        MarketDatum {
          ..datum,
          total_ada: datum.total_ada - payout,
          total_no_minted: datum.total_no_minted - burn_amount,
        }
    }

  // If all winning tokens are claimed, no continuing output needed
  if total_winning_minted == burn_amount {
    // Last claimer — gets whatever remains, no continuing output required
    True
  } else {
    expect Some(continuing_output) =
      tx.outputs
        |> list.find(fn(output) { output.address == script_address })

    expect InlineDatum(out_datum) = continuing_output.datum
    expect output_market_datum: MarketDatum = out_datum
    expect output_market_datum == expected_datum

    // Verify ADA decreased by exactly payout
    let input_lovelace = assets.lovelace_of(spend_input.output.value)
    let output_lovelace = assets.lovelace_of(continuing_output.value)
    expect output_lovelace >= input_lovelace - payout

    True
  }
}

// ─── Cancel validation ───────────────────────────────────────────────────────

pub fn validate_cancel(datum: MarketDatum, tx: Transaction) -> Bool {
  // Market must not be resolved
  expect !datum.resolved

  // Creator must sign
  expect
    tx.extra_signatories |> list.has(datum.creator)

  True
}

// ─── Mint validation ─────────────────────────────────────────────────────────

pub fn validate_mint_tokens(
  one_shot: OutputReference,
  policy_id: assets.PolicyId,
  tx: Transaction,
) -> Bool {
  let one_shot_consumed =
    tx.inputs
      |> list.any(fn(input) { input.output_reference == one_shot })

  let has_market_spend =
    tx.inputs
      |> list.any(
          fn(input) {
            input.output.address.payment_credential == Script(policy_id)
          },
        )

  or {
    one_shot_consumed,
    has_market_spend,
  }
}

pub fn validate_burn_tokens(
  policy_id: assets.PolicyId,
  tx: Transaction,
) -> Bool {
  let mint_dict = tx.mint |> assets.tokens(policy_id)
  dict.foldl(mint_dict, True, fn(_key, qty, acc) { acc && qty < 0 })
}
