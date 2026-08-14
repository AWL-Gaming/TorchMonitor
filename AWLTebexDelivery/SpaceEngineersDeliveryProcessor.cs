using System;
using System.Globalization;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.ModAPI;
using Torch.API;

namespace AWLTebexDelivery
{
    internal sealed class SpaceEngineersDeliveryProcessor
    {
        private readonly ITorchBase _torch;
        private readonly TebexDeliveryConfig _config;
        private readonly DeliveryJournal _journal;

        public SpaceEngineersDeliveryProcessor(ITorchBase torch, TebexDeliveryConfig config, DeliveryJournal journal)
        {
            _torch = torch;
            _config = config;
            _journal = journal;
        }

        public DeliveryDecision Execute(RemoteDelivery delivery)
        {
            string validationError;
            ulong steamId;
            if (!ValidateDelivery(delivery, out steamId, out validationError))
                return DeliveryDecision.Review("Rejected invalid SE delivery: " + validationError + ". No banking write was attempted.");

            var prepared = _journal.Prepare(delivery);
            if (prepared.State == JournalPrepareState.Delivered)
                return DeliveryDecision.Delivered(prepared.Detail);
            if (prepared.State == JournalPrepareState.Review)
                return DeliveryDecision.Review(prepared.Detail);

            BalanceSnapshot before;
            try
            {
                before = ReadBalance(steamId);
            }
            catch (Exception error)
            {
                _journal.ResetClaimed(delivery, "Pre-write SE balance read failed; no banking write was attempted.");
                return DeliveryDecision.Retry("SE balance could not be read before delivery; no banking write was attempted: " + SafeException(error));
            }

            if (before.IdentityId == 0)
            {
                _journal.ResetClaimed(delivery, "Steam64 did not resolve to a Space Engineers identity; no banking write was attempted.");
                return DeliveryDecision.Retry("SE Steam64 is not currently resolvable to a player identity; no banking write was attempted.");
            }
            if (!before.AccountFound)
            {
                _journal.ResetClaimed(delivery, "Space Engineers bank account was not found; no banking write was attempted.");
                return DeliveryDecision.Retry("SE bank account does not exist for the resolved identity; no banking write was attempted.");
            }

            _journal.MarkDelivering(delivery, before.Balance);
            WriteResult result;
            try
            {
                result = WriteAndVerify(steamId, before.IdentityId, before.Balance, delivery.Amount);
            }
            catch (WriteAttemptException error)
            {
                if (!error.WriteStarted)
                {
                    _journal.ResetClaimed(delivery, "SE banking precondition changed before the write; no banking write was attempted.");
                    return DeliveryDecision.Retry("SE banking precondition changed before delivery; no banking write was attempted: " + SafeException(error));
                }

                var detail = "SE banking result is uncertain and automatic replay is blocked: " + SafeException(error);
                _journal.MarkReview(delivery, before.Balance, error.AfterBalance, detail);
                return DeliveryDecision.Review(detail);
            }
            catch (Exception error)
            {
                var detail = "SE banking result is uncertain and automatic replay is blocked: " + SafeException(error);
                _journal.MarkReview(delivery, before.Balance, null, detail);
                return DeliveryDecision.Review(detail);
            }

            long expectedAfter;
            try
            {
                expectedAfter = checked(before.Balance + delivery.Amount);
            }
            catch (OverflowException)
            {
                var detail = "SE balance overflowed during verification; automatic replay is blocked.";
                _journal.MarkReview(delivery, before.Balance, result.AfterBalance, detail);
                return DeliveryDecision.Review(detail);
            }

            if (result.AfterBalance != expectedAfter)
            {
                var detail = string.Format(CultureInfo.InvariantCulture,
                    "SE balance verification failed; automatic replay is blocked. Before: {0}; expected: {1}; observed: {2}.",
                    before.Balance, expectedAfter, result.AfterBalance);
                _journal.MarkReview(delivery, before.Balance, result.AfterBalance, detail);
                return DeliveryDecision.Review(detail);
            }

            var deliveredDetail = string.Format(CultureInfo.InvariantCulture,
                "Confirmed {0} Space Credits delivered to Steam64 {1}. Balance: {2} -> {3}.",
                delivery.Amount, delivery.PlayerId, before.Balance, result.AfterBalance);
            _journal.MarkDelivered(delivery, before.Balance, result.AfterBalance, deliveredDetail);
            return DeliveryDecision.Delivered(deliveredDetail);
        }

        internal bool ValidateDelivery(RemoteDelivery delivery, out ulong steamId, out string error)
        {
            steamId = 0;
            error = string.Empty;
            if (delivery == null) { error = "delivery is missing"; return false; }
            if (delivery.Id <= 0) { error = "delivery ID is invalid"; return false; }
            if (!IsHexToken(delivery.ClaimToken, 64)) { error = "claim token is invalid"; return false; }
            if (string.IsNullOrWhiteSpace(delivery.TransactionId) || delivery.TransactionId.Length > 128) { error = "transaction ID is invalid"; return false; }
            if (delivery.PackageId <= 0) { error = "package ID is invalid"; return false; }
            if (!string.Equals(delivery.ServerSlug, DeliveryContract.ServerSlug, StringComparison.Ordinal)) { error = "server slug is not allowlisted"; return false; }
            if (!string.Equals(delivery.ServerName, DeliveryContract.ServerName, StringComparison.Ordinal)) { error = "server name is not allowlisted"; return false; }
            if (!string.Equals(delivery.ItemId, DeliveryContract.ItemId, StringComparison.Ordinal)) { error = "item ID is not allowlisted"; return false; }
            if (!string.Equals(delivery.FulfillmentMode, DeliveryContract.FulfillmentMode, StringComparison.Ordinal)) { error = "delivery is not marked for automated fulfillment"; return false; }
            if (delivery.Quantity != 1) { error = "fixed SE packages require quantity 1"; return false; }
            if (!DeliveryContract.TryParseSteamId64(delivery.PlayerId, out steamId)) { error = "recipient is not a valid Steam64 individual account ID"; return false; }

            var contract = DeliveryContract.GetPackage(delivery.PackageCustomData);
            if (contract == null) { error = "package custom data is not allowlisted"; return false; }
            if (!string.Equals(delivery.ProductName, contract.ProductName, StringComparison.Ordinal)) { error = "product name does not match the allowlisted contract"; return false; }
            if (delivery.Amount != contract.Amount || delivery.Amount <= 0 || delivery.Amount > _config.MaxAmount) { error = "credit amount does not match the allowlisted contract"; return false; }
            return true;
        }

        private BalanceSnapshot ReadBalance(ulong steamId)
        {
            if (_torch.GameState != TorchGameState.Loaded)
                throw new InvalidOperationException("Space Engineers session is not loaded.");

            var snapshot = new BalanceSnapshot();
            _torch.InvokeBlocking(() =>
            {
                var players = MyAPIGateway.Players;
                if (players == null) return;
                snapshot.IdentityId = players.TryGetIdentityId(steamId);
                if (snapshot.IdentityId == 0) return;
                MyAccountInfo account = default(MyAccountInfo);
                snapshot.AccountFound = MyBankingSystem.Static != null && MyBankingSystem.Static.TryGetAccountInfo(snapshot.IdentityId, out account);
                if (snapshot.AccountFound) snapshot.Balance = account.Balance;
            }, _config.GameThreadTimeoutMs, "AWLTebexDelivery.ReadBalance");
            return snapshot;
        }

        private WriteResult WriteAndVerify(ulong steamId, long expectedIdentityId, long expectedBefore, long amount)
        {
            if (_torch.GameState != TorchGameState.Loaded)
                throw new WriteAttemptException("Space Engineers session unloaded before the write.", false, null, null);

            var result = new WriteResult();
            Exception actionError = null;
            bool writeStarted = false;
            long? observedAfter = null;
            try
            {
                _torch.InvokeBlocking(() =>
                {
                    try
                    {
                        var players = MyAPIGateway.Players;
                        if (players == null) throw new InvalidOperationException("Space Engineers player collection is unavailable.");
                        var identityId = players.TryGetIdentityId(steamId);
                        if (identityId == 0 || identityId != expectedIdentityId)
                            throw new PreWriteConditionException("Steam64 resolved to a different or missing identity before the write.");

                        MyAccountInfo account = default(MyAccountInfo);
                        if (MyBankingSystem.Static == null || !MyBankingSystem.Static.TryGetAccountInfo(identityId, out account))
                            throw new PreWriteConditionException("Space Engineers bank account disappeared before the write.");
                        if (account.Balance != expectedBefore)
                            throw new PreWriteConditionException("Space Engineers balance changed before the write.");

                        writeStarted = true;
                        MyBankingSystem.ChangeBalance(identityId, amount);

                        MyAccountInfo afterAccount = default(MyAccountInfo);
                        if (MyBankingSystem.Static == null || !MyBankingSystem.Static.TryGetAccountInfo(identityId, out afterAccount))
                            throw new InvalidOperationException("Space Engineers bank account could not be read after the write.");
                        observedAfter = afterAccount.Balance;
                        result.AfterBalance = afterAccount.Balance;
                    }
                    catch (Exception error)
                    {
                        actionError = error;
                    }
                }, _config.GameThreadTimeoutMs, "AWLTebexDelivery.ChangeBalance");
            }
            catch (Exception error)
            {
                throw new WriteAttemptException("Game-thread banking operation failed: " + SafeException(error), writeStarted, observedAfter, error);
            }

            if (actionError != null)
                throw new WriteAttemptException(SafeException(actionError), writeStarted, observedAfter, actionError);
            if (!writeStarted)
                throw new WriteAttemptException("Banking write did not start.", false, observedAfter, null);
            return result;
        }

        private static bool IsHexToken(string value, int length)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != length) return false;
            foreach (var character in value)
            {
                if (!Uri.IsHexDigit(character)) return false;
            }
            return true;
        }

        private static string SafeException(Exception error)
        {
            if (error == null) return "unknown error";
            var text = error.GetType().Name + ": " + (error.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 400 ? text : text.Substring(0, 400);
        }

        private sealed class BalanceSnapshot
        {
            public long IdentityId;
            public bool AccountFound;
            public long Balance;
        }

        private sealed class WriteResult
        {
            public long AfterBalance;
        }

        private sealed class PreWriteConditionException : Exception
        {
            public PreWriteConditionException(string message) : base(message) { }
        }

        private sealed class WriteAttemptException : Exception
        {
            public WriteAttemptException(string message, bool writeStarted, long? afterBalance, Exception inner)
                : base(message, inner)
            {
                WriteStarted = writeStarted;
                AfterBalance = afterBalance;
            }

            public bool WriteStarted { get; }
            public long? AfterBalance { get; }
        }
    }
}
