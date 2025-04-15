using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.SettingsData
{
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

        [SettingDetails("RPC URL", $"RPC URL to use for requests. Default: {DefaultRPC}")]
        [UrlSettingValidation]
        public string Url { get; set; } = DefaultRPC;

        public static readonly string[] AvailableRPCs = new[]
        {
            "https://bitz-000.eclipserpc.xyz/",
            "https://mainnetbeta-rpc.eclipse.xyz/",
            "https://eclipse.helius-rpc.com/"
        };
    }
} 