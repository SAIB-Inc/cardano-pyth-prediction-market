use aiken/interval
use cardano/address.{Script}
use cardano/assets
use cardano/transaction.{InlineDatum, Output, OutputReference, Transaction,
  Withdraw}
use prediction_market/market_validation.{validate_resolve}
use prediction_market/mocks.{
  mock_creator_vkh, mock_market_datum, mock_market_input, mock_oracle_hash,
  mock_oracle_ref_input, mock_script_address, mock_transaction, mock_tx_id,
}
use prediction_market/types.{MarketDatum, No, PriceFeed, SignedPriceFeed, Yes}

// ── Helpers ──────────────────────────────────────────────────────────────────

fn mock_spend_ref() -> OutputReference {
  OutputReference { transaction_id: mock_tx_id, output_index: 0 }
}

/// Build a signed price feed (signature is dummy — oracle validator not tested here)
fn mock_signed_feed(price: Int) -> SignedPriceFeed {
  SignedPriceFeed {
    data: PriceFeed { price: price, name: "BTC/USD", timestamp: 300_000 },
    signature: #"0000000000000000000000000000000000000000000000000000000000000000",
  }
}

/// Build a valid resolve transaction
fn resolve_tx(datum: MarketDatum, oracle_price: Int) -> Transaction {
  let winning_side =
    if oracle_price > datum.target_price {
      Some(Yes)
    } else {
      Some(No)
    }

  let resolved_datum =
    MarketDatum { ..datum, resolved: True, winning_side: winning_side }

  let signed_feed: Data = mock_signed_feed(oracle_price)

  Transaction {
    ..mock_transaction(),
    inputs: [mock_market_input(datum.total_ada)],
    reference_inputs: [mock_oracle_ref_input()],
    outputs: [
      Output {
        address: mock_script_address(),
        value: assets.from_lovelace(datum.total_ada),
        datum: InlineDatum(resolved_datum),
        reference_script: None,
      },
    ],
    withdrawals: [Pair(Script(mock_oracle_hash), 0)],
    redeemers: [Pair(Withdraw(Script(mock_oracle_hash)), signed_feed)],
    validity_range: interval.between(datum.resolution_time, datum.resolution_time + 100_000),
    extra_signatories: [mock_creator_vkh],
  }
}

// ── Success cases ────────────────────────────────────────────────────────────

test resolve_yes_wins() {
  let datum = mock_market_datum()
  // Price above target → YES wins
  let tx = resolve_tx(datum, datum.target_price + 1)
  validate_resolve(datum, mock_spend_ref(), tx)
}

test resolve_no_wins() {
  let datum = mock_market_datum()
  // Price below target → NO wins
  let tx = resolve_tx(datum, datum.target_price - 1)
  validate_resolve(datum, mock_spend_ref(), tx)
}

test resolve_equal_price_no_wins() {
  let datum = mock_market_datum()
  // Price equals target → NO wins (not strictly greater)
  let tx = resolve_tx(datum, datum.target_price)
  validate_resolve(datum, mock_spend_ref(), tx)
}

// ── Failure cases ────────────────────────────────────────────────────────────

test resolve_before_resolution_time_fails() fail {
  let datum = mock_market_datum()
  let tx =
    Transaction {
      ..resolve_tx(datum, datum.target_price + 1),
      // Lower bound before resolution time
      validity_range: interval.between(0, 100_000),
    }
  validate_resolve(datum, mock_spend_ref(), tx)
}

test resolve_already_resolved_fails() fail {
  let datum =
    MarketDatum { ..mock_market_datum(), resolved: True, winning_side: Some(Yes) }
  let tx = resolve_tx(datum, datum.target_price + 1)
  validate_resolve(datum, mock_spend_ref(), tx)
}

test resolve_missing_oracle_withdrawal_fails() fail {
  let datum = mock_market_datum()
  let tx =
    Transaction {
      ..resolve_tx(datum, datum.target_price + 1),
      withdrawals: [],
    }
  validate_resolve(datum, mock_spend_ref(), tx)
}

test resolve_missing_oracle_ref_input_fails() fail {
  let datum = mock_market_datum()
  let tx =
    Transaction {
      ..resolve_tx(datum, datum.target_price + 1),
      reference_inputs: [],
    }
  validate_resolve(datum, mock_spend_ref(), tx)
}

test resolve_missing_creator_sig_fails() fail {
  let datum = mock_market_datum()
  let tx =
    Transaction {
      ..resolve_tx(datum, datum.target_price + 1),
      extra_signatories: [],
    }
  validate_resolve(datum, mock_spend_ref(), tx)
}

test resolve_wrong_winning_side_fails() fail {
  let datum = mock_market_datum()
  // Price > target should be YES wins, but we set NO in output
  let wrong_datum =
    MarketDatum { ..datum, resolved: True, winning_side: Some(No) }

  let signed_feed: Data = mock_signed_feed(datum.target_price + 1)

  let tx =
    Transaction {
      ..mock_transaction(),
      inputs: [mock_market_input(datum.total_ada)],
      reference_inputs: [mock_oracle_ref_input()],
      outputs: [
        Output {
          address: mock_script_address(),
          value: assets.from_lovelace(datum.total_ada),
          datum: InlineDatum(wrong_datum),
          reference_script: None,
        },
      ],
      withdrawals: [Pair(Script(mock_oracle_hash), 0)],
      redeemers: [Pair(Withdraw(Script(mock_oracle_hash)), signed_feed)],
      validity_range: interval.between(datum.resolution_time, datum.resolution_time + 100_000),
      extra_signatories: [mock_creator_vkh],
    }
  validate_resolve(datum, mock_spend_ref(), tx)
}

test resolve_value_stolen_fails() fail {
  let datum = mock_market_datum()
  let resolved_datum =
    MarketDatum { ..datum, resolved: True, winning_side: Some(Yes) }
  let signed_feed: Data = mock_signed_feed(datum.target_price + 1)

  let tx =
    Transaction {
      ..mock_transaction(),
      inputs: [mock_market_input(datum.total_ada)],
      reference_inputs: [mock_oracle_ref_input()],
      outputs: [
        Output {
          address: mock_script_address(),
          // Less ADA in output than input
          value: assets.from_lovelace(datum.total_ada - 1_000_000),
          datum: InlineDatum(resolved_datum),
          reference_script: None,
        },
      ],
      withdrawals: [Pair(Script(mock_oracle_hash), 0)],
      redeemers: [Pair(Withdraw(Script(mock_oracle_hash)), signed_feed)],
      validity_range: interval.between(datum.resolution_time, datum.resolution_time + 100_000),
      extra_signatories: [mock_creator_vkh],
    }
  validate_resolve(datum, mock_spend_ref(), tx)
}
