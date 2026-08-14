# AWL Tebex Delivery

Fail-closed Torch plugin for the five fixed AWL Space Engineers credit packages.

The plugin is deliberately disabled by default. It does not own Tebex package IDs. WordPress owns the package-ID mapping so IDs can be changed without rebuilding the Torch plugin. The plugin independently validates the signed delivery metadata, fixed package custom-data/name/amount contract, Steam64 recipient, server identity, item type, quantity, and automated fulfillment mode.

## Native delivery path

All game state is accessed on the Torch game thread with native APIs from the installed server build:

- `MyAPIGateway.Players.TryGetIdentityId(steam64)` resolves Steam64 to the canonical Space Engineers IdentityId.
- `MyBankingSystem.Static.TryGetAccountInfo(identityId, out account)` reads the native bank balance.
- `MyBankingSystem.ChangeBalance(identityId, amount)` performs the server-side balance change.

No reflection is used.

## Idempotency and crash safety

For each delivery the plugin:

1. Validates all immutable delivery fields before any write.
2. Reads the native balance.
3. Persists a durable journal state of `delivering` before entering the write-critical section.
4. Re-resolves Steam64 and rechecks the exact pre-write balance.
5. Applies `ChangeBalance` once.
6. Reads the native balance again and requires `after == before + amount`.
7. Persists `delivered` before acknowledging the WordPress lease.

Any exception or uncertain result after the write starts is persisted as `review`. `delivering` and `review` rows are never replayed automatically. If the bridge acknowledgement is lost after a confirmed write, a later claim is acknowledged from the local `delivered` journal without changing the balance again.

## Configuration

Environment variables:

```text
SE_TEBEX_DELIVERY_ENABLED=false
SE_TEBEX_DELIVERY_ENDPOINT=https://awlgaming.net/wp-json/awl/v1/tebex/deliveries
AWL_API_KEY=
AWL_SECRET=
SE_TEBEX_SERVER_SLUG=space-engineers
SE_TEBEX_SERVER_NAME=AWL Space Engineers Shared Economy
SE_TEBEX_JOURNAL_PATH=
SE_TEBEX_DELIVERY_POLL_INTERVAL=30
SE_TEBEX_DELIVERY_REQUEST_TIMEOUT=10
SE_TEBEX_GAME_THREAD_TIMEOUT_MS=10000
SE_TEBEX_MAX_AMOUNT=5000000
```

`AWL_API_KEY` and `AWL_SECRET` are required only when delivery is enabled and must be supplied through the service/runtime secret configuration, never committed to the repository.

## Activation rule

Do not set `SE_TEBEX_DELIVERY_ENABLED=true` and do not change the WordPress Space Engineers catalog from `adapter_pending` to `automated` until the plugin has been installed and an isolated end-to-end acceptance test has verified the exact before/change/after flow on the target server.