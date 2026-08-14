using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AWLTebexDelivery
{
    internal sealed class TebexApiClient : IDisposable
    {
        private const int MaxResponseBytes = 64 * 1024;
        private readonly TebexDeliveryConfig _config;
        private readonly HttpClient _http;

        public TebexApiClient(TebexDeliveryConfig config)
        {
            _config = config;
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            _http = new HttpClient(handler) { Timeout = config.RequestTimeout };
        }

        public async Task<RemoteDelivery> ClaimAsync(CancellationToken cancellationToken)
        {
            var timestamp = UnixTimestamp();
            var payload = new { server_slug = _config.ServerSlug, timestamp };
            var envelope = await SignedPostAsync<ClaimEnvelope>(_config.Endpoint + "/claim", payload, timestamp, cancellationToken).ConfigureAwait(false);
            return envelope == null ? null : envelope.Delivery;
        }

        public async Task CompleteAsync(RemoteDelivery delivery, DeliveryDecision decision, CancellationToken cancellationToken)
        {
            var timestamp = UnixTimestamp();
            var payload = new
            {
                delivery_id = delivery.Id,
                claim_token = delivery.ClaimToken,
                status = decision.StatusName,
                detail = decision.Detail,
                timestamp,
            };
            var envelope = await SignedPostAsync<CompletionEnvelope>(_config.Endpoint + "/complete", payload, timestamp, cancellationToken).ConfigureAwait(false);
            if (envelope == null || !envelope.Ok)
                throw new InvalidOperationException("SE Tebex completion was not accepted by the bridge.");
        }

        private async Task<T> SignedPostAsync<T>(string endpoint, object payload, long timestamp, CancellationToken cancellationToken)
        {
            var body = JsonConvert.SerializeObject(payload, Formatting.None);
            var signature = Sign(timestamp, body, _config.Secret);
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.TryAddWithoutValidation("X-AWL-Bot-Key", _config.ApiKey);
                request.Headers.TryAddWithoutValidation("X-AWL-Signature", signature);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length > MaxResponseBytes)
                        throw new InvalidOperationException("SE Tebex endpoint returned an oversized response.");
                    var text = Encoding.UTF8.GetString(bytes);
                    if (response.StatusCode < HttpStatusCode.OK || response.StatusCode >= HttpStatusCode.MultipleChoices)
                        throw new HttpRequestException("SE Tebex endpoint returned HTTP " + (int)response.StatusCode + ".");
                    return JsonConvert.DeserializeObject<T>(text);
                }
            }
        }

        internal static string Sign(long timestamp, string body, string secret)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? string.Empty)))
            {
                var payload = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + (body ?? string.Empty));
                var hash = hmac.ComputeHash(payload);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static long UnixTimestamp()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private sealed class CompletionEnvelope
        {
            [JsonProperty("ok")]
            public bool Ok { get; set; }
        }
    }
}