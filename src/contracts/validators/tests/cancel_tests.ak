use cardano/transaction.{Transaction}
use prediction_market/market_validation.{validate_cancel}
use prediction_market/mocks.{mock_creator_vkh, mock_market_datum,
  mock_transaction}
use prediction_market/types.{MarketDatum, Yes}

// ── Success cases ────────────────────────────────────────────────────────────

test cancel_with_creator_sig() {
  let datum = mock_market_datum()
  let tx =
    Transaction { ..mock_transaction(), extra_signatories: [mock_creator_vkh] }
  validate_cancel(datum, tx)
}

// ── Failure cases ────────────────────────────────────────────────────────────

test cancel_without_sig_fails() fail {
  let datum = mock_market_datum()
  let tx = mock_transaction()
  validate_cancel(datum, tx)
}

test cancel_resolved_market_fails() fail {
  let datum =
    MarketDatum { ..mock_market_datum(), resolved: True, winning_side: Some(Yes) }
  let tx =
    Transaction { ..mock_transaction(), extra_signatories: [mock_creator_vkh] }
  validate_cancel(datum, tx)
}

test cancel_wrong_signer_fails() fail {
  let datum = mock_market_datum()
  let wrong_vkh =
    #"ffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
  let tx =
    Transaction { ..mock_transaction(), extra_signatories: [wrong_vkh] }
  validate_cancel(datum, tx)
}
