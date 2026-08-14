using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Torch;
using Torch.API;
using Torch.API.Plugins;

namespace AWLTebexDelivery
{
    public sealed class AWLTebexDeliveryPlugin : TorchPluginBase
    {
        private static readonly ILogger Log = LogManager.GetCurrentClassLogger();
        private ITorchBase _torch;
        private TebexDeliveryConfig _config;
        private DeliveryJournal _journal;
        private TebexApiClient _api;
        private SpaceEngineersDeliveryProcessor _processor;
        private CancellationTokenSource _cancellation;
        private Task _loopTask;

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            _torch = torch ?? throw new ArgumentNullException(nameof(torch));
            try
            {
                var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
                _config = TebexDeliveryConfig.Load(assemblyDirectory);
                if (!_config.Enabled)
                {
                    Log.Info("AWL Tebex Space Engineers delivery is disabled. Set SE_TEBEX_DELIVERY_ENABLED=true only after the bridge catalog is production-enabled.");
                    return;
                }

                _journal = new DeliveryJournal(_config.JournalPath);
                _api = new TebexApiClient(_config);
                _processor = new SpaceEngineersDeliveryProcessor(_torch, _config, _journal);
                _cancellation = new CancellationTokenSource();
                _loopTask = Task.Run(() => RunLoopAsync(_cancellation.Token));
                Log.Info("AWL Tebex Space Engineers delivery worker initialized for {0}.", _config.ServerName);
            }
            catch (Exception error)
            {
                Log.Error(error, "AWL Tebex Space Engineers delivery failed closed during initialization.");
                DisposeRuntime();
            }
        }

        public override void Dispose()
        {
            DisposeRuntime();
            _torch = null;
            base.Dispose();
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_torch != null && _torch.GameState == TorchGameState.Loaded)
                    {
                        for (var index = 0; index < 8 && !cancellationToken.IsCancellationRequested; index++)
                        {
                            var delivery = await _api.ClaimAsync(cancellationToken).ConfigureAwait(false);
                            if (delivery == null) break;

                            DeliveryDecision decision;
                            try
                            {
                                decision = _processor.Execute(delivery);
                            }
                            catch (Exception error)
                            {
                                Log.Error(error, "SE Tebex delivery {0} failed before a safe completion decision could be produced.", delivery.Id);
                                break;
                            }

                            try
                            {
                                await _api.CompleteAsync(delivery, decision, cancellationToken).ConfigureAwait(false);
                                Log.Info("SE Tebex delivery {0} completed with status {1}.", delivery.Id, decision.StatusName);
                            }
                            catch (Exception error)
                            {
                                Log.Warn(error, "SE Tebex delivery {0} could not report completion. Local journal state prevents unsafe replay.", delivery.Id);
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    Log.Warn(error, "AWL Tebex Space Engineers polling cycle failed.");
                }

                try
                {
                    await Task.Delay(_config.PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private void DisposeRuntime()
        {
            var cancellation = _cancellation;
            _cancellation = null;
            if (cancellation != null)
            {
                try { cancellation.Cancel(); } catch { }
            }

            var loop = _loopTask;
            _loopTask = null;
            if (loop != null && !loop.IsCompleted)
            {
                try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            }

            if (_api != null)
            {
                _api.Dispose();
                _api = null;
            }
            if (cancellation != null) cancellation.Dispose();
            _processor = null;
            _journal = null;
            _config = null;
        }
    }
}