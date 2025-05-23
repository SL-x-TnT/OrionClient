using Equix;
using ILGPU.Runtime;
using NLog;
using OrionClientLib.Hashers;
using OrionClientLib.Modules.Models;
using OrionClientLib.Pools;
using OrionClientLib.Pools.Models;
using OrionEventLib.Events.Mining;
using Solnet.Wallet;
using Spectre.Console;
using System.Diagnostics;

namespace OrionClientLib.Modules
{
    public class MinerModule : IModule
    {
        private static readonly Logger _logger = LogManager.GetLogger("Main");

        public string Name { get; } = "Start Mining";

        private Data _currentData;
        private CancellationTokenSource _cts;
        private bool _stop = false;
        private Layout _uiLayout = null;
        private Table _hashrateTable = null;
        private Table _poolInfoTable = null;
        private bool _paused = false;

        public async Task<ExecuteResult> ExecuteAsync(Data data)
        {
            _currentData = data;

            return new ExecuteResult { Exited = _stop, Renderer = _uiLayout };
        }

        public async Task ExitAsync()
        {
            _logger.Log(LogLevel.Debug, $"Exiting out of miner module...");

            if (_currentData.Settings.PreventSleeping)
            {
                SystemInformation.AllowSleep();
            }

            IPool pool = _currentData.GetChosenPool();
            (IHasher cpuHasher, IHasher gpuHasher) = _currentData.GetChosenHasher();

            List<Task> tasks = new List<Task>();

            if (cpuHasher != null)
            {
                tasks.Add(cpuHasher.StopAsync());
            }

            if (gpuHasher != null)
            {
                tasks.Add(gpuHasher.StopAsync());
            }

            if (pool != null)
            {
                await pool.DisconnectAsync();

                pool.OnMinerUpdate -= Pool_OnMinerUpdate;
                pool.OnChallengeUpdate -= Pool_OnChallengeUpdate;
                pool.ResumeMining -= Pool_ResumeMining;
                pool.PauseMining -= Pool_PauseMining;

                if (cpuHasher != null)
                {
                    cpuHasher.OnHashrateUpdate -= Hasher_OnHashrateUpdate;
                }

                if (gpuHasher != null)
                {
                    gpuHasher.OnHashrateUpdate -= Hasher_OnHashrateUpdate;
                }
            }

            await Task.WhenAll(tasks);

            bool cpuEnabled = cpuHasher != null && cpuHasher is not DisabledHasher;
            bool gpuEnabled = gpuHasher != null && gpuHasher is not DisabledHasher;
            bool setAffinity = cpuEnabled && !gpuEnabled && _currentData.Settings.CPUSetting.AutoSetCPUAffinity;

            //TODO: Check for efficiency cores
            if (OperatingSystem.IsWindows() && setAffinity)
            {
                List<CoreInfo> coreInformation = SystemInformation.GetCoreInformation();

                //Only use physical cores
                if (_currentData.Settings.CPUSetting.CPUThreads > 0 && coreInformation.Count >= _currentData.Settings.CPUSetting.CPUThreads)
                {
                    nint processorMask = 0;
                    nint fullMask = 0;

                    coreInformation.ForEach(x =>
                    {
                        processorMask |= (nint)x.PhysicalMask;
                        fullMask |= (nint)x.FullMask;
                    });

                    Process currentProcess = Process.GetCurrentProcess();

                    if (currentProcess.ProcessorAffinity == processorMask)
                    {
                        currentProcess.ProcessorAffinity = fullMask;
                    }
                }
            }

            _cts.Cancel();
            _stop = true;
        }

        public async Task<(bool, string)> InitializeAsync(Data data)
        {
            _stop = false;
            _currentData = data;
            _cts = new CancellationTokenSource();

            IPool pool = _currentData.GetChosenPool();
            (IHasher cpuHasher, IHasher gpuHasher) = _currentData.GetChosenHasher();

            if (pool == null)
            {
                return (false, $"No pool selected");
            }

            GenerateUI();

            _poolInfoTable = new Table();
            _poolInfoTable.AddColumns(pool.TableHeaders());

            _logger.Log(LogLevel.Debug, $"Checking setup requirements for '{pool.Name}'");

            (Wallet wallet, string publicKey) = await data.Settings.GetWalletAsync();

            if (pool.RequiresKeypair && wallet == null)
            {
                return (false, $"A full keypair is required for this pool. Private keys are never sent to the server");
            }

            pool.SetWalletInfo(wallet, publicKey);

            var poolResult = await pool.SetupAsync(_cts.Token);

            if (!poolResult.success)
            {
                return poolResult;
            }

            AnsiConsole.Clear();

            pool.OnMinerUpdate += Pool_OnMinerUpdate;
            pool.OnChallengeUpdate += Pool_OnChallengeUpdate;
            pool.ResumeMining += Pool_ResumeMining;
            pool.PauseMining += Pool_PauseMining;

            if (cpuHasher != null)
            {
                cpuHasher.OnHashrateUpdate += Hasher_OnHashrateUpdate;
            }

            if (gpuHasher != null)
            {
                gpuHasher.OnHashrateUpdate += Hasher_OnHashrateUpdate;
            }

            bool cpuEnabled = cpuHasher != null && cpuHasher is not DisabledHasher;
            bool gpuEnabled = gpuHasher != null && gpuHasher is not DisabledHasher;
            bool setAffinity = OperatingSystem.IsWindows() && cpuEnabled && !gpuEnabled && _currentData.Settings.CPUSetting.AutoSetCPUAffinity;

            if (OperatingSystem.IsWindows() && setAffinity)
            {
                List<CoreInfo> coreInformation = SystemInformation.GetCoreInformation();

                //Only use physical cores
                if (_currentData.Settings.CPUSetting.CPUThreads > 0 && coreInformation.Count >= _currentData.Settings.CPUSetting.CPUThreads)
                {
                    nint processorMask = 0;
                    nint fullMask = 0;

                    coreInformation.ForEach(x =>
                    {
                        processorMask |= (nint)x.PhysicalMask;
                        fullMask |= (nint)x.FullMask;
                    });

                    Process currentProcess = Process.GetCurrentProcess();

                    if (currentProcess.ProcessorAffinity == fullMask)
                    {
                        currentProcess.ProcessorAffinity = processorMask;
                    }
                }
            }

            if (data.Settings.PreventSleeping)
            {
                SystemInformation.PreventSleep();
            }

            if (cpuEnabled)
            {
                _logger.Log(LogLevel.Debug, $"Initializing {cpuHasher.Name} {cpuHasher.HardwareType} hasher");

                var result = await cpuHasher.InitializeAsync(pool, data.Settings);

                if (!result.success)
                {
                    _logger.Log(LogLevel.Warn, $"Failed to initialize CPU hasher. Reason: {result.message}");
                }
            }

            if (gpuEnabled)
            {
                _logger.Log(LogLevel.Debug, $"Initializing {gpuHasher.Name} {gpuHasher.HardwareType} hasher");

                var result = await gpuHasher.InitializeAsync(pool, data.Settings);

                if (!result.success)
                {
                    _logger.Log(LogLevel.Warn, $"Failed to initialize GPU hasher. Reason: {result.message}");
                }
            }

            _logger.Log(LogLevel.Debug, $"Connecting to pool '{pool.Name}'");

            if (await pool.ConnectAsync(_cts.Token))
            {
                MiningStartEvent miningStartEvent = new MiningStartEvent
                {
                    Id = _currentData.Settings.EventWebsocketSetting.Id,
                    CPUEnabled = cpuEnabled,
                    CPUHasher = cpuHasher.Name,
                    CPUThreads = (cpuHasher as BaseCPUHasher)?.Threads ?? 0,
                    GPUBatchSize = (gpuHasher as BaseGPUHasher)?.BatchSize ?? 0,
                    GPUBlockSize = _currentData.Settings.GPUSetting.GPUBlockSize,
                    GPUHasher = gpuHasher?.Name ?? "Disabled",
                    Pool = pool.Name,
                    ProgramGenerationThreads = _currentData.Settings.GPUSetting.ProgramGenerationThreads <= 0 ? Environment.ProcessorCount : _currentData.Settings.GPUSetting.ProgramGenerationThreads,
                };

                if (gpuHasher != null)
                {
                    var devices = GetDevicesInUse((IGPUHasher)gpuHasher);

                    for (int i = 0; i < devices.Count; i++)
                    {
                        miningStartEvent.Devices.Add(new MiningStartEvent.DeviceInformation
                        {
                            Id = i,
                            Name = devices[i].Name
                        });
                    }
                }

                _currentData.EventHandler.AddEvent(miningStartEvent);
            }

            return (true, String.Empty);
        }

        private void Pool_OnChallengeUpdate(object? sender, NewChallengeInfo e)
        {
            _currentData.EventHandler.AddEvent(new NewChallengeEvent
            {
                Id = _currentData.Settings.EventWebsocketSetting.Id,
                Challenge = e.Challenge,
                ChallengeId = e.ChallengeId,
                StartNonce = e.StartNonce,
                EndNonce = e.EndNonce
            });
        }

        private void Pool_PauseMining(object? sender, EventArgs e)
        {
            (IHasher cpu, IHasher gpu) = _currentData.GetChosenHasher();

            cpu?.PauseMining();
            gpu?.PauseMining();

            for (int i = 0; i < _hashrateTable.Rows.Count; i++)
            {
                _hashrateTable.UpdateCell(i, 3, "[yellow]Paused[/]");
            }

            _currentData.EventHandler.AddEvent(new MiningPauseEvent
            {
                Id = _currentData.Settings.EventWebsocketSetting.Id
            });
        }

        private void Pool_ResumeMining(object? sender, EventArgs e)
        {
            (IHasher cpu, IHasher gpu) = _currentData.GetChosenHasher();

            cpu?.ResumeMining();
            gpu?.ResumeMining();
        }

        private void Pool_OnMinerUpdate(object? sender, (string[] columns, byte[] data) ev)
        {
            var e = ev.columns;

            if (e.Length != _poolInfoTable.Columns.Count)
            {
                _logger.Log(LogLevel.Warn, $"Pool info table expects {_poolInfoTable.Columns.Count} columns. Received: {e.Length}");
                return;
            }

            _poolInfoTable.ShowRowSeparators = true;

            if (_poolInfoTable.Rows.Count >= 25)
            {
                _poolInfoTable.RemoveRow(_poolInfoTable.Rows.Count - 1);
            }

            //Gets removed for some reason
            _poolInfoTable.Title = new TableTitle("Pool Info");
            _poolInfoTable.InsertRow(0, e);

            _uiLayout["poolInfo"].Update(_poolInfoTable);

            _currentData.EventHandler.AddEvent(new SubmissionResultEvent
            {
                Id = _currentData.Settings.EventWebsocketSetting.Id,
                SubmissionResult = ev.data
            });
        }

        private void Hasher_OnHashrateUpdate(object? sender, Hashers.Models.HashrateInfo e)
        {
            IHasher hasher = (IHasher)sender;
            int index = hasher.HardwareType == IHasher.Hardware.CPU ? 0 : e.Index + 1;

            _hashrateTable.UpdateCell(index, 2, e.CurrentThreads == -1 ? "-" : e.CurrentThreads.ToString());
            _hashrateTable.UpdateCell(index, 3, hasher.IsMiningPaused ? "[yellow]Paused[/]" : "[green]Mining[/]");

            if (hasher.HardwareType == IHasher.Hardware.GPU && e.ProgramGenerationTooLong)
            {
                _hashrateTable.UpdateCell(index, 4, $"[red]{e.ChallengeSolutionsPerSecond}[/]");
            }
            else
            {
                _hashrateTable.UpdateCell(index, 4, e.ChallengeSolutionsPerSecond.ToString());
            }

            _hashrateTable.UpdateCell(index, 5, e.SolutionsPerSecond.ToString());
            _hashrateTable.UpdateCell(index, 6, e.HighestDifficulty.ToString());
            _hashrateTable.UpdateCell(index, 7, e.ChallengeId.ToString());
            //_hashrateTable.UpdateCell(index, 8, $"{e.TotalTime.TotalSeconds:0.00}s");

            _currentData.EventHandler.AddEvent(new HashrateUpdateEvent
            {
                Id = _currentData.Settings.EventWebsocketSetting.Id,
                AverageHashesPerSecond = e.ChallengeSolutionsPerSecond.Speed,
                CurrentHashesPerSecond = e.SolutionsPerSecond.Speed,
                CPUStruggling = hasher.HardwareType == IHasher.Hardware.GPU && e.ProgramGenerationTooLong,
                DeviceId = e.Index,
                HighestDeviceDifficulty = (byte)e.HighestDifficulty
            });
        }

        private void GenerateUI()
        {
            IPool pool = _currentData.GetChosenPool();
            (IHasher cpuHasher, IHasher gpuHasher) = _currentData.GetChosenHasher();

            //Generate UI
            _uiLayout = new Layout("minerModule").SplitColumns(
                new Layout("hashrate"),
                new Layout("poolInfo")
                );
            _uiLayout["hashrate"].Ratio = 85;
            _uiLayout["poolInfo"].Ratio = 100;

            _hashrateTable = new Table();
            _hashrateTable.Title = new TableTitle($"Pool: {pool.DisplayName}");

            _hashrateTable.AddColumn(new TableColumn("Name").Centered());
            _hashrateTable.AddColumn(new TableColumn("Hasher").Centered());
            _hashrateTable.AddColumn(new TableColumn("Threads").Centered());
            _hashrateTable.AddColumn(new TableColumn("Status").Centered());
            _hashrateTable.AddColumn(new TableColumn("Avg Hashrate").Centered());
            _hashrateTable.AddColumn(new TableColumn("Cur Hashrate").Centered());
            _hashrateTable.AddColumn(new TableColumn("Diff").Centered());
            _hashrateTable.AddColumn(new TableColumn("Id").Centered());
            _hashrateTable.ShowRowSeparators = true;
            //_hashrateTable.AddColumn(new TableColumn("Challenge Time").Centered());

            _poolInfoTable = new Table();
            _poolInfoTable.Title = new TableTitle("Pool Info");
            _poolInfoTable.AddColumns(pool.TableHeaders());
            _poolInfoTable.ShowRowSeparators = true;

            for (int i = 0; i < _poolInfoTable.Columns.Count; i++)
            {
                _poolInfoTable.Columns[i].Centered();
            }

            _poolInfoTable.Expand();

            _uiLayout["hashrate"].Update(_hashrateTable);
            _uiLayout["poolInfo"].Update(_poolInfoTable);

            //Add CPU
            _hashrateTable.AddRow(SystemInformation.GetProcessorName(), cpuHasher?.Name, "-", "-", "-", "-", "-", "-");


            //Add GPUs
            if (gpuHasher != null)
            {
                IGPUHasher gHasher = (IGPUHasher)gpuHasher;



                //Will need to add a row for each GPU
                foreach (var device in GetDevicesInUse(gHasher))
                {
                    string gpuName = device.Name.Replace("NVIDIA ", "").Replace("GeForce ", "");

                    _hashrateTable.AddRow(gpuName, gpuHasher.Name, "-", "-", "-", "-", "-", "-");
                }
            }
        }

        private List<Device> GetDevicesInUse(IGPUHasher gHasher)
        {
            //Grab all devices that are being used
            List<Device> devicesToUse = new List<Device>();
            List<Device> devices = gHasher.GetDevices(false); //All devices
            HashSet<Device> supportedDevices = new HashSet<Device>(gHasher.GetDevices(true)); //Only supported

            //The setting is the full list of GPU devices
            foreach (var d in _currentData.Settings.GPUDevices)
            {
                if (d >= 0 && d < devices.Count)
                {
                    if (supportedDevices.Contains(devices[d]))
                    {
                        devicesToUse.Add(devices[d]);
                    }
                }
            }

            return devicesToUse;
        }
    }
}
