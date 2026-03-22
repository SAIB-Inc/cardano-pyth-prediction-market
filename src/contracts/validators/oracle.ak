use aiken/cbor
use aiken/collection/list
use aiken/crypto
use aiken/interval.{Finite}
use cardano/address.{Credential}
use cardano/assets
use cardano/certificate.{Certificate, RegisterCredential}
use cardano/transaction.{InlineDatum, Transaction}
use prediction_market/types.{OracleDatum, SignedPriceFeed}

/// Maximum feed age: 5 minutes in milliseconds
const max_feed_age: Int = 300_000

/// Mock oracle withdraw validator.
/// Verifies Ed25519 signed price feeds against a trusted public key
/// stored in the oracle state UTxO (identified by NFT in reference inputs).
validator oracle(oracle_nft_policy: ByteArray) {
  withdraw(signed_feed: SignedPriceFeed, _account: Credential, self: Transaction) {
    // Find oracle state UTxO in reference inputs (identified by NFT)
    let oracle_datum = find_oracle_datum(self, oracle_nft_policy)

    // Verify Ed25519 signature over CBOR-serialised feed data
    let sig_valid =
      crypto.verify_ed25519_signature(
        oracle_datum.trusted_pub_key,
        cbor.serialise(signed_feed.data),
        signed_feed.signature,
      )

    // Check feed name matches expected feed_id from oracle state
    let name_valid = signed_feed.data.name == oracle_datum.feed_id

    // Check timestamp freshness against tx validity range upper bound
    let timestamp_valid = {
      expect Finite(upper) = self.validity_range.upper_bound.bound_type
      upper - signed_feed.data.timestamp <= max_feed_age
    }

    and {
      sig_valid?,
      name_valid?,
      timestamp_valid?,
    }
  }

  publish(_redeemer: Data, certificate: Certificate, _self: Transaction) {
    when certificate is {
      RegisterCredential { .. } -> True
      _ -> fail
    }
  }

  else(_) {
    fail
  }
}

/// Find the oracle state datum from reference inputs by looking for the NFT
fn find_oracle_datum(tx: Transaction, nft_policy: ByteArray) -> OracleDatum {
  expect Some(oracle_input) =
    tx.reference_inputs
      |> list.find(
          fn(input) {
            assets.quantity_of(input.output.value, nft_policy, "Oracle State") > 0
          },
        )
  expect InlineDatum(raw_datum) = oracle_input.output.datum
  expect datum: OracleDatum = raw_datum
  datum
}
