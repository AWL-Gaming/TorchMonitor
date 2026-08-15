using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace AWLTebexDelivery
{
    internal static class DeliveryContract
    {
        public const string ServerSlug = "space-engineers";
        public const string ServerName = "AWL Space Engineers Shared Economy";
        public const string ItemId = "SpaceCredit";
        public const string FulfillmentMode = "automated";
        public const long AbsoluteMaxAmount = 5_000_000L;
        public const ulong SteamId64Base = 76561197960265728UL;
        public const ulong SteamId64Max = SteamId64Base + uint.MaxValue;

        private static readonly IReadOnlyDictionary<string, PackageContract> Packages =
            new Dictionary<string, PackageContract>(StringComparer.Ordinal)
            {
                ["space_credits_250000"] = new PackageContract("250,000 Space Credits", 250_000L),
                ["space_credits_600000"] = new PackageContract("600,000 Space Credits", 600_000L),
                ["space_credits_1250000"] = new PackageContract("1,250,000 Space Credits", 1_250_000L),
                ["space_credits_2500000"] = new PackageContract("2,500,000 Space Credits", 2_500_000L),
                ["space_credits_5000000"] = new PackageContract("5,000,000 Space Credits", 5_000_000L),
            };

        public static PackageContract GetPackage(string customData)
        {
            PackageContract contract;
            return customData != null && Packages.TryGetValue(customData, out contract) ? contract : null;
        }

        public static bool TryParseSteamId64(string value, out ulong steamId)
        {
            steamId = 0;
            value = (value ?? string.Empty).Trim();
            if (value.Length != 17 || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out steamId))
                return false;
            return steamId >= SteamId64Base && steamId <= SteamId64Max;
        }
    }

    internal sealed class PackageContract
    {
        public PackageContract(string productName, long amount)
        {
            ProductName = productName;
            Amount = amount;
        }

        public string ProductName { get; }
        public long Amount { get; }
    }

    internal sealed class TebexDeliveryConfig
    {
        public string Endpoint { get; private set; }
        public string ApiKey { get; private set; }
        public string Secret { get; private set; }
        public string ServerSlug { get; private set; }
        public string ServerName { get; private set; }
        public string JournalPath { get; private set; }
        public TimeSpan PollInterval { get; private set; }
        public TimeSpan RequestTimeout { get; private set; }
        public int GameThreadTimeoutMs { get; private set; }
        public long MaxAmount { get; private set; }
        public bool Enabled { get; private set; }

        public static TebexDeliveryConfig Load(string assemblyDirectory)
        {
            var credentials = LoadCredentialOverlay();
            var config = new TebexDeliveryConfig
            {
                Endpoint = Read("SE_TEBEX_DELIVERY_ENDPOINT", "https://awlgaming.net/wp-json/awl/v1/tebex/deliveries"),
                ApiKey = Read("AWL_API_KEY", credentials.ApiKey),
                Secret = Read("AWL_SECRET", credentials.Secret),
                ServerSlug = Read("SE_TEBEX_SERVER_SLUG", DeliveryContract.ServerSlug),
                ServerName = Read("SE_TEBEX_SERVER_NAME", DeliveryContract.ServerName),
                JournalPath = Read("SE_TEBEX_JOURNAL_PATH", Path.Combine(assemblyDirectory, "AWLTebexDelivery.journal.json")),
                PollInterval = TimeSpan.FromSeconds(ReadInt("SE_TEBEX_DELIVERY_POLL_INTERVAL", 30, 5, 900)),
                RequestTimeout = TimeSpan.FromSeconds(ReadInt("SE_TEBEX_DELIVERY_REQUEST_TIMEOUT", 10, 2, 60)),
                GameThreadTimeoutMs = ReadInt("SE_TEBEX_GAME_THREAD_TIMEOUT_MS", 10000, 1000, 60000),
                MaxAmount = ReadLong("SE_TEBEX_MAX_AMOUNT", DeliveryContract.AbsoluteMaxAmount, 1, DeliveryContract.AbsoluteMaxAmount),
                Enabled = ReadBool("SE_TEBEX_DELIVERY_ENABLED", false),
            };
            config.Validate();
            return config;
        }

        private void Validate()
        {
            Uri endpoint;
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out endpoint)
                || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(endpoint.Host, "awlgaming.net", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(endpoint.AbsolutePath.TrimEnd('/'), "/wp-json/awl/v1/tebex/deliveries", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(endpoint.Query)
                || !string.IsNullOrEmpty(endpoint.Fragment)
                || !string.IsNullOrEmpty(endpoint.UserInfo))
                throw new InvalidDataException("SE_TEBEX_DELIVERY_ENDPOINT must be the allowlisted awlgaming.net HTTPS delivery endpoint.");

            Endpoint = Endpoint.TrimEnd('/');
            if (!string.Equals(ServerSlug, DeliveryContract.ServerSlug, StringComparison.Ordinal))
                throw new InvalidDataException("SE_TEBEX_SERVER_SLUG must be space-engineers.");
            if (!string.Equals(ServerName, DeliveryContract.ServerName, StringComparison.Ordinal))
                throw new InvalidDataException("SE_TEBEX_SERVER_NAME must exactly match the AWL shared-economy server identity.");
            if (string.IsNullOrWhiteSpace(JournalPath))
                throw new InvalidDataException("SE_TEBEX_JOURNAL_PATH cannot be empty.");
            JournalPath = Path.GetFullPath(JournalPath);
            if (Enabled && (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Secret)))
                throw new InvalidDataException("AWL_API_KEY and AWL_SECRET are required when SE Tebex delivery is enabled.");
        }

        private const string DefaultCredentialFile = @"C:\AWL\Tebex\SecureRuntime\space-engineers-tebex.env";

        private static CredentialOverlay LoadCredentialOverlay()
        {
            var configuredPath = Environment.GetEnvironmentVariable("SE_TEBEX_CREDENTIAL_FILE");
            var explicitPath = !string.IsNullOrWhiteSpace(configuredPath);
            var path = explicitPath
                ? configuredPath.Trim()
                : Path.DirectorySeparatorChar == '\\' ? DefaultCredentialFile : string.Empty;

            if (string.IsNullOrWhiteSpace(path))
                return new CredentialOverlay();

            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                if (explicitPath)
                    throw new InvalidDataException("SE_TEBEX_CREDENTIAL_FILE does not exist.");
                return new CredentialOverlay();
            }

            return ParseCredentialFile(path);
        }

        internal static CredentialOverlay ParseCredentialFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("SE Tebex credential path cannot be empty.");

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sourceLine in File.ReadAllLines(Path.GetFullPath(path)))
            {
                var line = (sourceLine ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    throw new InvalidDataException("SE Tebex credential file contains a malformed entry.");

                var name = line.Substring(0, separator).Trim();
                if (!string.Equals(name, "AWL_API_KEY", StringComparison.Ordinal)
                    && !string.Equals(name, "AWL_SECRET", StringComparison.Ordinal))
                    throw new InvalidDataException("SE Tebex credential file contains an unsupported key.");
                if (values.ContainsKey(name))
                    throw new InvalidDataException("SE Tebex credential file contains a duplicate key.");

                var value = line.Substring(separator + 1).Trim();
                if (value.Length >= 2
                    && ((value[0] == '"' && value[value.Length - 1] == '"')
                        || (value[0] == '\'' && value[value.Length - 1] == '\'')))
                    value = value.Substring(1, value.Length - 2);
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidDataException("SE Tebex credential file contains an empty value.");

                values.Add(name, value);
            }

            string apiKey;
            string secret;
            values.TryGetValue("AWL_API_KEY", out apiKey);
            values.TryGetValue("AWL_SECRET", out secret);
            return new CredentialOverlay(apiKey, secret);
        }

        private static string Read(string name, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static bool ReadBool(string name, bool fallback)
        {
            bool parsed;
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : bool.TryParse(value.Trim(), out parsed) ? parsed : fallback;
        }

        private static int ReadInt(string name, int fallback, int min, int max)
        {
            int parsed;
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed < min || parsed > max)
                throw new InvalidDataException(name + " is outside the allowed range.");
            return parsed;
        }

        private static long ReadLong(string name, long fallback, long min, long max)
        {
            long parsed;
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed < min || parsed > max)
                throw new InvalidDataException(name + " is outside the allowed range.");
            return parsed;
        }
    }

    internal sealed class CredentialOverlay
    {
        public CredentialOverlay(string apiKey = null, string secret = null)
        {
            ApiKey = apiKey ?? string.Empty;
            Secret = secret ?? string.Empty;
        }

        public string ApiKey { get; }
        public string Secret { get; }
    }

    internal sealed class ClaimEnvelope
    {
        [JsonProperty("delivery")]
        public RemoteDelivery Delivery { get; set; }
    }

    internal sealed class RemoteDelivery
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("claim_token")] public string ClaimToken { get; set; }
        [JsonProperty("transaction_id")] public string TransactionId { get; set; }
        [JsonProperty("package_id")] public long PackageId { get; set; }
        [JsonProperty("package_custom_data")] public string PackageCustomData { get; set; }
        [JsonProperty("product_name")] public string ProductName { get; set; }
        [JsonProperty("fulfillment_mode")] public string FulfillmentMode { get; set; }
        [JsonProperty("server_slug")] public string ServerSlug { get; set; }
        [JsonProperty("server_name")] public string ServerName { get; set; }
        [JsonProperty("player_id")] public string PlayerId { get; set; }
        [JsonProperty("item_id")] public string ItemId { get; set; }
        [JsonProperty("quantity")] public long Quantity { get; set; }
        [JsonProperty("amount")] public long Amount { get; set; }
    }

    internal enum DeliveryStatus
    {
        Delivered,
        Retry,
        Review,
    }

    internal sealed class DeliveryDecision
    {
        private DeliveryDecision(DeliveryStatus status, string detail)
        {
            Status = status;
            Detail = SanitizeDetail(detail);
        }

        public DeliveryStatus Status { get; }
        public string Detail { get; }
        public string StatusName => Status == DeliveryStatus.Delivered ? "delivered" : Status == DeliveryStatus.Retry ? "retry" : "review";

        public static DeliveryDecision Delivered(string detail) => new DeliveryDecision(DeliveryStatus.Delivered, detail);
        public static DeliveryDecision Retry(string detail) => new DeliveryDecision(DeliveryStatus.Retry, detail);
        public static DeliveryDecision Review(string detail) => new DeliveryDecision(DeliveryStatus.Review, detail);

        private static string SanitizeDetail(string value)
        {
            value = (value ?? string.Empty).Replace('\0', ' ').Trim();
            return value.Length <= 1500 ? value : value.Substring(0, 1500);
        }
    }
}
