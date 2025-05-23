using CommandLine;
using ILGPU.Runtime.Cuda;
using NLog;
using OrionClient.Commands;
using OrionClientLib;
using OrionClientLib.Hashers;
using OrionClientLib.Hashers.CPU;
using OrionClientLib.Hashers.GPU.AMDBaseline;
using OrionClientLib.Hashers.GPU.Baseline;
using OrionClientLib.Modules;
using OrionClientLib.Modules.Models;
using OrionClientLib.Pools;
using OrionClientLib.Utilities;
using OrionEventLib;
using Solnet.Wallet;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OrionClient
{
    public class Program
    {
        private static readonly Logger _logger = LogManager.GetLogger("Main");
        private static (int Width, int Height) windowSize = (Console.WindowWidth, Console.WindowHeight);
        private static ConcurrentQueue<LogInformation> _logQueue = new ConcurrentQueue<LogInformation>();
        private static List<IPool> _pools;
        private static List<IModule> _modules;
        private static List<IHasher> _hashers;
        private static IModule _currentModule = null;
        private static bool _attemptedExit = false;
        private static Settings _settings;

        private static Layout _uiLayout;
        private static Table _logTable;

        private static string _message = String.Empty;
        private static string _version = "2.0.0.0";
        private static GithubApi.Data _updateData;
        private static string _cudaLocation = String.Empty;
        private static OrionEventHandler _eventHandler;

        private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (OperatingSystem.IsLinux() && (libraryName == "nvcuda" || libraryName == "cuda"))
            {
                if (String.IsNullOrEmpty(_cudaLocation))
                {
                    string[] files = GetLibCudaFromDirectory("/usr/lib");

                    if (files.Length == 0)
                    {
                        files = GetLibCudaFromDirectory("/usr/lib/wsl/lib/");

                        if (files.Length == 0)
                        {
                            //_logger.Log(LogLevel.Warn, $"Failed to find libcuda.so. Using default resolver");

                            return IntPtr.Zero;
                        }
                    }

                    if (files.Length > 1)
                    {
                        _logger.Log(LogLevel.Debug, $"Found multiple locations for libcuda.so. Using first. {String.Join(", ", files)}");
                    }


                    _cudaLocation = files[0];
                }

                return NativeLibrary.Load(_cudaLocation, assembly, searchPath);
            }

            // Otherwise, fallback to default import resolver.
            return IntPtr.Zero;

            string[] GetLibCudaFromDirectory(string directory)
            {
                if (Directory.Exists(directory))
                {
                    return Directory.GetFiles(directory, "libcuda.so", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false
                    });
                }

                return new string[0];
            }
        }

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Settings))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Program))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CommandLineOptions))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DefaultCommandLineOptions))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MineCommandLineOptions))]
        static async Task Main(string[] args)
        {
            if (!IsSupported())
            {
                Console.WriteLine($"Only x64 Windows/Linux is currently supported");

                return;
            }

            NativeLibrary.SetDllImportResolver(Assembly.GetAssembly(typeof(CudaAccelerator)), DllImportResolver);

            //ConsoleTraceListener listener = new ConsoleTraceListener();
            //Trace.Listeners.Add(listener);

            #region Configure

            _pools = new List<IPool>
            {
                new Ec1ipseOrePool(),
                //new ShinystCoalPool(),
                new CustomOreHQPool(),
                new BitzNoPool()
            };

            _modules = new List<IModule>
            {
                new MinerModule(),
                new PoolModule(),
                new StakingModule(),
                new VanityModule(),
                new BenchmarkerModule(),
                new SetupModule(),
                new SettingsModule(),
                new ExitModule(),
            };

            _hashers = new List<IHasher>();
            AddSupportedHasher(new ManagedCPUHarakaHasher());
            //AddSupportedHasher(new ManagedCPUHasher());
            ////AddSupportedHasher(new HybridCPUHasher());
            //AddSupportedHasher(new PartialCPUHasherAVX2());
            ////AddSupportedHasher(new HybridCPUHasherAVX512());
            //AddSupportedHasher(new AVX512CPUHasher());
            ////AddSupportedHasher(new NativeCPUHasher());
            //AddSupportedHasher(new NativeCPUHasherAVX2());
            ////AddSupportedHasher(new CudaBaselineGPUHasher());
            //AddSupportedHasher(new CudaOptEmulationGPUHasher());
            ////AddSupportedHasher(new Cuda4090OptGPUHasher());
            //AddSupportedHasher(new OpenCLOptEmulationGPUHasher());
            AddSupportedHasher(new DisabledCPUHasher());
            AddSupportedHasher(new DisabledGPUHasher());

            void AddSupportedHasher(IHasher hasher)
            {
                if (hasher.IsSupported())
                {
                    _hashers.Add(hasher);
                }
            }

            #endregion

            #region UI Configure

            _uiLayout = new Layout("Root").SplitRows(
                new Layout("Main"),
                new Layout("Logs"));


            _logTable = new Table();
            _logTable.AddColumn("Time");
            _logTable.AddColumn("Type");
            _logTable.AddColumn("Message");
            _logTable.NoBorder();

            _uiLayout["Logs"].Update(_logTable);

            #endregion

            _settings = await Settings.LoadAsync();
            await _settings.SaveAsync();

            if (!await HandleSettings(args))
            {
                return;
            }

            AnsiConsole.Clear();

            Console.WriteLine("Checking for updates ...");

            _updateData = await GithubApi.CheckForUpdates(_version);
            _eventHandler = new OrionEventHandler(_settings.EventWebsocketSetting.Enable, _settings.EventWebsocketSetting.ReconnectTimeMs, _settings.EventWebsocketSetting.Serialization);

            if (_settings.EventWebsocketSetting.Enable)
            {
                Console.WriteLine("Connecting to event server ...");
                await _eventHandler.Connect(_settings.EventWebsocketSetting.WebsocketUrl, _settings.EventWebsocketSetting.Port);
            }

            AnsiConsole.Clear();

            Console.CancelKeyPress += Console_CancelKeyPress;

            while (true)
            {
                Data data = new Data(_hashers, _pools, _settings, _eventHandler);

                await DisplayMenu(data);

                AnsiConsole.Clear();
            }
        }

        private static async Task<bool> HandleSettings(string[] args)
        {
            var parser = new Parser((settings) =>
            {
                settings.AutoVersion = true;
                settings.HelpWriter = Parser.Default.Settings.HelpWriter;
                settings.IgnoreUnknownArguments = false;
            });

            return await parser.ParseArguments<DefaultCommandLineOptions, MineCommandLineOptions>(args).MapResult<DefaultCommandLineOptions, MineCommandLineOptions, Task<bool>>(
                async (defaultOpts) =>
                {
                    return await HandleDefaultSettings(defaultOpts);
                },
                async (mineOpts) =>
                {
                    bool success = await HandleDefaultSettings(mineOpts);

                    if (!success)
                    {
                        return success;
                    }

                    await InitializeModule<MinerModule>();

                    return success;
                },
                async errs =>
                {
                    return await Task.FromResult(false);
                });


            async Task InitializeModule<T>() where T : IModule
            {
                _currentModule = _modules.FirstOrDefault(x => x is MinerModule);
                _eventHandler = new OrionEventHandler(_settings.EventWebsocketSetting.Enable, _settings.EventWebsocketSetting.ReconnectTimeMs, _settings.EventWebsocketSetting.Serialization);

                Data data = new Data(_hashers, _pools, _settings, _eventHandler);
                (IHasher? cpuHasher, IHasher? gpuHasher) = data.GetChosenHasher();

                if ((cpuHasher != null && cpuHasher is not DisabledHasher) || (gpuHasher != null && gpuHasher is not DisabledHasher))
                {
                    var result = await _currentModule?.InitializeAsync(data);

                    if (!result.success)
                    {
                        _message = $"[red]{result.errorMessage}[/]\n";
                        _currentModule = null;
                    }
                }
                else
                {
                    _currentModule = null;
                }
            }
        }

        private static async Task<bool> HandleDefaultSettings(CommandLineOptions cmdOptions)
        {
            //Manually handle options
            Data temp = new Data(_hashers, _pools, null, null, false);

            #region Main

            if (!String.IsNullOrEmpty(cmdOptions.KeyFile))
            {
                if (!String.IsNullOrEmpty(cmdOptions.PublicKey))
                {
                    AnsiConsole.MarkupLine($"[red]Error: [green]--keypair[/] and [green]--key[/] options can't be used together[/]");

                    return false;
                }

                _settings.KeyFile = cmdOptions.KeyFile;
                (Wallet wallet, string publicKey) result = await _settings.GetWalletAsync();

                if (result.wallet == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error: [green]--keypair[/] path '[cyan]{cmdOptions.KeyFile}[/]' is invalid[/]");

                    return false;
                }
            }
            else if (!String.IsNullOrEmpty(cmdOptions.PublicKey))
            {
                _settings.KeyFile = null; //Remove key file
                _settings.PublicKey = cmdOptions.PublicKey;

                (Wallet wallet, string publicKey) result = await _settings.GetWalletAsync();

                if (String.IsNullOrEmpty(result.publicKey))
                {
                    AnsiConsole.MarkupLine($"[red]Error: [green]--key[/] value '[cyan]{cmdOptions.PublicKey}[/]' is invalid[/]");

                    return false;
                }
            }

            #endregion

            #region Pool Settings

            if (!String.IsNullOrEmpty(cmdOptions.Pool))
            {
                IPool chosenPool = _pools.FirstOrDefault(x => x.ArgName == cmdOptions.Pool.ToLower());

                if (chosenPool == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error: [green]--pool[/] value '[cyan]{cmdOptions.Pool}[/]' is invalid. Valid values: {String.Join(", ", _pools.Select(x => x.ArgName))}[/]");

                    return false;
                }

                _settings.Pool = chosenPool.Name;
            }

            #endregion

            #region CPU

            if (cmdOptions.CPUThreads.HasValue)
            {
                int threads = cmdOptions.CPUThreads.Value;

                if (threads < 0 || threads > Environment.ProcessorCount)
                {
                    AnsiConsole.MarkupLine($"[red]Error: [green]--cpu-threads[/] value '[cyan]{cmdOptions.CPUThreads}[/]' is invalid. Valid (0-{Environment.ProcessorCount})[/]");

                    return false;
                }

                _settings.CPUSetting.CPUThreads = threads;
            }

            if (cmdOptions.DisableCPU)
            {
                _settings.CPUSetting.CPUHasher = "Disabled";
            }

            if (cmdOptions.AutoSelectCPU)
            {
                _settings.CPUSetting.CPUHasher = temp.GetBestCPUHasher().Name;
            }

            #endregion

            #region GPU

            if (cmdOptions.EnableGPU)
            {
                if (_settings.GPUDevices == null || _settings.GPUDevices.Count == 0)
                {
                    (IHasher hasher, List<int> devices) = temp.GetGPUSettingInfo(cmdOptions.OpenCL);

                    _settings.GPUDevices = hasher == null ? new List<int>() : devices;
                    _settings.GPUSetting.GPUHasher = hasher?.Name ?? "Disabled"; //If no supported devices, will be empty
                }
            }

            if (cmdOptions.BatchSize.HasValue)
            {
                int batchSize = cmdOptions.BatchSize.Value;

                int[] validValues = new int[] { 2048, 1024, 512, 256, 128 };

                if (!validValues.Contains(batchSize))
                {
                    AnsiConsole.MarkupLine($"[red]Error: [green]--gpu-batch-size[/] value '[cyan]{cmdOptions.BatchSize}[/]' is invalid. Valid ({String.Join(", ", validValues)})[/]");

                    return false;
                }

                _settings.GPUSetting.MaxGPUNoncePerBatch = batchSize;
            }

            //if (cmdOptions.BlockSize.HasValue)
            //{
            //    int blockSize = cmdOptions.BlockSize.Value;

            //    int[] validValues = new int[] { 512, 256, 128, 64, 32, 16 };

            //    if (!validValues.Contains(blockSize))
            //    {
            //        AnsiConsole.MarkupLine($"[red]Error: [green]--gpu-block-size[/] value '[cyan]{cmdOptions.BlockSize}[/]' is invalid. Valid ({String.Join(", ", validValues)})[/]");

            //        return false;
            //    }

            //    _settings.GPUSetting.GPUBlockSize = blockSize;
            //}

            if (cmdOptions.ProgramGenerationThreads.HasValue)
            {
                int threads = cmdOptions.ProgramGenerationThreads.Value;

                if (threads < 0 || threads > Environment.ProcessorCount)
                {
                    AnsiConsole.MarkupLine($"[red]Error: [green]--gpu-gen-threads[/] value '[cyan]{cmdOptions.ProgramGenerationThreads}[/]' is invalid. Valid (0-{Environment.ProcessorCount})[/]");

                    return false;
                }

                _settings.GPUSetting.ProgramGenerationThreads = threads;
            }

            #endregion

            #region Events

            if (!String.IsNullOrEmpty(cmdOptions.WebsocketUrl))
            {
                _settings.EventWebsocketSetting.WebsocketUrl = cmdOptions.WebsocketUrl;
            }

            if (cmdOptions.Port.HasValue)
            {
                _settings.EventWebsocketSetting.Port = cmdOptions.Port.Value;
            }

            if (!String.IsNullOrEmpty(cmdOptions.Id))
            {
                _settings.EventWebsocketSetting.Id = cmdOptions.Id;
            }

            if (cmdOptions.ReconnectTimeMs.HasValue)
            {
                _settings.EventWebsocketSetting.ReconnectTimeMs = cmdOptions.ReconnectTimeMs.Value;
            }

            if (cmdOptions.Serialization.HasValue)
            {
                _settings.EventWebsocketSetting.Serialization = cmdOptions.Serialization.Value;
            }

            #endregion

            return true;
        }

        private static async void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            //Allow exit
            if (_currentModule == null || _attemptedExit)
            {
                Environment.Exit(-1073741510);
            }

            //Prevent program from closing
            e.Cancel = true;
            _attemptedExit = true;

            await _currentModule.ExitAsync();
        }

        private static async Task DisplayMenu(Data data)
        {
            if (_currentModule == null)
            {
                _attemptedExit = false;

                _currentModule = await DisplayModuleSelector(data);
                _message = String.Empty;

                try
                {
                    var result = await _currentModule.InitializeAsync(data);

                    if (!result.success)
                    {
                        if (!String.IsNullOrEmpty(result.errorMessage))
                        {
                            _message = $"[red]{result.errorMessage}[/]\n";
                        }

                        _currentModule = null;
                    }
                }
                catch (TaskCanceledException)
                {
                    _currentModule = null;
                }

                return;
            }

            LiveDisplay liveLayout = AnsiConsole.Live(_uiLayout);

            await liveLayout.StartAsync(async (ctx) =>
            {
                while (true)
                {
                    ExecuteResult executeResult = await _currentModule.ExecuteAsync(data);

                    if (executeResult.Exited)
                    {
                        _currentModule = null;

                        break;
                    }

                    if (executeResult.Renderer != null)
                    {
                        _uiLayout["Main"].Update(executeResult.Renderer);
                        ctx.UpdateTarget(_uiLayout);
                    }
                    else
                    {
                        ctx.UpdateTarget(_logTable);
                    }

                    //Update logs
                    while (_logQueue.TryDequeue(out var log))
                    {
                        if (_logTable.Rows.Count > 25)
                        {
                            _logTable.Rows.RemoveAt(25);
                        }

                        string color = "gray";

                        if (log.Level == "Info")
                        {
                            color = "green";
                        }
                        else if (log.Level == "Warn")
                        {
                            color = "yellow";
                        }
                        else if (log.Level == "Error")
                        {
                            color = "red";
                        }

                        _logTable.InsertRow(0, $"[{color}]{log.Time}[/]", $"[{color}]{log.Level}[/]", $"[{color}]{Markup.Escape(log.Message)}[/]");
                    }

                    await Task.Delay(500);

                    //Changing window size can mess up with the display. Easiest method is to clear the UI and redraw
                    if (WindowSizeChange())
                    {
                        break;
                    }
                }
            });
        }

        private static async Task<IModule> DisplayModuleSelector(Data data)
        {
            SelectionPrompt<IModule> prompt = new SelectionPrompt<IModule>();
            prompt.WrapAround = true;

            (Wallet wallet, string publicKey) = await _settings.GetWalletAsync();
            (IHasher cpuHasher, IHasher gpuHasher) = data.GetChosenHasher();
            IPool pool = data.GetChosenPool();

            string newVersion = _updateData == null ? String.Empty : $" -- New Version {_updateData.TagName} {(_updateData.Prerelease ? $"[[Prerelease]]" : String.Empty)}";

            prompt.Title($"         [lime]Orion Client v{_version}[/]{newVersion}\n\nWallet: {publicKey ?? "N/A"}\n" +
                $"Hasher: CPU - {cpuHasher?.Name ?? "N/A"} ({(_settings.CPUSetting.CPUThreads > 0 ? _settings.CPUSetting.CPUThreads : Environment.ProcessorCount)} threads), GPU - {gpuHasher?.Name ?? "N/A"}\n" +
                $"Pool: {pool?.DisplayName ?? "N/A"}" +
                $"{(_settings.EventWebsocketSetting.Enable ? $"\nEvent Server Status: {GetEventServerStatus()}" : String.Empty)}" +
                $"{(!String.IsNullOrEmpty(_message) ? $"\n\n[red]Error: {_message}[/]" : String.Empty)}");
            _message = String.Empty;

            string GetEventServerStatus()
            {
                if (_eventHandler.Connected)
                {
                    return $"[green]Connected[/]";
                }

                return $"[red]Disconnected[/]";
            }


            prompt.UseConverter((module) =>
            {
                if (module is PoolModule && pool != null)
                {
                    return pool.DisplayName;
                }

                if (module is SetupModule && data.Settings.NeedsSetup)
                {
                    return $"[green]{module.Name}[/]";
                }

                return module.Name;
            });

            foreach (IModule module in _modules)
            {
                if (module is MinerModule)
                {
                    if (data.Settings.NeedsSetup)
                    {
                        continue;
                    }

                    if ((cpuHasher is DisabledHasher || cpuHasher == null) && (gpuHasher is DisabledHasher || gpuHasher == null))
                    {
                        continue;
                    }
                }

                if (module is PoolModule)
                {
                    if (pool == null)
                    {
                        continue;
                    }

                    pool.SetWalletInfo(wallet, publicKey);
                }

                if (module is SettingsModule && data.Settings.NeedsSetup)
                {
                    continue;
                }

                prompt.AddChoice(module);
            }

            return await prompt.ShowAsync(AnsiConsole.Console, CancellationToken.None);
        }

        private static bool WindowSizeChange()
        {
            if (Console.WindowHeight != windowSize.Height || Console.WindowWidth != windowSize.Width)
            {
                windowSize = (Console.WindowWidth, Console.WindowHeight);
                return true;
            }

            return false;
        }

        public static void LogMethod(string time, string level, string message, string exception)
        {
            _logQueue.Enqueue(new LogInformation
            {
                Time = time,
                Level = level,
                Message = message,
                Exception = exception
            });
        }

        static bool IsSupported()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                return false;
            }

            if (RuntimeInformation.OSArchitecture != Architecture.X64)
            {
                return false;
            }

            return true;
        }

        private class LogInformation
        {
            public string Time { get; set; }
            public string Level { get; set; }
            public string Message { get; set; }
            public string Exception { get; set; }
        }
    }
}
