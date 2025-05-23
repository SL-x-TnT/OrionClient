using Newtonsoft.Json;
using OrionClientLib.Hashers;
using OrionClientLib.Modules.SettingsData;
using OrionClientLib.Pools;
using OrionEventLib;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using System.Reflection;

namespace OrionClientLib
{
    public class Settings
    {
        public static string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

        [SettingDetails("Pool", "Change pool implementation")]
        [TypeValidator<BasePool>()]
        public string Pool { get; set; }

        [SettingDetails("Prevent Sleeping", "Prevent computer from going to sleep when mining (Windows only)")]
        public bool PreventSleeping { get; set; } = true;

        public List<int> GPUDevices { get; set; } = new List<int>();

        [JsonIgnore]
        public bool HasPrivateKey => !String.IsNullOrEmpty(KeyFile);

        public string KeyFile { get; set; }
        public string PublicKey { get; set; }

        //public bool EnableDebugging { get; set; }
        public bool MigratedSettings { get; set; } = false;
        public bool UsedAdvancedSettings { get; set; }

        [JsonIgnore]
        public bool NeedsSetup => String.IsNullOrEmpty(CPUSetting.CPUHasher) || String.IsNullOrEmpty(GPUSetting.GPUHasher) || String.IsNullOrEmpty(Pool) || (String.IsNullOrEmpty(PublicKey) && String.IsNullOrEmpty(KeyFile));


        [SettingDetails("View CPU Settings", "Configure CPU settings")]
        public CPUSettings CPUSetting { get; set; } = new CPUSettings();
        [SettingDetails("View GPU Settings", "Configure GPU settings")]
        public GPUSettings GPUSetting { get; set; } = new GPUSettings();
        [SettingDetails("View Vanity Settings", "Configure Vanity settings")]
        public VanitySettings VanitySetting { get; set; } = new VanitySettings();
        [SettingDetails("View RPC Settings", "Configure RPC settings")]
        public RPCSettings RPCSetting { get; set; } = new RPCSettings();
        [SettingDetails("View Staking View Settings", "Configure Staking View settings")]
        public StakingViewSettings StakingViewSetting { get; set; } = new StakingViewSettings();
        [SettingDetails("View Event Settings", "Configure Event settings that handle sending events to an external server")]
        public EventWebsocketSettings EventWebsocketSetting { get; set; } = new EventWebsocketSettings();
        [SettingDetails("View Bitz RPC Settings", "Configure Bitz RPC settings for Eclipse blockchain")]
        public BitzRPCSettings BitzRPCSetting { get; set; } = new BitzRPCSettings();

        public class CPUSettings
        {
            [SettingDetails("Hasher", "Change CPU hasher implementation")]
            [TypeValidator<BaseCPUHasher>()]
            public string CPUHasher { get; set; } = "Stock";

            [SettingDetails("Threads", "Total threads to use for mining (0 = all threads)")]
            [ThreadValidator]
            public int CPUThreads { get; set; } = 1;

            [SettingDetails("Auto Set CPU Affinity", "Automatically sets CPU affinity when only CPU mining is enabled and only using physical thread count (windows only)")]
            public bool AutoSetCPUAffinity { get; set; } = true;

            [SettingDetails("Min Hash Time", "Minimum time, in seconds, CPU will hash before updating UI. High core count CPUs should change this to about 3")]
            [MinMaxSettingValidation<double>(0.5, 10)]
            public double MinimumHashTime { get; set; } = 1.75;

        }

        public class GPUSettings
        {

            [SettingDetails("Hasher", "Change GPU hasher implementation")]
            [TypeValidator<BaseGPUHasher>()]
            public string GPUHasher { get; set; } = "Disabled";

            [SettingDetails("Batch Size", "Higher values use more ram and take longer to run. Lower values can cause lower hashrates")]
            [OptionSettingValidation<int>(4096, 2048, 1024, 512, 256, 128, 64, 32, 16, 8, 4, 2, 1)]
            public int MaxGPUNoncePerBatch { get; set; } = 2048;

            //[SettingDetails("Block Size", "Can try different values to see if HashX performance changes. GPU specific implementations will override this value.")]
            //[OptionSettingValidation<int>(512, 256, 128, 64, 32, 16)]
            public int GPUBlockSize { get; set; } = 512;

            [SettingDetails("Program Generation Threads", "Total CPU threads to use to generation program instructions (0 = all threads)")]
            [ThreadValidator]
            public int ProgramGenerationThreads { get; set; } = 0;

            [SettingDetails("Enable Experimental Hashers", "Enables/Disables displaying experimental hashers")]
            public bool EnableExperimentalHashers { get; set; } = false;

        }

        public class VanitySettings
        {
            public const string Directory = "vanity_data";

            public List<int> GPUDevices { get; set; } = new List<int>();

            [SettingDetails("Max GPU RAM Usage (MB)", "Maximum GPU RAM that will be used per device. CPU will a similar amount to the total of all GPUs")]
            [OptionSettingValidation<int>(8192, 4096 + 2048, 4096, 2048 + 1024, 2048, 1024 + 512, 1024, 512)]
            public int MaxRAMUsageMB { get; set; } = 1024;

            public string VanitySearchFile { get; set; } = "search.txt";
            public string VanityOutputFile { get; set; } = "foundWallets.txt";

            [SettingDetails("Block Size", "Default should provide best performance")]
            [OptionSettingValidation<int>(512, 256, 128, 64, 32, 16)]
            public int GPUBlockSize { get; set; } = 256;


            [SettingDetails("Minimum Character Length", "Minimum character length to search. Useful to filter when importing a dictionary list")]
            public int MinimumCharacterLength { get; set; } = 0;

            [SettingDetails("Vanity Threads", "Total CPU threads to use to validate found vanities (0 = all threads)")]
            [ThreadValidator]
            public int VanityThreads { get; set; } = 0;
        }

        public class RPCSettings
        {
            public enum RPCProvider { Unknown, Solana, Helius, Quicknode };

            [JsonIgnore]
            public RPCProvider Provider => GetProvider();

            private RPCProvider GetProvider()
            {
                if (Url == DefaultRPC)
                {
                    return RPCProvider.Solana;
                }

                if (Url?.Contains("helius-rpc") == true)
                {
                    return RPCProvider.Helius;
                }
                else if (Url?.Contains("quiknode.pro") == true)
                {
                    return RPCProvider.Quicknode;
                }

                return RPCProvider.Unknown;
            }

            public const string DefaultRPC = "https://api.mainnet-beta.solana.com/";

            [SettingDetails("RPC URL", $"RPC URL to use for requests. Default: {DefaultRPC}")]
            [UrlSettingValidation]
            public string Url { get; set; } = DefaultRPC;
        }

        public class StakingViewSettings
        {
            public const string Directory = "staking_data";

            public string StakingViewCacheFile { get; set; } = "cache.json";

            [SettingDetails("Historical Days", "Total days of history to keep for daily boost rewards")]
            public int TotalHistoricalDays { get; set; } = 7;
        }

        public class EventWebsocketSettings
        {
            [SettingDetails("Enable", "Enable/Disable sending event data to external server (Requires restart)")]
            public bool Enable { get; set; }

            [SettingDetails("Host", "URL to send event data")]
            [UrlSettingValidation]
            public string WebsocketUrl { get; set; } = "localhost";

            [SettingDetails("Port", "Port number")]
            public int Port { get; set; } = 54321;

            [SettingDetails("Id", "Arbitrary id that's sent in all events")]
            public string Id { get; set; } = String.Empty;

            [SettingDetails("Reconnect Time", "How often, in milliseconds, to try connecting to server")]
            public int ReconnectTimeMs { get; set; } = 5000;

            [SettingDetails("Serialization Type", "Type of serialization to use for event messages . (0 = Binary, 1 = Json)")]
            [OptionSettingValidation<SerializationType>(SerializationType.Binary, SerializationType.Json)]
            public SerializationType Serialization { get; set; } = SerializationType.Binary;
        }

        public class BitzRPCSettings
        {
            public enum RPCProvider { Unknown, Eclipse };

            [JsonIgnore]
            public RPCProvider Provider => GetProvider();

            private RPCProvider GetProvider()
            {
                if (Url?.Contains("eclipse") == true)
                {
                    return RPCProvider.Eclipse;
                }
                return RPCProvider.Unknown;
            }

            public const string DefaultRPC = "https://bitz-000.eclipserpc.xyz/";

            [SettingDetails("RPC URL", $"RPC URL to use for Bitz requests. Default: {DefaultRPC}")]
            [UrlSettingValidation]
            public string Url { get; set; } = DefaultRPC;

            public static readonly string[] AvailableRPCs = new[]
            {
                "https://bitz-000.eclipserpc.xyz/",
                "https://mainnetbeta-rpc.eclipse.xyz/",
                "https://eclipse.helius-rpc.com/"
            };
        }

        public static async Task<Settings> LoadAsync()
        {
            if (!File.Exists(FilePath))
            {
                Settings settings = new Settings();
                settings.CPUSetting.CPUThreads = Environment.ProcessorCount;

                return settings;
            }

            var s = await File.ReadAllTextAsync(FilePath);
            var t = JsonConvert.DeserializeObject<Settings>(s);

            if (t == null)
            {
                throw new Exception("Failed to read setting file");
            }

            //Migrats to new settings
            if (!t.MigratedSettings)
            {
                t.CPUSetting.CPUThreads = t.CPUSetting.CPUThreads;
                t.MigratedSettings = true;
            }

            return t;
        }

        public async Task ReloadAsync()
        {
            Settings oldSettings = await LoadAsync();

            PropertyInfo[] properties = typeof(Settings).GetProperties();

            foreach (PropertyInfo property in properties)
            {
                if (property.SetMethod != null)
                {
                    var oldValue = property.GetValue(oldSettings);
                    property.SetValue(this, oldValue);
                }
            }
        }

        public async Task<List<SettingChange>> GetChanges()
        {
            List<SettingChange> changes = new List<SettingChange>();

            Settings oldSettings = await LoadAsync();

            CheckChanges(this, oldSettings);

            return changes;

            void CheckChanges(object baseObj, object oldSettings, string path = null)
            {
                foreach (PropertyInfo property in baseObj.GetType().GetProperties())
                {
                    SettingDetailsAttribute details = property.GetCustomAttribute<SettingDetailsAttribute>();

                    if (details != null)
                    {
                        var oldValue = property.GetValue(oldSettings);
                        var newValue = property.GetValue(baseObj);

                        if (property.PropertyType != typeof(string) && property.PropertyType.IsClass)
                        {
                            CheckChanges(newValue, oldValue, path == null ? details.Name : $"{path} > {details.Name}");
                        }
                        else if ((oldValue == null && newValue != null) || oldValue?.Equals(newValue) == false)
                        {
                            changes.Add(new SettingChange
                            {
                                OldValue = oldValue ?? "Unknown",
                                NewValue = newValue ?? "Unknown",
                                Path = path == null ? details.Name : $"{path} > {details.Name}",
                                Setting = details.Name
                            });
                        }
                    }
                }
            }
        }

        public Task SaveAsync()
        {
            return File.WriteAllTextAsync(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public async Task<(Wallet, string)> GetWalletAsync()
        {
            if (!HasPrivateKey)
            {
                if (String.IsNullOrEmpty(PublicKey))
                {
                    return default;
                }

                Base58Encoder encoder = new Base58Encoder();

                try
                {
                    byte[] key = encoder.DecodeData(PublicKey);

                    //Invalid length
                    if (key.Length != 32)
                    {
                        return default;
                    }
                }
                catch (Exception)
                {
                    return default;
                }

                return (null, PublicKey);
            }

            if (String.IsNullOrEmpty(KeyFile))
            {
                return default;
            }

            if (!File.Exists(KeyFile))
            {
                return default;
            }

            string text = await File.ReadAllTextAsync(KeyFile);
            byte[] keyPair = JsonConvert.DeserializeObject<byte[]>(text);

            if (keyPair == null)
            {
                return default;
            }

            Wallet wallet = new Wallet(keyPair, seedMode: SeedMode.Bip39);

            return (wallet, wallet.Account.PublicKey);
        }

        public class SettingChange
        {
            public string Path { get; set; }
            public string Setting { get; set; }
            public object OldValue { get; set; }
            public object NewValue { get; set; }
        }
    }

    public interface ISettingInfo
    {
        public string Name { get; }
        public string Description { get; }
        public bool DisplaySetting { get; }
    }
}