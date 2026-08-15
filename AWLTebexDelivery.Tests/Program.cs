using System;
using System.IO;

namespace AWLTebexDelivery
{
    internal static class Program
    {
        private static int _assertions;

        private static void Main()
        {
            TestSteam64Validation();
            TestHmacVector();
            TestCredentialFileFallbackAndEnvironmentPrecedence();
            TestCredentialFileRejectsUnsafeEntries();
            TestDeliveryContract();
            TestJournalCrashBlocksReplay();
            TestJournalDeliveredAcknowledgesWithoutReplay();
            TestJournalImmutableMismatchBlocksReplay();
            Console.WriteLine("AWL Tebex Delivery tests passed: " + _assertions + " assertions.");
        }

        private static void TestSteam64Validation()
        {
            ulong steam;
            Assert(DeliveryContract.TryParseSteamId64("76561197960265729", out steam), "valid individual Steam64 should pass");
            Assert(steam == 76561197960265729UL, "Steam64 should parse exactly");
            Assert(!DeliveryContract.TryParseSteamId64("76561197960265727", out steam), "Steam64 below individual base should fail");
            Assert(!DeliveryContract.TryParseSteamId64("76561202255233024", out steam), "Steam64 above account range should fail");
            Assert(!DeliveryContract.TryParseSteamId64("not-steam", out steam), "nonnumeric Steam64 should fail");
        }

        private static void TestHmacVector()
        {
            var signature = TebexApiClient.Sign(123, "{\"x\":1}", "secret");
            Assert(signature == "e026d9fb244519ab60b34977ea31915edb1040d96ec9a5ef93c1c024b174c1e4", "HMAC signature vector should be stable");
        }

        private static void TestCredentialFileFallbackAndEnvironmentPrecedence()
        {
            var path = Path.Combine(Path.GetTempPath(), "awl-se-tebex-credentials-" + Guid.NewGuid().ToString("N") + ".env");
            var oldEnabled = Environment.GetEnvironmentVariable("SE_TEBEX_DELIVERY_ENABLED");
            var oldPath = Environment.GetEnvironmentVariable("SE_TEBEX_CREDENTIAL_FILE");
            var oldApiKey = Environment.GetEnvironmentVariable("AWL_API_KEY");
            var oldSecret = Environment.GetEnvironmentVariable("AWL_SECRET");
            try
            {
                File.WriteAllText(path, "# dedicated SE credentials\nAWL_API_KEY=file-key\nAWL_SECRET='file-secret'\n");
                Environment.SetEnvironmentVariable("SE_TEBEX_DELIVERY_ENABLED", "true");
                Environment.SetEnvironmentVariable("SE_TEBEX_CREDENTIAL_FILE", path);
                Environment.SetEnvironmentVariable("AWL_API_KEY", null);
                Environment.SetEnvironmentVariable("AWL_SECRET", null);

                var fromFile = TebexDeliveryConfig.Load(Path.GetTempPath());
                Assert(fromFile.Enabled, "credential-file config should remain enabled");
                Assert(fromFile.ApiKey == "file-key", "credential file should supply AWL_API_KEY");
                Assert(fromFile.Secret == "file-secret", "credential file should unquote AWL_SECRET");

                Environment.SetEnvironmentVariable("AWL_API_KEY", "env-key");
                Environment.SetEnvironmentVariable("AWL_SECRET", "env-secret");
                var fromEnvironment = TebexDeliveryConfig.Load(Path.GetTempPath());
                Assert(fromEnvironment.ApiKey == "env-key", "environment AWL_API_KEY must override credential file");
                Assert(fromEnvironment.Secret == "env-secret", "environment AWL_SECRET must override credential file");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SE_TEBEX_DELIVERY_ENABLED", oldEnabled);
                Environment.SetEnvironmentVariable("SE_TEBEX_CREDENTIAL_FILE", oldPath);
                Environment.SetEnvironmentVariable("AWL_API_KEY", oldApiKey);
                Environment.SetEnvironmentVariable("AWL_SECRET", oldSecret);
                Cleanup(path);
            }
        }

        private static void TestCredentialFileRejectsUnsafeEntries()
        {
            var path = Path.Combine(Path.GetTempPath(), "awl-se-tebex-credentials-invalid-" + Guid.NewGuid().ToString("N") + ".env");
            try
            {
                File.WriteAllText(path, "AWL_API_KEY=a\nAWL_SECRET=b\nUNEXPECTED=value\n");
                AssertThrowsInvalidData(() => TebexDeliveryConfig.ParseCredentialFile(path), "credential file must reject unsupported keys");
                File.WriteAllText(path, "AWL_API_KEY=a\nAWL_API_KEY=b\nAWL_SECRET=c\n");
                AssertThrowsInvalidData(() => TebexDeliveryConfig.ParseCredentialFile(path), "credential file must reject duplicate keys");
                File.WriteAllText(path, "AWL_API_KEY=a\nAWL_SECRET=\n");
                AssertThrowsInvalidData(() => TebexDeliveryConfig.ParseCredentialFile(path), "credential file must reject empty values");
            }
            finally { Cleanup(path); }
        }

        private static void AssertThrowsInvalidData(Action action, string message)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                Assert(true, message);
                return;
            }
            Assert(false, message);
        }

        private static void TestDeliveryContract()
        {
            var oldEnabled = Environment.GetEnvironmentVariable("SE_TEBEX_DELIVERY_ENABLED");
            try
            {
                Environment.SetEnvironmentVariable("SE_TEBEX_DELIVERY_ENABLED", "false");
                var config = TebexDeliveryConfig.Load(Path.GetTempPath());
                var processor = new SpaceEngineersDeliveryProcessor(null, config, null);
                var delivery = ValidDelivery(101);
                ulong steam;
                string error;
                Assert(processor.ValidateDelivery(delivery, out steam, out error), "valid fixed package should pass validation: " + error);
                delivery.Amount++;
                Assert(!processor.ValidateDelivery(delivery, out steam, out error), "tampered amount must fail validation");
                delivery = ValidDelivery(102);
                delivery.FulfillmentMode = "manual";
                Assert(!processor.ValidateDelivery(delivery, out steam, out error), "non-automated mode must fail validation");
                delivery = ValidDelivery(103);
                delivery.PackageId = 9999999;
                Assert(processor.ValidateDelivery(delivery, out steam, out error), "package ID may rotate when signed catalog metadata remains exact");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SE_TEBEX_DELIVERY_ENABLED", oldEnabled);
            }
        }

        private static void TestJournalCrashBlocksReplay()
        {
            var path = TempJournal("crash");
            try
            {
                var delivery = ValidDelivery(201);
                var journal = new DeliveryJournal(path);
                Assert(journal.Prepare(delivery).State == JournalPrepareState.Ready, "new delivery should be ready");
                journal.MarkDelivering(delivery, 1000);
                var reloaded = new DeliveryJournal(path);
                Assert(reloaded.Prepare(delivery).State == JournalPrepareState.Review, "crash after delivering marker must block automatic replay");
            }
            finally { Cleanup(path); }
        }

        private static void TestJournalDeliveredAcknowledgesWithoutReplay()
        {
            var path = TempJournal("delivered");
            try
            {
                var delivery = ValidDelivery(202);
                var journal = new DeliveryJournal(path);
                Assert(journal.Prepare(delivery).State == JournalPrepareState.Ready, "delivery should start ready");
                journal.MarkDelivering(delivery, 1000);
                journal.MarkDelivered(delivery, 1000, 251000, "confirmed");
                var reloaded = new DeliveryJournal(path);
                var result = reloaded.Prepare(delivery);
                Assert(result.State == JournalPrepareState.Delivered, "confirmed delivery must not replay after restart");
                Assert(result.Detail == "confirmed", "confirmed journal detail should survive restart");
            }
            finally { Cleanup(path); }
        }

        private static void TestJournalImmutableMismatchBlocksReplay()
        {
            var path = TempJournal("immutable");
            try
            {
                var original = ValidDelivery(203);
                var journal = new DeliveryJournal(path);
                Assert(journal.Prepare(original).State == JournalPrepareState.Ready, "original delivery should be ready");
                var tampered = ValidDelivery(203);
                tampered.PlayerId = "76561197960265730";
                Assert(journal.Prepare(tampered).State == JournalPrepareState.Review, "same delivery ID with changed recipient must be review-only");
            }
            finally { Cleanup(path); }
        }

        private static RemoteDelivery ValidDelivery(long id)
        {
            return new RemoteDelivery
            {
                Id = id,
                ClaimToken = new string('a', 64),
                TransactionId = "tbx-transaction-" + id,
                PackageId = 7602440,
                PackageCustomData = "space_credits_250000",
                ProductName = "250,000 Space Credits",
                FulfillmentMode = DeliveryContract.FulfillmentMode,
                ServerSlug = DeliveryContract.ServerSlug,
                ServerName = DeliveryContract.ServerName,
                PlayerId = "76561197960265729",
                ItemId = DeliveryContract.ItemId,
                Quantity = 1,
                Amount = 250000,
            };
        }

        private static string TempJournal(string label)
        {
            return Path.Combine(Path.GetTempPath(), "awl-se-tebex-" + label + "-" + Guid.NewGuid().ToString("N") + ".json");
        }

        private static void Cleanup(string path)
        {
            foreach (var candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                try { if (File.Exists(candidate)) File.Delete(candidate); } catch { }
            }
        }

        private static void Assert(bool condition, string message)
        {
            _assertions++;
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
        }
    }
}