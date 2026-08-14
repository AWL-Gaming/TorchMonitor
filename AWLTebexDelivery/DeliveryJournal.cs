using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace AWLTebexDelivery
{
    internal enum JournalPrepareState
    {
        Ready,
        Delivered,
        Review,
    }

    internal sealed class JournalPrepareResult
    {
        public JournalPrepareResult(JournalPrepareState state, string detail)
        {
            State = state;
            Detail = detail ?? string.Empty;
        }

        public JournalPrepareState State { get; }
        public string Detail { get; }
    }

    internal sealed class DeliveryJournal
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private JournalDocument _document;

        public DeliveryJournal(string path)
        {
            _path = Path.GetFullPath(path);
            _document = Load();
        }

        public JournalPrepareResult Prepare(RemoteDelivery delivery)
        {
            lock (_gate)
            {
                JournalEntry entry;
                var key = delivery.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!_document.Deliveries.TryGetValue(key, out entry))
                {
                    _document.Deliveries[key] = JournalEntry.FromDelivery(delivery, "claimed", null, null, string.Empty);
                    Save();
                    return new JournalPrepareResult(JournalPrepareState.Ready, string.Empty);
                }

                if (!entry.ImmutableMatches(delivery))
                    return new JournalPrepareResult(JournalPrepareState.Review, "SE delivery ID was reused with different immutable fields; automatic delivery is blocked.");

                if (string.Equals(entry.State, "delivered", StringComparison.Ordinal))
                    return new JournalPrepareResult(JournalPrepareState.Delivered, string.IsNullOrWhiteSpace(entry.Detail) ? "Previously confirmed SE delivery acknowledged without replay." : entry.Detail);

                if (string.Equals(entry.State, "claimed", StringComparison.Ordinal))
                    return new JournalPrepareResult(JournalPrepareState.Ready, string.Empty);

                return new JournalPrepareResult(JournalPrepareState.Review,
                    string.IsNullOrWhiteSpace(entry.Detail)
                        ? "A previous SE banking write may have reached the game; automatic replay is blocked."
                        : entry.Detail);
            }
        }

        public void MarkDelivering(RemoteDelivery delivery, long beforeBalance)
        {
            lock (_gate)
            {
                var entry = RequireMatching(delivery);
                entry.State = "delivering";
                entry.BeforeBalance = beforeBalance;
                entry.AfterBalance = null;
                entry.Detail = "SE banking delivery entered the write-critical section.";
                entry.UpdatedAtUtc = DateTime.UtcNow;
                Save();
            }
        }

        public void ResetClaimed(RemoteDelivery delivery, string detail)
        {
            lock (_gate)
            {
                var entry = RequireMatching(delivery);
                entry.State = "claimed";
                entry.BeforeBalance = null;
                entry.AfterBalance = null;
                entry.Detail = detail ?? string.Empty;
                entry.UpdatedAtUtc = DateTime.UtcNow;
                Save();
            }
        }

        public void MarkReview(RemoteDelivery delivery, long? beforeBalance, long? afterBalance, string detail)
        {
            lock (_gate)
            {
                var entry = RequireMatching(delivery);
                entry.State = "review";
                entry.BeforeBalance = beforeBalance;
                entry.AfterBalance = afterBalance;
                entry.Detail = detail ?? string.Empty;
                entry.UpdatedAtUtc = DateTime.UtcNow;
                Save();
            }
        }

        public void MarkDelivered(RemoteDelivery delivery, long beforeBalance, long afterBalance, string detail)
        {
            lock (_gate)
            {
                var entry = RequireMatching(delivery);
                entry.State = "delivered";
                entry.BeforeBalance = beforeBalance;
                entry.AfterBalance = afterBalance;
                entry.Detail = detail ?? string.Empty;
                entry.UpdatedAtUtc = DateTime.UtcNow;
                Save();
            }
        }

        private JournalEntry RequireMatching(RemoteDelivery delivery)
        {
            JournalEntry entry;
            var key = delivery.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_document.Deliveries.TryGetValue(key, out entry))
                throw new InvalidOperationException("SE Tebex journal entry disappeared.");
            if (!entry.ImmutableMatches(delivery))
                throw new InvalidOperationException("SE Tebex journal immutable fields changed.");
            return entry;
        }

        private JournalDocument Load()
        {
            if (!File.Exists(_path)) return new JournalDocument();
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var document = JsonConvert.DeserializeObject<JournalDocument>(json);
            if (document == null || document.Deliveries == null)
                throw new InvalidDataException("SE Tebex journal is invalid; automatic delivery is disabled until it is repaired.");
            return document;
        }

        private void Save()
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            var backup = _path + ".bak";
            var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(_document, Formatting.Indented));
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(_path))
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(temp, _path, backup, true);
                if (File.Exists(backup)) File.Delete(backup);
            }
            else
            {
                File.Move(temp, _path);
            }
        }

        private sealed class JournalDocument
        {
            public JournalDocument()
            {
                Deliveries = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
            }

            [JsonProperty("deliveries")]
            public Dictionary<string, JournalEntry> Deliveries { get; set; }
        }

        private sealed class JournalEntry
        {
            [JsonProperty("delivery_id")] public long DeliveryId { get; set; }
            [JsonProperty("transaction_id")] public string TransactionId { get; set; }
            [JsonProperty("package_id")] public long PackageId { get; set; }
            [JsonProperty("package_custom_data")] public string PackageCustomData { get; set; }
            [JsonProperty("product_name")] public string ProductName { get; set; }
            [JsonProperty("player_id")] public string PlayerId { get; set; }
            [JsonProperty("item_id")] public string ItemId { get; set; }
            [JsonProperty("quantity")] public long Quantity { get; set; }
            [JsonProperty("amount")] public long Amount { get; set; }
            [JsonProperty("server_slug")] public string ServerSlug { get; set; }
            [JsonProperty("server_name")] public string ServerName { get; set; }
            [JsonProperty("state")] public string State { get; set; }
            [JsonProperty("before_balance")] public long? BeforeBalance { get; set; }
            [JsonProperty("after_balance")] public long? AfterBalance { get; set; }
            [JsonProperty("detail")] public string Detail { get; set; }
            [JsonProperty("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }

            public static JournalEntry FromDelivery(RemoteDelivery delivery, string state, long? before, long? after, string detail)
            {
                return new JournalEntry
                {
                    DeliveryId = delivery.Id,
                    TransactionId = delivery.TransactionId,
                    PackageId = delivery.PackageId,
                    PackageCustomData = delivery.PackageCustomData,
                    ProductName = delivery.ProductName,
                    PlayerId = delivery.PlayerId,
                    ItemId = delivery.ItemId,
                    Quantity = delivery.Quantity,
                    Amount = delivery.Amount,
                    ServerSlug = delivery.ServerSlug,
                    ServerName = delivery.ServerName,
                    State = state,
                    BeforeBalance = before,
                    AfterBalance = after,
                    Detail = detail ?? string.Empty,
                    UpdatedAtUtc = DateTime.UtcNow,
                };
            }

            public bool ImmutableMatches(RemoteDelivery delivery)
            {
                return DeliveryId == delivery.Id
                    && string.Equals(TransactionId, delivery.TransactionId, StringComparison.Ordinal)
                    && PackageId == delivery.PackageId
                    && string.Equals(PackageCustomData, delivery.PackageCustomData, StringComparison.Ordinal)
                    && string.Equals(ProductName, delivery.ProductName, StringComparison.Ordinal)
                    && string.Equals(PlayerId, delivery.PlayerId, StringComparison.Ordinal)
                    && string.Equals(ItemId, delivery.ItemId, StringComparison.Ordinal)
                    && Quantity == delivery.Quantity
                    && Amount == delivery.Amount
                    && string.Equals(ServerSlug, delivery.ServerSlug, StringComparison.Ordinal)
                    && string.Equals(ServerName, delivery.ServerName, StringComparison.Ordinal);
            }
        }
    }
}