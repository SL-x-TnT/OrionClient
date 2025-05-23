using Equix;
using ILGPU.Runtime;
using Newtonsoft.Json;
using OrionClientLib.Hashers;
using OrionClientLib.Modules.Models;
using OrionClientLib.Pools;
using OrionClientLib.Utilities;
using Solnet.Wallet;
using Solnet.Wallet.Bip39;
using Solnet.Wallet.Utilities;
using Spectre.Console;
using Spectre.Console.Prompts;

namespace OrionClientLib.Modules
{
    public class SetupModule : IModule
    {
        public string Name { get; } = "Run Setup";

        private int _currentStep = 0;
        private List<Func<Task<int>>> _steps = new List<Func<Task<int>>>();
        private Data _data;
        private Settings _settings => _data?.Settings;
        private CancellationTokenSource _cts;
        private string _errorMessage = String.Empty;
        private bool _isKeypairSetup = false;
        private bool _isSimpleSetup = false;

        public SetupModule()
        {
            _steps.Add(SetupTypeAsync);
            _steps.Add(WalletSetupAsync);
            _steps.Add(ChoosePoolAsync);
            _steps.Add(ChooseCPUHasherAsync);
            _steps.Add(ChooseGPUHasherAsync);
            _steps.Add(FinalConfirmationAsync);
        }

        public async Task<ExecuteResult> ExecuteAsync(Data data)
        {
            return new ExecuteResult
            {
                Exited = true
            };
        }

        public async Task ExitAsync()
        {
            _cts.Cancel();
        }

        public async Task<(bool, string)> InitializeAsync(Data data)
        {
            _cts = new CancellationTokenSource();
            _currentStep = 0;
            _data = data;

            bool reloadSettings = false;

            try
            {
                while (_currentStep < _steps.Count)
                {
                    _currentStep = await _steps[_currentStep]();
                }
            }
            catch (TaskCanceledException)
            {
                reloadSettings = true;
            }

            if (reloadSettings)
            {
                await _settings.ReloadAsync();

                return (false, "Setup cancelled by user");
            }

            return (true, String.Empty);
        }

        private async Task<int> SetupTypeAsync()
        {
            SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
            selectionPrompt.WrapAround = true;

            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSetup type\n\tRecommend: CPU (100% usage) + GPU (if found)\n\tAdvanced: Manually choose each setting\n\tModify Keypair: Only modify keypair");

            const string recommended = "Recommended";
            const string advanced = "Advanced";
            const string keypair = "Modify Keypair";
            const string exit = "Exit";

            //Flip first choice based on previous settings
            if (!_settings.UsedAdvancedSettings)
            {
                selectionPrompt.AddChoice(recommended);
                selectionPrompt.AddChoice(advanced);
            }
            else
            {
                selectionPrompt.AddChoice(advanced);
                selectionPrompt.AddChoice(recommended);
            }

            selectionPrompt.AddChoice(keypair);
            selectionPrompt.AddChoice(exit);

            string response = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            _isKeypairSetup = false;
            _isSimpleSetup = false;

            switch (response)
            {
                case recommended:
                    _isSimpleSetup = true;
                    SetRecommendedSettings();
                    return _currentStep + 1;
                case advanced:
                    return _currentStep + 1;
                case keypair:
                    _isKeypairSetup = true;
                    return _currentStep + 1;
                case exit:
                    return _steps.Count;
                default:
                    return _currentStep;
            }

            void SetRecommendedSettings()
            {
                //CPU settings
                _settings.CPUSetting.CPUHasher = _data.GetBestCPUHasher().Name;
                _settings.CPUSetting.CPUThreads = Environment.ProcessorCount;

                //GPU settings
                (IHasher hasher, List<int> devices) = _data.GetGPUSettingInfo(false);

                _settings.GPUDevices = hasher == null ? new List<int>() : devices;
                _settings.GPUSetting.GPUHasher = hasher?.Name ?? "Disabled"; //If no supported devices, will be empty

                int threadReduction = _settings.GPUDevices.Count * 2;

                _settings.CPUSetting.CPUThreads -= threadReduction;

                //Not enough CPU threads left, disable CPU hashing
                if (_settings.CPUSetting.CPUThreads <= 2)
                {
                    _settings.CPUSetting.CPUHasher = "Disabled";
                }

            }
        }

        private async Task<int> WalletSetupAsync()
        {
            while (true)
            {
                (Wallet solanaWallet, string publicKey) = await _settings.GetWalletAsync();

                SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
                selectionPrompt.WrapAround = true;

                selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSetup solana wallet. Current: {publicKey ?? "??"}. Path: {(_settings.HasPrivateKey ? (_settings.KeyFile ?? "N/A") : "N/A")}");

                const string confirm = "Confirm";
                const string createNew = "Create New";
                const string usePublicKey = "Use Public Key";
                const string search = "Search / Set Filepath";
                const string exit = "[aqua]<-- Previous Step[/]";

                if (!String.IsNullOrEmpty(publicKey))
                {
                    selectionPrompt.AddChoice(confirm);
                }

                if (_data.Pools.Any(x => !x.RequiresKeypair))
                {
                    selectionPrompt.AddChoice(usePublicKey);
                }

                selectionPrompt.AddChoice(createNew);
                selectionPrompt.AddChoice(search);
                selectionPrompt.AddChoice(exit);

                string response = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

                switch (response)
                {
                    case confirm:
                        if (_isKeypairSetup)
                        {
                            return _steps.Count - 1;
                        }

                        return _currentStep + 1;
                    case createNew:
                        await CreateNewWallet(solanaWallet);
                        break;
                    case search:
                        await SearchWallet();
                        break;
                    case usePublicKey:
                        await SetupPublicKey(publicKey);
                        break;
                    case exit:
                        return _currentStep - 1;
                }
            }
        }

        private async Task<int> ChooseCPUHasherAsync()
        {
            (IHasher cpuHasher, IHasher gpuHasher) = _data.GetChosenHasher();

            SelectionPrompt<IHasher> selectionPrompt = new SelectionPrompt<IHasher>();
            selectionPrompt.WrapAround = true;
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSelect CPU hashing implementation. Run benchmark to see hashrates");
            selectionPrompt.UseConverter((hasher) =>
            {
                if (hasher == null)
                {
                    return "[aqua]<-- Previous Step[/]";
                }

                string chosenText = String.Empty;

                if (hasher == cpuHasher)
                {
                    chosenText = "[b][[Current]][/] ";
                }

                bool isBest = _data.GetBestCPUHasher() == hasher;

                return $"{chosenText}{hasher.Name} - {hasher.Description} {(hasher.Experimental ? "[red][[Experimental]][/]" : String.Empty)} {(isBest ? "[green][[Recommended]][/]" : String.Empty)}";
            });

            selectionPrompt.AddChoices(_data.Hashers.Where(x => x.HardwareType == IHasher.Hardware.CPU && (_settings.GPUSetting.EnableExperimentalHashers || !x.Experimental)).OrderByDescending(x => x == cpuHasher));
            selectionPrompt.AddChoice(null);

            cpuHasher = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if (cpuHasher == null)
            {
                return _currentStep - 1;
            }

            _settings.CPUSetting.CPUHasher = cpuHasher.Name;

            return await ThreadCountAsync();
        }

        //Reduce to single method later
        private async Task<int> ChooseGPUHasherAsync()
        {
            (IHasher cpuHasher, IHasher gpuHasher) = _data.GetChosenHasher();

            SelectionPrompt<IHasher> selectionPrompt = new SelectionPrompt<IHasher>();
            selectionPrompt.WrapAround = true;
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSelect GPU hashing implementation. Run benchmark to see hashrates{(!String.IsNullOrEmpty(_errorMessage) ? $"\n[red]Error: {_errorMessage}[/]\n" : String.Empty)}");
            selectionPrompt.UseConverter((hasher) =>
            {
                if (hasher == null)
                {
                    return "[aqua]<-- Previous Step[/]";
                }

                string chosenText = String.Empty;

                if (hasher == gpuHasher)
                {
                    chosenText = "[b][[Current]][/] ";
                }

                return $"{chosenText}{hasher.Name} - {hasher.Description} {(hasher.Experimental ? "[red][[Experimental]][/]" : String.Empty)}";
            });

            _errorMessage = String.Empty;

            selectionPrompt.AddChoices(_data.Hashers.Where(x => x.HardwareType == IHasher.Hardware.GPU && (_settings.GPUSetting.EnableExperimentalHashers || !x.Experimental)).OrderByDescending(x => x == gpuHasher));
            selectionPrompt.AddChoice(null);

            gpuHasher = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if (gpuHasher == null)
            {
                return _currentStep - 1;
            }

            _settings.GPUSetting.GPUHasher = gpuHasher.Name;

            if (gpuHasher is DisabledHasher)
            {
                _settings.GPUSetting.GPUHasher = gpuHasher.Name;

                return _currentStep + 1;
            }

            IGPUHasher hasher = (IGPUHasher)gpuHasher;
            List<Device> devices = hasher.GetDevices(false);
            HashSet<Device> validDevices = new HashSet<Device>(hasher.GetDevices(true));

            if (validDevices.Count == 0)
            {
                _errorMessage = "No valid GPUs found";

                return _currentStep;
            }

            //Allow device selection
            MultiSelectionPrompt<Device> deviceSelectionPrompt = new MultiSelectionPrompt<Device>();
            deviceSelectionPrompt.WrapAround = true;
            deviceSelectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSelect GPUs to use. Selecting different GPU types may cause performance issues\n[gray]Press escape to return to GPU implementation step[/]");
            deviceSelectionPrompt.UseConverter((device) =>
            {
                bool selected = _settings.GPUDevices.Contains(devices.IndexOf(device));

                return $"{(selected ? "[b][[Current]][/] " : String.Empty)}{device.Name} - {device.AcceleratorType}{(!validDevices.Contains(device) ? " [red][[Not supported]][/]" : String.Empty)}";
            });
            deviceSelectionPrompt.AbortKey = ConsoleKey.Escape;

            HashSet<Device> selectedDevices = new HashSet<Device>();

            if (_settings.GPUDevices.Count == 0)
            {
                selectedDevices = validDevices;
            }
            else
            {
                foreach (var deviceId in _settings.GPUDevices)
                {
                    if (deviceId >= 0 && deviceId < devices.Count)
                    {
                        selectedDevices.Add(devices[deviceId]);
                    }
                }
            }

            //Shouldn't happen, but disable GPU hasher for now
            if (devices == null)
            {
                _settings.GPUSetting.GPUHasher = "Disabled";

                return _currentStep + 1;
            }

            foreach (var device in devices.OrderByDescending(x => x.NumMultiprocessors))
            {
                deviceSelectionPrompt.AddChoice(device, selectedDevices.Contains(device));
            }


            List<Device> result = new List<Device>();

            try
            {
                result = await deviceSelectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);
            }
            catch (PromptAbortException)
            {
                AnsiConsole.Clear();

                return _currentStep;
            }

            List<int> chosenGPUs = new List<int>();

            foreach (Device device in result)
            {
                chosenGPUs.Add(devices.IndexOf(device));
            }

            _settings.GPUDevices = chosenGPUs;

            return _currentStep + 1;
        }

        private async Task<int> ThreadCountAsync()
        {
            List<CoreInfo> coreInfo = SystemInformation.GetCoreInformation();

            List<(int, string)> choices = new List<(int, string)>();

            int totalThreads = Environment.ProcessorCount;

            if (coreInfo.Count == 0)
            {
                choices.Add((Environment.ProcessorCount, "(100% usage) [green][[Recommended]][/]"));
                choices.Add((1, "(single thread)"));
                choices.Add((0, "Custom"));
            }
            else
            {
                totalThreads = coreInfo.Sum(x => x.ThreadCount);
                bool hasECores = coreInfo.Any(x => !x.IsPCore);

                totalThreads = Math.Max(totalThreads, Environment.ProcessorCount);

                choices.Add((totalThreads, "(100% usage) [green][[Recommended]][/]"));

                if (coreInfo.Count != totalThreads)
                {
                    choices.Add((coreInfo.Count, "(physical cores only)"));
                }
                //CPU has efficiency cores
                if (hasECores)
                {
                    List<CoreInfo> pCores = coreInfo.Where(x => x.IsPCore).ToList();
                    int totalPerformanceThreads = pCores.Sum(x => x.ThreadCount);

                    if (coreInfo.Count == totalThreads)
                    {
                        choices.Add((pCores.Sum(x => x.ThreadCount), "(performance cores only)"));
                    }

                    if (pCores.Count != totalPerformanceThreads)
                    {
                        choices.Add((pCores.Count, "(physical performance cores only)"));
                    }
                }

                choices.Add((1, "(single thread)"));
                choices.Add((0, "Custom"));

                if (!choices.Any(x => x.Item1 == _settings.CPUSetting.CPUThreads))
                {
                    choices.Add((_settings.CPUSetting.CPUThreads, "[[Current]]"));
                }
            }

            choices.Add((-1, "[aqua]<-- Previous Step[/]"));

            SelectionPrompt<(int, string)> selectionPrompt = new SelectionPrompt<(int, string)>();
            selectionPrompt.WrapAround = true;
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSelect total threads. Highest value recommended. Current: {_settings.CPUSetting.CPUThreads}");
            selectionPrompt.UseConverter((tuple) =>
            {
                if (tuple.Item1 > 0)
                {
                    return $"{tuple.Item1} {tuple.Item2}";
                }

                return tuple.Item2;
            });

            selectionPrompt.AddChoices(choices.OrderByDescending(x => x.Item1 == _settings.CPUSetting.CPUThreads).ThenByDescending(x => x.Item1));

            (int, string) choice = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if (choice.Item1 == -1)
            {
                //We're going back to the start of this step
                return _currentStep;
            }
            else if (choice.Item1 == 0)
            {
                TextPrompt<int> textPrompt = new TextPrompt<int>($"Total threads (min: 0, max: {totalThreads}):");
                textPrompt.DefaultValue(_settings.CPUSetting.CPUThreads);
                textPrompt.Validate((x) => { return x >= 0 && x <= totalThreads; });

                int result = await textPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

                AnsiConsole.Clear();
            }
            else
            {
                _settings.CPUSetting.CPUThreads = choice.Item1;
            }

            return _currentStep + 1;
        }

        private async Task<int> ChoosePoolAsync()
        {
            IPool chosenPool = _data.GetChosenPool();

            SelectionPrompt<IPool> selectionPrompt = new SelectionPrompt<IPool>();
            selectionPrompt.WrapAround = true;
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nPool Selection{(!String.IsNullOrEmpty(_errorMessage) ? $"\n[red]Error: {_errorMessage}[/]\n" : String.Empty)}");
            _errorMessage = String.Empty;

            selectionPrompt.UseConverter((pool) =>
            {
                if (pool == null)
                {
                    return "[aqua]<-- Previous Step[/]";
                }

                string chosenText = String.Empty;

                if (pool == chosenPool)
                {
                    chosenText = "[b][[Current]][/] ";
                }

                if (pool == null)
                {
                    return $"{chosenText}Nothing - Skips pool selection";
                }

                return $"{chosenText}{pool.DisplayName} - {pool.Description}";
            });

            if (chosenPool == null)
            {
                selectionPrompt.AddChoices(_data.Pools);
            }
            else
            {
                selectionPrompt.AddChoices(_data.Pools.OrderByDescending(x => x == chosenPool));
            }

            selectionPrompt.AddChoice(null);

            chosenPool = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if (chosenPool == null)
            {
                return _currentStep - 1;
            }
            else
            {
                (Wallet wallet, string publicKey) = await _data.Settings.GetWalletAsync();

                Console.WriteLine($"DEBUG: SetupModule - Setting wallet info for pool: {chosenPool.Name}");
                Console.WriteLine($"DEBUG: SetupModule - Wallet: {wallet?.Account?.PublicKey}, PublicKey: {publicKey}");
                
                chosenPool.SetWalletInfo(wallet, publicKey);

                Console.WriteLine($"DEBUG: SetupModule - Calling SetupAsync on pool: {chosenPool.Name}");
                var poolSetup = await chosenPool.SetupAsync(_cts.Token, true);
                Console.WriteLine($"DEBUG: SetupModule - Pool setup result: {poolSetup.success}");
                Console.WriteLine($"DEBUG: SetupModule - Pool setup error: {poolSetup.errorMessage}");

                if (!poolSetup.success)
                {
                    Console.WriteLine($"DEBUG: SetupModule - Setup failed, setting error message: {poolSetup.errorMessage}");
                    _errorMessage = poolSetup.errorMessage;

                    return _currentStep;
                }

                AnsiConsole.Clear();
            }

            _settings.Pool = chosenPool?.Name;

            if (_isSimpleSetup)
            {
                return _steps.Count - 1;
            }

            return _currentStep + 1;
        }

        private async Task<int> FinalConfirmationAsync()
        {
            IPool chosenPool = _data.GetChosenPool();
            (IHasher cpuHasher, IHasher gpuHasher) = _data.GetChosenHasher();
            (Wallet wallet, string publicKey) = await _settings.GetWalletAsync();

            SelectionPrompt<int> selectionPrompt = new SelectionPrompt<int>();
            selectionPrompt.WrapAround = true;
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nAll settings can be manually changed in [b]{Settings.FilePath}[/]\n\nWallet: {publicKey ?? "??"}\nHasher: CPU - {cpuHasher?.Name ?? "N/A"} ({_settings.CPUSetting.CPUThreads} threads), GPU - {gpuHasher?.Name ?? "N/A"}\nPool: {chosenPool?.DisplayName ?? "None"}\n");
            selectionPrompt.EnableSearch();
            selectionPrompt.AddChoice(0);
            selectionPrompt.AddChoice(1);
            selectionPrompt.AddChoice(2);

            selectionPrompt.UseConverter((i) =>
            {
                switch (i)
                {
                    case 0:
                        return "Confirm (save changes)";
                    case 1:
                        return "Restart Setup";
                    case 2:
                        return "Exit (don't save changes)";
                }

                return "??";
            });

            int result = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            switch (result)
            {
                case 0:
                    _settings.UsedAdvancedSettings = !_isSimpleSetup;
                    await _settings.SaveAsync();
                    return _currentStep + 1;
                case 1:
                    return 0;
                case 2:
                    await _settings.ReloadAsync();
                    return _currentStep + 1;
            }

            return 0;
        }

        private async Task CreateNewWallet(Wallet solanaWallet)
        {
            if (solanaWallet != null)
            {
                ConfirmationPrompt test = new ConfirmationPrompt($"Replace existing wallet ({solanaWallet.Account.PublicKey})?");
                test.DefaultValue = false;

                if (!await test.ShowAsync(AnsiConsole.Console, _cts.Token))
                {
                    AnsiConsole.Clear();

                    return;
                }

                AnsiConsole.Clear();
            }

            string executableDirectory = Utils.GetExecutableDirectory();

            Mnemonic mnemonic = new Mnemonic(WordList.English, WordCount.Twelve);
            SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
            selectionPrompt.WrapAround = true;
            Wallet wallet = new Wallet(mnemonic);

            selectionPrompt.Title($"Public Key: {wallet.Account.PublicKey}\n\nKey phrase: [green]{String.Join(", ", mnemonic.Words)}[/].\nA file named '[bold]id.json[/]' will also be created in [bold]{executableDirectory}[/] that the client will use\n[red]Highly recommended to save a copy of each[/]");
            selectionPrompt.AddChoices("Confirm", "Back (discard key)");

            string result = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if (result == "Confirm")
            {
                string keyFile = Path.Combine(executableDirectory, "id.json");

                await File.WriteAllTextAsync(keyFile, JsonConvert.SerializeObject(wallet.Account.PrivateKey.KeyBytes.ToList()));
                _settings.KeyFile = keyFile;
            }
        }

        private async Task SearchWallet()
        {
            List<(Wallet wallet, string path)> potentialWallets = new List<(Wallet wallet, string path)>();

            await AddWallet(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "solana", "id.json"));
            await AddWallet(Path.Combine(Utils.GetExecutableDirectory(), "id.json"));

            string walletDirectory = Path.Combine(Path.Combine(Utils.GetExecutableDirectory(), Settings.VanitySettings.Directory), "wallets");

            if (Directory.Exists(walletDirectory))
            {
                foreach (string exportedWallet in Directory.GetFiles(walletDirectory, "*.json"))
                {
                    await AddWallet(exportedWallet);
                }
            }

            SelectionPrompt<int> selectionPrompt = new SelectionPrompt<int>();
            selectionPrompt.WrapAround = true;
            selectionPrompt.Title(potentialWallets.Count == 0 ? "Found no wallet keys in default location for solana-cli or client" : $"Found {potentialWallets.Count} potential wallets");
            selectionPrompt.EnableSearch();
            selectionPrompt.UseConverter((i) =>
            {
                switch (i)
                {
                    case -1:
                        return "Manual Search";
                    case -2:
                        return "Exit";
                    default:
                        if (i >= potentialWallets.Count)
                        {
                            return "???";
                        }

                        return $"Wallet: {potentialWallets[i].wallet.Account.PublicKey}. Path: {potentialWallets[i].path}";
                }
            });

            for (int i = 0; i < potentialWallets.Count; i++)
            {
                selectionPrompt.AddChoice(i);
            }

            selectionPrompt.AddChoice(-1);
            selectionPrompt.AddChoice(-2);

            int selection = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if (selection >= 0)
            {
                _settings.KeyFile = potentialWallets[selection].path;
            }
            else if (selection == -1)
            {
                while (true)
                {
                    TextPrompt<string> filePath = new TextPrompt<string>("Solana keypair file path:");
                    filePath.AllowEmpty();
                    string path = await filePath.ShowAsync(AnsiConsole.Console, _cts.Token);
                    AnsiConsole.Clear();

                    if (String.IsNullOrEmpty(path))
                    {
                        await SearchWallet();
                        return;
                    }


                    Wallet wallet = await AddWallet(path);

                    if (wallet == null)
                    {
                        string reason = File.Exists(path) ? "Failed to import key" : "File doesn't exist";

                        ConfirmationPrompt prompt = new ConfirmationPrompt($"{reason} ({path}). Try again?");

                        if (await prompt.ShowAsync(AnsiConsole.Console, _cts.Token))
                        {
                            continue;
                        }

                        AnsiConsole.Clear();

                        return;
                    }
                    else
                    {
                        _settings.KeyFile = path;

                        return;
                    }
                }
            }

            //Adds to a list and returns
            async Task<Wallet> AddWallet(string file)
            {
                if (!File.Exists(file))
                {
                    return null;
                }

                string text = await File.ReadAllTextAsync(file);

                try
                {
                    byte[] keyPair = JsonConvert.DeserializeObject<byte[]>(text);

                    if (keyPair == null)
                    {
                        return null;
                    }

                    Wallet wallet = new Wallet(keyPair, seedMode: SeedMode.Bip39);

                    potentialWallets.Add((wallet, file));

                    return wallet;
                }
                catch (Exception)
                {
                    //Might be good to log reason, but should only fail due to user changing the file

                    return null;
                }
            }
        }

        private async Task SetupPublicKey(string current)
        {
            Base58Encoder encoder = new Base58Encoder();

            TextPrompt<string> publicKeyPrompt = new TextPrompt<string>("Public Key:");
            publicKeyPrompt.Validate((str) =>
            {
                try
                {
                    if (encoder.DecodeData(str).Length != 32)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }

                return true;
            });

            publicKeyPrompt.DefaultValue(current);
            string publicKey = await publicKeyPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            AnsiConsole.Clear();

            if (String.IsNullOrEmpty(publicKey))
            {
                _settings.PublicKey = String.Empty;

                return;
            }

            _settings.KeyFile = String.Empty;
            _settings.PublicKey = publicKey;
        }
    }
}
