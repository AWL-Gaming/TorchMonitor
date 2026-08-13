using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace TorchMonitor
{
    public partial class TorchMonitorPlugin
    {
        private const string TebexExpectedItem = "SpaceCredit";
        private const string TebexExpectedSlug = "space-engineers";
        private const string TebexExpectedFulfillmentMode = "automated";
        private static readonly Logger TebexLog = LogManager.GetLogger("TebexCreditBridge");
        private readonly SemaphoreSlim _tebexDeliveryGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _tebexCancellation;
        private Task _tebexWorker;
        private TebexCreditBridgeConfig _tebexConfig;
        private string _tebexStoragePath;

        private void StartTebexCreditBridge()
        {
            try
            {
                _tebexStoragePath = Path.Combine(
                    Path.GetDirectoryName(GetType().Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                    "TebexCreditBridge");
                Directory.CreateDirectory(_tebexStoragePath);
                Directory.CreateDirectory(Path.Combine(_tebexStoragePath, "journal"));
                var configPath = Path.Combine(_tebexStoragePath, "config.json");
                if (!File.Exists(configPath))
                {
                    var template = new TebexCreditBridgeConfig();
                    WriteAtomic(configPath, JsonConvert.SerializeObject(template, Formatting.Indented));
                    TebexLog.Warn("Tebex credit bridge created a disabled config template at {0}", configPath);
                    return;
                }
                _tebexConfig = JsonConvert.DeserializeObject<TebexCreditBridgeConfig>(File.ReadAllText(configPath));
                ValidateConfig(_tebexConfig);
                if (_tebexConfig.RunStartupProbe || _tebexConfig.RunRoundTripProbe)
                {
                    Task.Run(() => RunStartupProbeAsync());
                }
                if (!_tebexConfig.Enabled)
                {
                    TebexLog.Info("Tebex credit bridge is disabled by configuration");
                    return;
                }
                _tebexCancellation = new CancellationTokenSource();
                _tebexWorker = Task.Run(() => TebexPollLoopAsync(_tebexCancellation.Token));
                TebexLog.Info(
                    "Tebex credit bridge started for {0}; poll interval {1}s; dry run {2}",
                    _tebexConfig.ServerName,
                    _tebexConfig.PollSeconds,
                    _tebexConfig.DryRun);
            }
            catch (Exception error)
            {
                TebexLog.Error(error, "Tebex credit bridge did not start");
            }
        }

        private void StopTebexCreditBridge()
        {
            var cancellation = _tebexCancellation;
            _tebexCancellation = null;
            if (cancellation != null)
            {
                try { cancellation.Cancel(); } catch { }
                try { if (_tebexWorker != null) _tebexWorker.Wait(TimeSpan.FromSeconds(5)); } catch { }
                cancellation.Dispose();
            }
            _tebexWorker = null;
        }

        private async Task RunStartupProbeAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            var resultPath = Path.Combine(_tebexStoragePath, "startup-probe-result.json");
            var probeId = string.IsNullOrWhiteSpace(_tebexConfig.ProbeId)
                ? "default"
                : Clean(_tebexConfig.ProbeId);
            try
            {
                if (File.Exists(resultPath))
                {
                    var existing = JsonConvert.DeserializeObject<TebexStartupProbeResult>(File.ReadAllText(resultPath));
                    if (existing != null && existing.Success && string.Equals(existing.ProbeId, probeId, StringComparison.Ordinal))
                    {
                        TebexLog.Info("Tebex credit startup probe {0} was already verified", probeId);
                        return;
                    }
                }
                var delivery = new TebexRemoteDelivery
                {
                    Id = -1,
                    ClaimToken = "startup-probe-not-a-queue-delivery",
                    TransactionId = "startup-probe-" + probeId,
                    PackageId = 1,
                    PackageCustomData = "space_credit_probe",
                    ProductName = "Space Credit startup probe",
                    FulfillmentMode = TebexExpectedFulfillmentMode,
                    ServerSlug = TebexExpectedSlug,
                    PlayerId = _tebexConfig.ProbeSteam64,
                    ItemId = TebexExpectedItem,
                    Quantity = 1,
                    Amount = 1,
                    ServerName = _tebexConfig.ServerName,
                };
                CreditMutationResult result;
                if (_tebexConfig.RunRoundTripProbe)
                {
                    result = await InvokeRoundTripProbeAsync(delivery).ConfigureAwait(false);
                }
                else
                {
                    if (!_tebexConfig.DryRun)
                        throw new InvalidOperationException("RunStartupProbe requires DryRun=true");
                    result = await InvokeCreditMutationAsync(delivery).ConfigureAwait(false);
                    if (result.MutationAttempted || result.Success || !result.Detail.StartsWith("Dry run only:", StringComparison.Ordinal))
                        throw new InvalidOperationException("Dry-run probe did not reach the expected no-write bank-account result: " + result.Detail);
                }
                var probeResult = new TebexStartupProbeResult
                {
                    ProbeId = probeId,
                    Steam64 = _tebexConfig.ProbeSteam64,
                    Mode = _tebexConfig.RunRoundTripProbe ? "round-trip" : "dry-run",
                    Success = true,
                    Detail = result.Detail,
                    BeforeBalance = result.BeforeBalance,
                    AfterBalance = result.AfterBalance,
                    CompletedAtUtc = DateTime.UtcNow,
                };
                WriteAtomic(resultPath, JsonConvert.SerializeObject(probeResult, Formatting.Indented));
                TebexLog.Info("Tebex credit startup probe passed: {0}", result.Detail);
            }
            catch (Exception error)
            {
                var probeResult = new TebexStartupProbeResult
                {
                    ProbeId = probeId,
                    Steam64 = _tebexConfig.ProbeSteam64,
                    Mode = _tebexConfig.RunRoundTripProbe ? "round-trip" : "dry-run",
                    Success = false,
                    Detail = Clean(error.Message),
                    CompletedAtUtc = DateTime.UtcNow,
                };
                try { WriteAtomic(resultPath, JsonConvert.SerializeObject(probeResult, Formatting.Indented)); } catch { }
                TebexLog.Error(error, "Tebex credit startup probe failed");
            }
        }

        private Task<CreditMutationResult> InvokeRoundTripProbeAsync(TebexRemoteDelivery delivery)
        {
            var completion = new TaskCompletionSource<CreditMutationResult>();
            if (MyAPIGateway.Utilities == null)
            {
                completion.SetResult(CreditMutationResult.Retry("Space Engineers game API is not ready; no probe write was attempted."));
                return completion.Task;
            }
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                long? beforeBalance = null;
                var credited = false;
                try
                {
                    ulong steamId;
                    if (!ulong.TryParse(delivery.PlayerId, NumberStyles.None, CultureInfo.InvariantCulture, out steamId) || delivery.PlayerId.Length != 17)
                        throw new InvalidOperationException("Round-trip probe Steam64 is invalid");
                    if (MySession.Static == null || MySession.Static.Players == null)
                        throw new InvalidOperationException("Space Engineers session is not ready");
                    var identityId = MySession.Static.Players.TryGetIdentityId(steamId);
                    if (identityId == 0)
                        throw new InvalidOperationException("Round-trip probe Steam64 has no server identity");
                    if (!MyBankingSystem.Static.TryGetAccountInfo(identityId, out var before))
                        throw new InvalidOperationException("Round-trip probe identity has no banking account");
                    beforeBalance = before.Balance;
                    var creditedBalance = checked(before.Balance + 1);
                    MyBankingSystem.ChangeBalance(identityId, 1);
                    credited = true;
                    if (!MyBankingSystem.Static.TryGetAccountInfo(identityId, out var afterCredit) || afterCredit.Balance != creditedBalance)
                        throw new InvalidOperationException("Round-trip probe could not verify the +1 credit stage");
                    MyBankingSystem.ChangeBalance(identityId, -1);
                    credited = false;
                    if (!MyBankingSystem.Static.TryGetAccountInfo(identityId, out var restored) || restored.Balance != before.Balance)
                        throw new InvalidOperationException("Round-trip probe could not verify balance restoration");
                    completion.SetResult(CreditMutationResult.Delivered(before.Balance, restored.Balance, string.Format(CultureInfo.InvariantCulture, "Round-trip probe passed for identity {0}: balance {1} -> {2} -> {1}.", identityId, before.Balance, afterCredit.Balance)));
                }
                catch (Exception error)
                {
                    if (credited && beforeBalance.HasValue)
                    {
                        try
                        {
                            ulong steamId;
                            if (ulong.TryParse(delivery.PlayerId, out steamId))
                            {
                                var identityId = MySession.Static.Players.TryGetIdentityId(steamId);
                                if (identityId != 0 && MyBankingSystem.Static.TryGetAccountInfo(identityId, out var current))
                                {
                                    var correction = beforeBalance.Value - current.Balance;
                                    if (correction != 0) MyBankingSystem.ChangeBalance(identityId, correction);
                                }
                            }
                        }
                        catch (Exception rollbackError)
                        {
                            TebexLog.Fatal(rollbackError, "Round-trip credit probe rollback failed");
                        }
                    }
                    completion.SetException(error);
                }
            });
            return completion.Task;
        }

        private async Task TebexPollLoopAsync(CancellationToken cancellationToken)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using (handler)
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_tebexConfig.RequestTimeoutSeconds) })
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        for (var index = 0; index < _tebexConfig.BatchSize; index++)
                        {
                            var delivery = await ClaimDeliveryAsync(http, cancellationToken).ConfigureAwait(false);
                            if (delivery == null) break;
                            await ProcessDeliveryAsync(http, delivery, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception error)
                    {
                        TebexLog.Warn(error, "Tebex credit poll failed");
                    }
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_tebexConfig.PollSeconds), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task<TebexRemoteDelivery> ClaimDeliveryAsync(HttpClient http, CancellationToken cancellationToken)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new Dictionary<string, object>
            {
                ["server_slug"] = _tebexConfig.ServerSlug,
                ["timestamp"] = timestamp,
            };
            var response = await SignedPostAsync<TebexClaimEnvelope>(
                http,
                _tebexConfig.QueueEndpoint.TrimEnd('/') + "/claim",
                timestamp,
                payload,
                cancellationToken).ConfigureAwait(false);
            return response == null ? null : response.Delivery;
        }

        private async Task ProcessDeliveryAsync(HttpClient http, TebexRemoteDelivery delivery, CancellationToken cancellationToken)
        {
            await _tebexDeliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string validationError;
                if (!ValidateDelivery(delivery, out validationError))
                {
                    await CompleteAsync(http, delivery, "review", "Rejected invalid Space Engineers delivery: " + validationError, cancellationToken).ConfigureAwait(false);
                    return;
                }
                var journalPath = JournalPath(delivery);
                var existing = ReadJournal(journalPath);
                if (existing != null)
                {
                    if (!JournalIdentityMatches(existing, delivery))
                    {
                        await CompleteAsync(http, delivery, "review", "Delivery ID was reused with different immutable fields; automatic replay is blocked.", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (string.Equals(existing.Status, "delivered", StringComparison.OrdinalIgnoreCase))
                    {
                        await CompleteAsync(http, delivery, "delivered", existing.Detail, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await CompleteAsync(http, delivery, "review", string.IsNullOrWhiteSpace(existing.Detail) ? "A previous Space Engineers credit write may have occurred; automatic replay is blocked." : existing.Detail, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var processing = TebexCreditJournal.From(delivery, "processing", "Credit delivery claimed; mutation not yet confirmed.");
                WriteJournal(journalPath, processing);
                var result = await InvokeCreditMutationAsync(delivery).ConfigureAwait(false);
                if (!result.Success)
                {
                    if (!result.MutationAttempted)
                    {
                        TryDelete(journalPath);
                        await CompleteAsync(http, delivery, "retry", result.Detail, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        processing.Status = "review";
                        processing.Detail = result.Detail;
                        processing.BeforeBalance = result.BeforeBalance;
                        processing.AfterBalance = result.AfterBalance;
                        processing.UpdatedAtUtc = DateTime.UtcNow;
                        WriteJournal(journalPath, processing);
                        await CompleteAsync(http, delivery, "review", result.Detail, cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                processing.Status = "delivered";
                processing.Detail = result.Detail;
                processing.BeforeBalance = result.BeforeBalance;
                processing.AfterBalance = result.AfterBalance;
                processing.UpdatedAtUtc = DateTime.UtcNow;
                WriteJournal(journalPath, processing);
                await CompleteAsync(http, delivery, "delivered", result.Detail, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                TebexLog.Error(error, "Tebex credit delivery {0} failed", delivery == null ? 0 : delivery.Id);
                if (delivery != null)
                {
                    try
                    {
                        await CompleteAsync(http, delivery, "review", "Space Engineers delivery failed after claim and requires staff review: " + Clean(error.Message), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception completionError)
                    {
                        TebexLog.Error(completionError, "Could not report Tebex credit delivery failure");
                    }
                }
            }
            finally
            {
                _tebexDeliveryGate.Release();
            }
        }

        private Task<CreditMutationResult> InvokeCreditMutationAsync(TebexRemoteDelivery delivery)
        {
            var completion = new TaskCompletionSource<CreditMutationResult>();
            if (MyAPIGateway.Utilities == null)
            {
                completion.SetResult(CreditMutationResult.Retry("Space Engineers game API is not ready; no credit write was attempted."));
                return completion.Task;
            }
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                try
                {
                    ulong steamId;
                    if (!ulong.TryParse(delivery.PlayerId, NumberStyles.None, CultureInfo.InvariantCulture, out steamId))
                    {
                        completion.SetResult(CreditMutationResult.Retry("Steam64 recipient is invalid; no credit write was attempted."));
                        return;
                    }
                    if (MySession.Static == null || MySession.Static.Players == null)
                    {
                        completion.SetResult(CreditMutationResult.Retry("Space Engineers session is not ready; no credit write was attempted."));
                        return;
                    }
                    var identityId = MySession.Static.Players.TryGetIdentityId(steamId);
                    if (identityId == 0)
                    {
                        completion.SetResult(CreditMutationResult.Retry("Steam64 recipient has no Space Engineers identity on this server; no credit write was attempted."));
                        return;
                    }
                    if (!MyBankingSystem.Static.TryGetAccountInfo(identityId, out var before))
                    {
                        completion.SetResult(CreditMutationResult.Retry("Recipient has no banking account; no credit write was attempted."));
                        return;
                    }
                    var expectedAfter = checked(before.Balance + delivery.Amount);
                    if (_tebexConfig.DryRun)
                    {
                        completion.SetResult(CreditMutationResult.Retry(string.Format(CultureInfo.InvariantCulture, "Dry run only: identity {0}, balance {1}, requested credit {2}; no write was attempted.", identityId, before.Balance, delivery.Amount)));
                        return;
                    }
                    MyBankingSystem.ChangeBalance(identityId, delivery.Amount);
                    if (!MyBankingSystem.Static.TryGetAccountInfo(identityId, out var after))
                    {
                        completion.SetResult(CreditMutationResult.Review(before.Balance, null, "Credit mutation was attempted but the resulting account could not be read."));
                        return;
                    }
                    if (after.Balance != expectedAfter)
                    {
                        completion.SetResult(CreditMutationResult.Review(before.Balance, after.Balance, string.Format(CultureInfo.InvariantCulture, "Credit mutation verification failed: expected balance {0}, observed {1}.", expectedAfter, after.Balance)));
                        return;
                    }
                    completion.SetResult(CreditMutationResult.Delivered(before.Balance, after.Balance, string.Format(CultureInfo.InvariantCulture, "Confirmed {0} Space Credits delivered to Steam64 {1}. Balance {2} -> {3}.", delivery.Amount, delivery.PlayerId, before.Balance, after.Balance)));
                }
                catch (Exception error)
                {
                    completion.SetResult(CreditMutationResult.Review(null, null, "Credit mutation raised an exception and requires review: " + Clean(error.Message)));
                }
            });
            return completion.Task;
        }

        private async Task CompleteAsync(HttpClient http, TebexRemoteDelivery delivery, string status, string detail, CancellationToken cancellationToken)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new Dictionary<string, object>
            {
                ["delivery_id"] = delivery.Id,
                ["claim_token"] = delivery.ClaimToken,
                ["status"] = status,
                ["detail"] = Clean(detail),
                ["timestamp"] = timestamp,
            };
            var response = await SignedPostAsync<TebexCompletionEnvelope>(
                http,
                _tebexConfig.QueueEndpoint.TrimEnd('/') + "/complete",
                timestamp,
                payload,
                cancellationToken).ConfigureAwait(false);
            if (response == null || !response.Ok) throw new InvalidOperationException("Tebex completion was not accepted");
        }

        private async Task<T> SignedPostAsync<T>(HttpClient http, string url, long timestamp, object payload, CancellationToken cancellationToken)
        {
            var body = JsonConvert.SerializeObject(payload, Formatting.None);
            var signature = HmacHex(_tebexConfig.Secret, timestamp.ToString(CultureInfo.InvariantCulture) + body);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("X-AWL-Bot-Key", _tebexConfig.ApiKey);
                request.Headers.TryAddWithoutValidation("X-AWL-Signature", signature);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (responseBody.Length > 65536) throw new InvalidOperationException("Tebex endpoint returned an oversized response");
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Tebex endpoint returned HTTP " + (int)response.StatusCode + ": " + Clean(responseBody));
                    return JsonConvert.DeserializeObject<T>(responseBody);
                }
            }
        }

        private bool ValidateDelivery(TebexRemoteDelivery delivery, out string error)
        {
            error = null;
            if (delivery == null || delivery.Id <= 0) error = "delivery ID is invalid";
            else if (string.IsNullOrWhiteSpace(delivery.ClaimToken) || delivery.ClaimToken.Length < 24) error = "claim token is invalid";
            else if (string.IsNullOrWhiteSpace(delivery.TransactionId)) error = "transaction ID is missing";
            else if (delivery.PackageId <= 0) error = "package ID is invalid";
            else if (!IsSafeCatalogKey(delivery.PackageCustomData)) error = "package custom data is missing or unsafe";
            else if (!IsSafeProductName(delivery.ProductName)) error = "product name is missing or unsafe";
            else if (!string.Equals(delivery.FulfillmentMode, TebexExpectedFulfillmentMode, StringComparison.Ordinal)) error = "delivery is not marked for automated fulfillment";
            else if (!string.Equals(delivery.ServerSlug, _tebexConfig.ServerSlug, StringComparison.Ordinal) || !string.Equals(delivery.ServerSlug, TebexExpectedSlug, StringComparison.Ordinal)) error = "server slug does not match configuration";
            else if (!string.Equals(delivery.ItemId, TebexExpectedItem, StringComparison.Ordinal)) error = "item is not allowlisted";
            else if (delivery.Quantity != 1) error = "Space Engineers credit packages require quantity 1";
            else if (!string.Equals(delivery.ServerName, _tebexConfig.ServerName, StringComparison.Ordinal)) error = "server name does not match configuration";
            else if (delivery.Amount <= 0 || delivery.Amount > _tebexConfig.MaxAmount) error = "amount is outside the configured limit";
            else { ulong parsed; if (!ulong.TryParse(delivery.PlayerId, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || delivery.PlayerId.Length != 17 || !delivery.PlayerId.StartsWith("7656119", StringComparison.Ordinal)) error = "Steam64 recipient is invalid"; }
            return error == null;
        }


        private static bool IsSafeCatalogKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
            return value.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '-' || character == '.' || character == ':');
        }

        private static bool IsSafeProductName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 160) return false;
            return value.All(character => !char.IsControl(character) && character != '<' && character != '>');
        }

        private string JournalPath(TebexRemoteDelivery delivery)
        {
            var identity = delivery.Id.ToString(CultureInfo.InvariantCulture) + ":" + delivery.TransactionId;
            var hash = Sha256Hex(identity).ToLowerInvariant();
            return Path.Combine(_tebexStoragePath, "journal", hash + ".json");
        }

        private static TebexCreditJournal ReadJournal(string path)
        {
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<TebexCreditJournal>(File.ReadAllText(path));
        }

        private static bool JournalIdentityMatches(TebexCreditJournal journal, TebexRemoteDelivery delivery)
        {
            return journal.DeliveryId == delivery.Id &&
                   string.Equals(journal.TransactionId, delivery.TransactionId, StringComparison.Ordinal) &&
                   journal.PackageId == delivery.PackageId &&
                   string.Equals(journal.PackageCustomData, delivery.PackageCustomData, StringComparison.Ordinal) &&
                   string.Equals(journal.ProductName, delivery.ProductName, StringComparison.Ordinal) &&
                   string.Equals(journal.FulfillmentMode, delivery.FulfillmentMode, StringComparison.Ordinal) &&
                   string.Equals(journal.ServerSlug, delivery.ServerSlug, StringComparison.Ordinal) &&
                   string.Equals(journal.PlayerId, delivery.PlayerId, StringComparison.Ordinal) &&
                   string.Equals(journal.ItemId, delivery.ItemId, StringComparison.Ordinal) &&
                   journal.Quantity == delivery.Quantity &&
                   journal.Amount == delivery.Amount &&
                   string.Equals(journal.ServerName, delivery.ServerName, StringComparison.Ordinal);
        }

        private static void WriteJournal(string path, TebexCreditJournal journal)
        {
            WriteAtomic(path, JsonConvert.SerializeObject(journal, Formatting.Indented));
        }

        private static void WriteAtomic(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                var backup = path + ".replace-backup";
                try { File.Replace(temporary, path, backup, true); }
                finally { TryDelete(backup); TryDelete(temporary); }
            }
            else
            {
                File.Move(temporary, path);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string HmacHex(string secret, string value)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
                return ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var filtered = new string(value.Where(character => !char.IsControl(character) || character == '\n' || character == '\t').Take(1500).ToArray());
            return filtered.Trim();
        }

        private static void ValidateConfig(TebexCreditBridgeConfig config)
        {
            if (config == null) throw new InvalidOperationException("Tebex credit config is invalid");
            Uri endpoint;
            if (!Uri.TryCreate(config.QueueEndpoint, UriKind.Absolute, out endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || !string.Equals(endpoint.Host, "awlgaming.net", StringComparison.OrdinalIgnoreCase) || endpoint.AbsolutePath.TrimEnd('/') != "/wp-json/awl/v1/tebex/deliveries") throw new InvalidOperationException("QueueEndpoint must be the allowlisted HTTPS awlgaming.net delivery endpoint");
            if (!string.Equals(config.ServerSlug, TebexExpectedSlug, StringComparison.Ordinal)) throw new InvalidOperationException("ServerSlug must be space-engineers");
            if (string.IsNullOrWhiteSpace(config.ServerName)) throw new InvalidOperationException("ServerName is required");
            if (config.Enabled && (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.Secret))) throw new InvalidOperationException("ApiKey and Secret are required when the queue poller is enabled");
            if ((config.RunStartupProbe || config.RunRoundTripProbe) && (string.IsNullOrWhiteSpace(config.ProbeSteam64) || config.ProbeSteam64.Length != 17 || !config.ProbeSteam64.All(char.IsDigit))) throw new InvalidOperationException("ProbeSteam64 must be a valid configured Steam64 account");
            if (config.RunStartupProbe && config.RunRoundTripProbe) throw new InvalidOperationException("Only one startup probe mode can be enabled at a time");
            if (config.RunStartupProbe && !config.DryRun) throw new InvalidOperationException("RunStartupProbe requires DryRun=true");
            if (config.RunRoundTripProbe && config.Enabled) throw new InvalidOperationException("RunRoundTripProbe requires the queue poller to remain disabled");
            if (config.PollSeconds < 5 || config.RequestTimeoutSeconds < 5 || config.BatchSize < 1 || config.BatchSize > 20 || config.MaxAmount < 1 || config.MaxAmount > 10000000) throw new InvalidOperationException("Tebex credit numeric configuration is invalid");
        }
    }

    internal sealed class TebexCreditBridgeConfig
    {
        public bool Enabled { get; set; }
        public bool DryRun { get; set; }
        public bool RunStartupProbe { get; set; }
        public bool RunRoundTripProbe { get; set; }
        public string ProbeSteam64 { get; set; } = "";
        public string ProbeId { get; set; } = "";
        public string QueueEndpoint { get; set; } = "https://awlgaming.net/wp-json/awl/v1/tebex/deliveries";
        public string ApiKey { get; set; } = "";
        public string Secret { get; set; } = "";
        public string ServerSlug { get; set; } = "space-engineers";
        public string ServerName { get; set; } = "AWL Space Engineers Shared Economy";
        public int PollSeconds { get; set; } = 30;
        public int RequestTimeoutSeconds { get; set; } = 15;
        public int BatchSize { get; set; } = 8;
        public long MaxAmount { get; set; } = 10000000;
    }

    internal sealed class TebexClaimEnvelope { [JsonProperty("delivery")] public TebexRemoteDelivery Delivery { get; set; } }
    internal sealed class TebexCompletionEnvelope { [JsonProperty("ok")] public bool Ok { get; set; } }
    internal sealed class TebexRemoteDelivery
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("claim_token")] public string ClaimToken { get; set; }
        [JsonProperty("transaction_id")] public string TransactionId { get; set; }
        [JsonProperty("package_id")] public long PackageId { get; set; }
        [JsonProperty("package_custom_data")] public string PackageCustomData { get; set; }
        [JsonProperty("product_name")] public string ProductName { get; set; }
        [JsonProperty("fulfillment_mode")] public string FulfillmentMode { get; set; }
        [JsonProperty("server_slug")] public string ServerSlug { get; set; }
        [JsonProperty("player_id")] public string PlayerId { get; set; }
        [JsonProperty("item_id")] public string ItemId { get; set; }
        [JsonProperty("quantity")] public long Quantity { get; set; }
        [JsonProperty("amount")] public long Amount { get; set; }
        [JsonProperty("server_name")] public string ServerName { get; set; }
    }

    internal sealed class TebexCreditJournal
    {
        public long DeliveryId { get; set; }
        public string TransactionId { get; set; }
        public long PackageId { get; set; }
        public string PackageCustomData { get; set; }
        public string ProductName { get; set; }
        public string FulfillmentMode { get; set; }
        public string ServerSlug { get; set; }
        public string PlayerId { get; set; }
        public string ItemId { get; set; }
        public long Quantity { get; set; }
        public long Amount { get; set; }
        public string ServerName { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public long? BeforeBalance { get; set; }
        public long? AfterBalance { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public static TebexCreditJournal From(TebexRemoteDelivery delivery, string status, string detail) { return new TebexCreditJournal { DeliveryId=delivery.Id,TransactionId=delivery.TransactionId,PackageId=delivery.PackageId,PackageCustomData=delivery.PackageCustomData,ProductName=delivery.ProductName,FulfillmentMode=delivery.FulfillmentMode,ServerSlug=delivery.ServerSlug,PlayerId=delivery.PlayerId,ItemId=delivery.ItemId,Quantity=delivery.Quantity,Amount=delivery.Amount,ServerName=delivery.ServerName,Status=status,Detail=detail,UpdatedAtUtc=DateTime.UtcNow }; }
    }

    internal sealed class TebexStartupProbeResult
    {
        public string ProbeId { get; set; }
        public string Steam64 { get; set; }
        public string Mode { get; set; }
        public bool Success { get; set; }
        public string Detail { get; set; }
        public long? BeforeBalance { get; set; }
        public long? AfterBalance { get; set; }
        public DateTime CompletedAtUtc { get; set; }
    }

    internal sealed class CreditMutationResult
    {
        public bool Success { get; private set; }
        public bool MutationAttempted { get; private set; }
        public long? BeforeBalance { get; private set; }
        public long? AfterBalance { get; private set; }
        public string Detail { get; private set; }
        public static CreditMutationResult Retry(string detail) { return new CreditMutationResult { Success=false,MutationAttempted=false,Detail=detail }; }
        public static CreditMutationResult Review(long? before, long? after, string detail) { return new CreditMutationResult { Success=false,MutationAttempted=true,BeforeBalance=before,AfterBalance=after,Detail=detail }; }
        public static CreditMutationResult Delivered(long before, long after, string detail) { return new CreditMutationResult { Success=true,MutationAttempted=true,BeforeBalance=before,AfterBalance=after,Detail=detail }; }
    }
}
