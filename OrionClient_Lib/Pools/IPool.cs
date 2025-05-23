using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools.Models;
using Solnet.Wallet;

namespace OrionClientLib.Pools
{
    [Flags]
    public enum Coin { Ore, Coal, Chromium, Bitz };

    public interface IPool
    {
        public string Name { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string ArgName { get; }
        public Dictionary<string, string> Features { get; }
        public bool HideOnPoolList { get; }
        public Coin Coins { get; }
        public bool RequiresKeypair { get; }

        public event EventHandler<NewChallengeInfo> OnChallengeUpdate;
        public event EventHandler<(string[] columns, byte[] data)> OnMinerUpdate;
        public event EventHandler PauseMining;
        public event EventHandler ResumeMining;

        public void SetWalletInfo(Wallet wallet, string publicKey);
        public Task<(bool success, string errorMessage)> SetupAsync(CancellationToken token, bool initialSetup = false);

        public Task<bool> ConnectAsync(CancellationToken token);
        public Task<bool> DisconnectAsync();
        public Task<double> GetFeeAsync(CancellationToken token);
        public Task<(bool success, string errorMessage)> OptionsAsync(CancellationToken token);

        public string[] TableHeaders();
        public void DifficultyFound(DifficultyInfo info);
    }

    public abstract class BasePool : IPool, ISettingInfo
    {
        public abstract string Name { get; }
        public abstract string DisplayName { get; }
        public abstract bool DisplaySetting { get; }
        public abstract string ArgName { get; }
        public abstract string Description { get; }
        public abstract Dictionary<string, string> Features { get; }
        public abstract bool HideOnPoolList { get; }
        public abstract Coin Coins { get; }
        public abstract bool RequiresKeypair { get; }

        public abstract event EventHandler<NewChallengeInfo> OnChallengeUpdate;
        public abstract event EventHandler<(string[] columns, byte[] data)> OnMinerUpdate;
        public abstract event EventHandler PauseMining;
        public abstract event EventHandler ResumeMining;

        public abstract void SetWalletInfo(Wallet wallet, string publicKey);
        public abstract Task<(bool success, string errorMessage)> SetupAsync(CancellationToken token, bool initialSetup = false);

        public abstract Task<bool> ConnectAsync(CancellationToken token);
        public abstract Task<bool> DisconnectAsync();
        public abstract Task<double> GetFeeAsync(CancellationToken token);
        public abstract Task<(bool success, string errorMessage)> OptionsAsync(CancellationToken token);

        public abstract string[] TableHeaders();
        public abstract void DifficultyFound(DifficultyInfo info);
    }
}
