use cardano/assets
use cardano/transaction.{OutputReference, Transaction}
use prediction_market/market_validation.{validate_cancel}
use prediction_market/mocks.{
  mock_creator_vkh, mock_market_datum, mock_market_hash, mock_market_input,
  mock_transaction, mock_tx_id,
}
use prediction_market/types.{MarketDatum, Yes}

fn mock_spend_ref() -> OutputReference {
  OutputReference { transaction_id: mock_tx_id, output_index: 0 }
}

// ── Success cases ────────────────────────────────────────────────────────────

test cancel_with_creator_sig() {
  let datum = mock_market_datum()
  let tx =
    Transaction {
      ..mock_transaction(),
      inputs: [mock_market_input(datum.total_ada)],
      mint: assets.zero |> assets.add(mock_market_hash, "", -1),
      extra_signatories: [mock_creator_vkh],
    }
  validate_cancel(datum, mock_spend_ref(), tx)
}

// ── Failure cases ────────────────────────────────────────────────────────────

test cancel_without_sig_fails() fail {
  let datum = mock_market_datum()
  let tx =
    Transaction {
      ..mock_transaction(),
      inputs: [mock_market_input(datum.total_ada)],
      mint: assets.zero |> assets.add(mock_market_hash, "", -1),
    }
  validate_cancel(datum, mock_spend_ref(), tx)
}

test cancel_resolved_market_fails() fail {
  let datum =
    MarketDatum {
      ..mock_market_datum(),
      resolved: True,
      winning_side: Some(Yes),
    }
  let tx =
    Transaction {
      ..mock_transaction(),
      inputs: [mock_market_input(datum.total_ada)],
      mint: assets.zero |> assets.add(mock_market_hash, "", -1),
      extra_signatories: [mock_creator_vkh],
    }
  validate_cancel(datum, mock_spend_ref(), tx)
}

test cancel_wrong_signer_fails() fail {
  let datum = mock_market_datum()
  let wrong_vkh = #"ffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
  let tx =
    Transaction {
      ..mock_transaction(),
      inputs: [mock_market_input(datum.total_ada)],
      mint: assets.zero |> assets.add(mock_market_hash, "", -1),
      extra_signatories: [wrong_vkh],
    }
  validate_cancel(datum, mock_spend_ref(), tx)
}

test cancel_without_state_thread_burn_fails() fail {
  let datum = mock_market_datum()
  let tx =
    Transaction {
      ..mock_transaction(),
      inputs: [mock_market_input(datum.total_ada)],
      extra_signatories: [mock_creator_vkh],
    }
  validate_cancel(datum, mock_spend_ref(), tx)
}
