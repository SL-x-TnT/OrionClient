using DrillX;
using DrillX.Solver;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using OrionClientLib.CoinPrograms;
using OrionClientLib.Hashers.Models;
using OrionClientLib.Modules.Models;
using OrionClientLib.Pools.HQPool;
using OrionClientLib.Pools.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using Spectre.Console;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public enum OreHQRequestTypes { Ready, Submission };
    public enum OreHQResponseTypes { StartMining, SubmissionResult };

    public abstract class OreHQPool : WebsocketPool
    {
        private static readonly Logger _logger = LogManager.GetLogger("Main");

        public override event EventHandler<NewChallengeInfo> OnChallengeUpdate;
        public override event EventHandler<(string[] columns, byte[] data)> OnMinerUpdate;
        public override event EventHandler PauseMining;
        public override event EventHandler ResumeMining;

        public abstract override string Name { get; }
        public abstract override string DisplayName { get; }
        public abstract override string Description { get; }
        public abstract override Dictionary<string, string> Features { get; }
        public abstract override Coin Coins { get; }
        public abstract string Website { get; }
        public abstract bool StakingEnabled { get; }

        public abstract override bool HideOnPoolList { get; }
        public override bool RequiresKeypair { get; } = true;
        public override Uri WebsocketUrl => new Uri($"wss://{HostName}/v2/ws?timestamp={_timestamp}");
        public virtual Dictionary<Coin, double> MiniumumRewardPayout => new Dictionary<Coin, double> { { Coins, 0 } };


        protected HttpClient _client;
        protected MinerPoolInformation _minerInformation;
        protected HQPoolSettings _poolSettings;
        protected ulong _timestamp = 0;
        protected DifficultyInfo _bestDifficulty;
        protected int _currentBestDifficulty = 0;
        protected string _errorMessage = String.Empty;

        private bool _sendingReadyUp = false;
        private int _serverSentMineTransactions = 0;

        #region Overrides

        public override async void DifficultyFound(DifficultyInfo info)
        {
            if(info.BestDifficulty <= _currentBestDifficulty)
            {
                return;
            }

            _currentBestDifficulty = info.BestDifficulty;

            if (_currentBestDifficulty > 9)
            {
                await SendPoolSubmission(info);
            }
        }

        public override async Task<double> GetFeeAsync(CancellationToken token)
        {
            return 5;
        }

        public override async void OnMessage(ArraySegment<byte> buffer, WebSocketMessageType type)
        {
            try
            {
                if (type == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer);

                    const string serverMineSend = "Server is sending mine transaction...";

                    if (message == serverMineSend)
                    {
                        //"info" log with best difficulty submitted
                        if (_bestDifficulty != null && _serverSentMineTransactions == 0)
                        {
                            _logger.Log(LogLevel.Info, $"Challenge Id: {_bestDifficulty.ChallengeId}. Best Difficulty: {_bestDifficulty.BestDifficulty}. Best Nonce: {_bestDifficulty.BestNonce}");
                        }

                        _serverSentMineTransactions++;

                        //Continue to attempt to send readyup message until successful
                        if (!_sendingReadyUp)
                        {
                            _sendingReadyUp = true;

                            while (!await SendReadyUp())
                            {
                                await Task.Delay(1000);
                            }

                            _sendingReadyUp = false;
                        }
                    }
                    else
                    {
                        _logger.Log(LogLevel.Debug, $"Server sent message: {message}");
                    }
                }
                else
                {
                    if (buffer.Count > 0)
                    {
                        switch ((OreHQResponseTypes)buffer[0])
                        {
                            case OreHQResponseTypes.StartMining:
                                {
                                    OreHQChallengeResponse challenge = new OreHQChallengeResponse();
                                    challenge.Deserialize(buffer);

                                    HandleNewChallenge(challenge);
                                }
                                break;
                            case OreHQResponseTypes.SubmissionResult:
                                {
                                    HandleSubmissionResult(buffer);
                                }
                                break;
                            default:
                                _logger.Log(LogLevel.Warn, $"Unknown message type {buffer[0]}");
                                break;
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Warn, ex, $"Failed to parse message from server. Reason: {ex.Message}");
            }
        }

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(OreHQPoolStake))]
        public override async Task<bool> ConnectAsync(CancellationToken token)
        {
            if(!await UpdateTimestampAsync(token))
            {
                return false;
            }

            byte[] tBytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tBytes, _timestamp);

            Base58Encoder _encoder = new Base58Encoder();
            byte[] sigBytes = RequiresKeypair ? _wallet.Sign(tBytes) : new byte[64];
            string sig = _encoder.EncodeData(sigBytes);

            _authorization = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_publicKey}:{sig}"))}";
            bool result = await base.ConnectAsync(token);

            await RefreshStakeBalancesAsync(false, token);

            return result && await SendReadyUp(false);
        }

        public override void SetWalletInfo(Wallet wallet, string publicKey)
        {
            _minerInformation ??= new MinerPoolInformation(Coins);
            _poolSettings ??= new HQPoolSettings(Name);

            if (!String.IsNullOrEmpty(HostName))
            {
                _client ??= new HttpClient
                {
                    BaseAddress = new Uri($"{(_poolSettings.IsHttps == false ? "http" : "https")}://{WebsocketUrl.Host}"),
                    Timeout = TimeSpan.FromSeconds(10)
                };
            }

            base.SetWalletInfo(wallet, publicKey);
        }

        public override async Task<(bool, string)> SetupAsync(CancellationToken token, bool initialSetup = false)
        {
            bool isComplete = true;
            string errorMessage = String.Empty;

            await AnsiConsole.Status().StartAsync($"Setting up {Name} pool", async ctx =>
            {
                if (RequiresKeypair && _wallet == null)
                {
                    errorMessage = "A full keypair is required to sign message for this pool. Private keys are never sent to the server";

                    isComplete = false;
                }
                else
                {
                    try
                    {
                        if (!await RegisterAsync(token))
                        {
                            errorMessage = $"Failed to signup to pool";

                            isComplete = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        errorMessage = $"Failed to complete setup. Reason: {ex.Message}";
                        isComplete = false;
                    }
                }
            });

            return (isComplete, errorMessage);
        }

        public override async Task<(bool, string)> OptionsAsync(CancellationToken token)
        {
            await _poolSettings.LoadAsync();

            if(WebsocketUrl == null)
            {
                return (false, $"Domain is empty");
            }

            (bool success, string errorMessage) balanceRefresh = await RefreshStakeBalancesAsync(true, token);

            if(!balanceRefresh.success)
            {
                return (false, !String.IsNullOrEmpty(balanceRefresh.errorMessage) ? $"Failed to pull balance information. Error: {balanceRefresh.errorMessage}" : String.Empty);
            }

            if(token.IsCancellationRequested)
            {
                return (false, String.Empty);
            }

            await DisplayOptionsAsync(token);

            return (true, String.Empty);
        }

        public override string[] TableHeaders()
        {
            return ["Time", "Id", "Diff", "Mining Rewards", /*"Staking Rewards",*/ $"Pool Rewards", "Unclaimed Rewards", "Unclaimed Stake"];
        }

        #endregion

        #region Display Options

        protected async Task DisplayOptionsAsync(CancellationToken token)
        {
            while (true)
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine($"Public key: {_publicKey}");

                if(!String.IsNullOrEmpty(_poolSettings.ClaimWallet))
                {
                    builder.AppendLine($"Claim wallet: {_poolSettings.ClaimWallet ?? _publicKey}");
                }

                builder.AppendLine();

                foreach(Coin c in Enum.GetValues(typeof(Coin)))
                {
                    if(!Coins.HasFlag(c))
                    {
                        continue;
                    }

                    var walletBalance = _minerInformation.WalletBalance[c];
                    var miningReward = _minerInformation.TotalMiningRewards[c];
                    var stakeReward = _minerInformation.TotalStakeRewards[c];

                    builder.AppendLine($"{c} Info:");
                    builder.AppendLine($"   {"Wallet Balance".PadRight(14)} {walletBalance} {c}");
                    builder.AppendLine($"   {"Mining Rewards".PadRight(14)} {miningReward} {c} " +
                        $"{(miningReward.BalanceChangeSinceUpdate > 0 ? $"([green]+{miningReward.BalanceChangeSinceUpdate:0.00000000000}[/])" : String.Empty)}" +
                        $"{(miningReward.TotalChange > 0 ? $"[[[Cyan]+{miningReward.TotalChange:0.00000000000} since start[/]]]" : String.Empty)}");

                    //if (StakingEnabled)
                    //{
                    //    builder.AppendLine($"   {"Stake Rewards".PadRight(14)} {stakeReward} {c} " +
                    //    $"{(stakeReward.BalanceChangeSinceUpdate > 0 ? $"([green]+{stakeReward.BalanceChangeSinceUpdate:0.00000000000}[/])" : String.Empty)}" +
                    //    $"{(stakeReward.TotalChange > 0 ? $"[[[Cyan]+{stakeReward.TotalChange:0.00000000000} since start[/]]]" : String.Empty)}");
                    //}

                    builder.AppendLine();
                }

                //if (_minerInformation.Stakes?.Count > 0)
                //{
                //    builder.AppendLine();
                //    builder.AppendLine($"Stake Info:");

                //    if (_minerInformation.Stakes != null)
                //    {
                //        foreach (var stakeInfo in _minerInformation.Stakes)
                //        {
                //            OreProgram.BoostMints.TryGetValue(new PublicKey(stakeInfo.MintPubkey), out (string name, int decimals) data);

                //            builder.AppendLine($"   {(data.name ?? "??").PadRight(11)} {stakeInfo.StakedBalance / Math.Pow(10, !String.IsNullOrEmpty(data.name) ? data.decimals : 11)}");
                //        }
                //    }
                //}

                if (!String.IsNullOrEmpty(_errorMessage))
                {
                    builder.AppendLine($"\n{_errorMessage}");

                    _errorMessage = String.Empty;
                }

                const string claimRewardBalance = "Claim Mining Rewards";
                const string claimStakeBalance = "Claim Staking Rewards";
                const string changeWallet = "Change Claim Wallet";
                const string refreshBalance = "Refresh Balance";
                const string viewWebsite = "View Website";

                SelectionPrompt<(string, Coin)> choices = new SelectionPrompt<(string, Coin)>();
                choices.Title(builder.ToString());
                choices.UseConverter((obj) =>
                {
                    var c = obj.Item2;

                    string message = obj.Item1;


                    if (obj.Item1 == claimRewardBalance)
                    {
                        if (_minerInformation.TotalMiningRewards.TryGetValue(c, out var b) && b.CurrentBalance < MiniumumRewardPayout[c])
                        {
                            message = $"[red]{obj.Item1} (Min: {MiniumumRewardPayout[c]})[/]";
                        }

                        if(_wallet == null)
                        {
                            message = $"[red]{obj.Item1} (Full key required)[/]";
                        }
                    }
                    else if (obj.Item1 == claimStakeBalance)
                    {
                        if (_minerInformation.TotalStakeRewards.TryGetValue(c, out var b) && b.CurrentBalance < MiniumumRewardPayout[c])
                        {
                            message =  $"[red]{obj.Item1} (Min: {MiniumumRewardPayout[c]})[/]";
                        }

                        if(_wallet == null)
                        {
                            message = $"[red]{obj.Item1} (Full key required)[/]";
                        }
                    }

                    return message;
                });

                if (!String.IsNullOrEmpty(Website) && OperatingSystem.IsWindows() && Environment.UserInteractive)
                {
                    choices.AddChoice((viewWebsite, Coins));
                }

                choices.AddChoice((claimRewardBalance, Coins));

                //if (StakingEnabled)
                //{
                //    choices.AddChoice((claimStakeBalance, Coins));
                //}


                //foreach(Coin c in Enum.GetValues(typeof(Coin)))
                //{
                //    if(!Coins.HasFlag(c))
                //    {
                //        continue;
                //    }

                //    List<(string, Coin)> innerChoices = new List<(string, Coin)>();

                //    innerChoices.Add((claimRewardBalance, c));
                //    innerChoices.Add((claimStakeBalance, c));

                //    if (_minerInformation.TotalMiningRewards[c].CurrentBalance >= MiniumumRewardPayout[c])
                //    {
                //    }

                //    if (_minerInformation.TotalStakeRewards[c].CurrentBalance >= MiniumumRewardPayout[c])
                //    {
                //    }

                //    if(innerChoices.Count > 0)
                //    {
                //        choices.AddChoiceGroup(($"{c} ({MiniumumRewardPayout[c]} min payout)", c), innerChoices);
                //    }
                //}


                choices.AddChoice((refreshBalance, Coins));
                choices.AddChoice((changeWallet, Coins));
                choices.AddChoice(("Exit", Coins));

                (string choice, Coin coins) = await choices.ShowAsync(AnsiConsole.Console, token);

                switch (choice)
                {
                    case refreshBalance:
                        await RefreshStakeBalancesAsync(true, token);
                        break;
                    case changeWallet:
                        await SetClaimWalletOptionAsync(token);
                        break;
                    case claimStakeBalance:
                        await ClaimStakeOptionAsync(coins, token);
                        break;
                    case claimRewardBalance:
                        await ClaimRewardsOptionAsync(coins, token);
                        break;
                    case viewWebsite:
                        await ViewWebsiteOptionAsync(token);
                        break;
                    default:
                        return;
                }

                AnsiConsole.Clear();
            }

        }

        protected async Task SetClaimWalletOptionAsync(CancellationToken token)
        {
            Base58Encoder encoder = new Base58Encoder();

            TextPrompt<string> textPrompt = new TextPrompt<string>("Set wallet to claim rewards to. Type [yellow]clear[/] to remove. Default: ");
            textPrompt.DefaultValue(_poolSettings.ClaimWallet);
            textPrompt.Validate((str) =>
            {
                if (str.ToLower() == "clear")
                {
                    return true;
                }

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

            string result = await textPrompt.ShowAsync(AnsiConsole.Console, token);

            if (result?.ToLower() == "clear")
            {
                _poolSettings.ClaimWallet = String.Empty;
            }
            else
            {
                _poolSettings.ClaimWallet = result ?? String.Empty;
            }

            await _poolSettings.SaveAsync();
        }

        protected async Task ClaimStakeOptionAsync(Coin coin, CancellationToken token)
        {
            if (_minerInformation.TotalStakeRewards.TryGetValue(coin, out var b) && b.CurrentBalance < MiniumumRewardPayout[coin])
            {
                return;
            }

            string message = String.Empty;

            while (true)
            {
                AnsiConsole.Clear();

                SelectionPrompt<OreHQPoolStake> selectionPrompt = new SelectionPrompt<OreHQPoolStake>();

                selectionPrompt.Title($"Choose staking pool to claim from {(!String.IsNullOrEmpty(message) ? $"\n\n{message}" : String.Empty)}");
                selectionPrompt.UseConverter((pool) =>
                {
                    return pool == null ? "Exit" : $"{pool.PoolName}: {pool.RewardsUI} {Coins}";
                });

                selectionPrompt.AddChoices(_minerInformation.Stakes.Where(x => x.RewardsUI > MiniumumRewardPayout[coin]));
                selectionPrompt.AddChoice(null);
                message = String.Empty;

                OreHQPoolStake result = await selectionPrompt.ShowAsync(AnsiConsole.Console, token);

                //Exit
                if (result == null)
                {
                    return;
                }

                //Display claim prompt
                TextPrompt<double> claimPrompt = new TextPrompt<double>($"Enter claim amount between ({MiniumumRewardPayout[coin]}-{result.RewardsUI}): ");
                claimPrompt.DefaultValue(result.RewardsUI);
                claimPrompt.Validate((v) =>
                {
                    return v == 0 || (v >= MiniumumRewardPayout[coin] && v <= result.RewardsUI);
                });

                double claimAmount = await claimPrompt.ShowAsync(AnsiConsole.Console, token);

                if(claimAmount == 0)
                {
                    return;
                }

                //Confirmation
                string claimWallet = _poolSettings.ClaimWallet ?? _wallet?.Account.PublicKey;
                bool isSame = claimWallet == _wallet.Account.PublicKey;

                ConfirmationPrompt confirmationPrompt = new ConfirmationPrompt($"Claim {claimAmount} {Coins} from {result.PoolName} pool to {claimWallet}" +
                    $"{(!isSame ? $"\n[yellow]Warning: Claim wallet {claimWallet} is different from mining wallet {_wallet.Account.PublicKey}. Continue?[/]" : String.Empty)}?");
                confirmationPrompt.DefaultValue = false;

                if(await confirmationPrompt.ShowAsync(AnsiConsole.Console, token))
                {
                    //Try claim
                    (bool success, string message) claimResult = await ClaimStakeRewards(claimWallet, result.MintPubkey, (ulong)(claimAmount * result.Decimals), token);

                    if(claimResult.success)
                    {
                        message = $"[green]{claimResult.message}[/]";
                        result.RewardsBalance -= (long)(claimAmount * result.Decimals);
                    }
                    else
                    {
                        message = $"[red]{claimResult.message}[/]";
                    }
                }
                else
                {
                    message = $"[yellow]Notice: User canceled claiming[/]";
                }

            }
        }

        protected virtual async Task ClaimRewardsOptionAsync(Coin coin, CancellationToken token)
        {
            if (_minerInformation.TotalMiningRewards.TryGetValue(coin, out var b) && b.CurrentBalance < MiniumumRewardPayout[coin])
            {
                return;
            }

            string message = String.Empty;

            await RefreshStakeBalancesAsync(true, token);

            AnsiConsole.Clear();

            //Display claim prompt
            TextPrompt<double> claimPrompt = new TextPrompt<double>($"Enter claim amount between ({MiniumumRewardPayout[coin]}-{_minerInformation.TotalMiningRewards[coin].CurrentBalance}): ");
            claimPrompt.DefaultValue(_minerInformation.TotalMiningRewards[coin].CurrentBalance);
            claimPrompt.Validate((v) =>
            {
                return v == 0 || (v >= MiniumumRewardPayout[coin] && v <= _minerInformation.TotalMiningRewards[coin].CurrentBalance);
            });

            double claimAmount = await claimPrompt.ShowAsync(AnsiConsole.Console, token);

            if (claimAmount == 0)
            {
                return;
            }

            //Confirmation
            string claimWallet = _poolSettings.ClaimWallet ?? _wallet.Account.PublicKey;
            bool isSame = claimWallet == _wallet.Account.PublicKey;

            ConfirmationPrompt confirmationPrompt = new ConfirmationPrompt($"Claim {claimAmount} {coin} mining rewards to {claimWallet}" +
                $"{(!isSame ? $"\n[yellow]Warning: Claim wallet {claimWallet} is different from mining wallet {_wallet.Account.PublicKey}. Continue?[/]" : String.Empty)}?");
            confirmationPrompt.DefaultValue = false;

            if (await confirmationPrompt.ShowAsync(AnsiConsole.Console, token))
            {
                ulong ulongClaimAmount = (ulong)(claimAmount * (coin == Coin.Ore ? OreProgram.OreDecimals : CoalProgram.CoalDecimals));

                //Try claim
                (bool success, string message) claimResult = await ClaimMiningRewards(coin, claimWallet, ulongClaimAmount, token);

                if (claimResult.success)
                {
                    _errorMessage = $"[green]{claimResult.message}[/]";
                }
                else
                {
                    _errorMessage = $"[red]Failed to claim: {claimResult.message}[/]";
                }
            }
            else
            {
                _errorMessage = $"[yellow]Notice: User canceled claiming[/]";
            }
        }

        protected async Task ViewWebsiteOptionAsync(CancellationToken token)
        {
            string website = String.Format(Website, _publicKey);

            ConfirmationPrompt confirmationPrompt = new ConfirmationPrompt($"Open browser to view website '{website}'?");

            if(await confirmationPrompt.ShowAsync(AnsiConsole.Console, token))
            {
                Process.Start(new ProcessStartInfo(website) { UseShellExecute = true });
            }
        }

        #endregion

        #region HTTP Requests

        protected virtual async Task<bool> RegisterAsync(CancellationToken token)
        {
            using var response = await _client.PostAsync($"/v2/signup?miner={_publicKey}", null, token);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return false;
            }

            string data = await response.Content.ReadAsStringAsync();

            if(data == "SUCCESS" || data == "EXISTS")
            {
                return true;
            }

            return false;
        }

        protected virtual async Task<bool> UpdateTimestampAsync(CancellationToken token)
        {
            try
            {
                using var response = await _client.GetAsync($"/timestamp", token);

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return false;
                }

                string data = await response.Content.ReadAsStringAsync();

                if (!ulong.TryParse(data, out ulong value))
                {
                    return false;
                }

                _timestamp = value;

                return true;
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to request timestamp from {WebsocketUrl.Host}. Reason: {ex.Message}");

                return false;
            }
        }

        protected virtual async Task<(bool success, string errorMessage)> RefreshStakeBalancesAsync(bool displayUI, CancellationToken token, bool updateBalance = true)
        {
            try
            {
                if (displayUI)
                {
                    await AnsiConsole.Status().StartAsync($"Grabbing balance information", async ctx =>
                    {
                        await Update(updateBalance, token);
                    });
                }
                else
                {
                    await Update(updateBalance, token);
                }

                return (true, String.Empty);
            }
            catch(TaskCanceledException)
            {
                return (false, String.Empty);
            }
            catch(Exception ex)
            {
                return (false, ex.Message);
            }
        }

        protected virtual async Task Update(bool updateBalance, CancellationToken token)
        {
            if (updateBalance)
            {
                var balanceInfo = await GetBalanceAsync(token);

                if (balanceInfo.success)
                {
                    foreach (var bInfo in balanceInfo.balances)
                    {
                        _minerInformation.UpdateWalletBalance(bInfo.coin, bInfo.balance);
                    }
                }
            }

            var rewardInfo = await GetRewardAsync(token);

            if (rewardInfo.success)
            {
                foreach (var rInfo in rewardInfo.balances)
                {
                    _minerInformation.UpdateMiningRewards(rInfo.coin, rInfo.reward);
                }

            }

            //_minerInformation.UpdateStakes(await GetStakingInformationAsync(token));
        }

        protected virtual async Task<(List<(Coin, double)> value, bool success)> GetDataAsync(string endpoint, CancellationToken token)
        {
            List<(Coin, double)> balanceData = new List<(Coin, double)>();

            try
            {
                using var response = await _client.GetAsync($"/miner/{endpoint}?pubkey={_publicKey}", token);

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return (balanceData, false);
                }

                string data = await response.Content.ReadAsStringAsync();

                if (!double.TryParse(data, out double value))
                {
                    return (balanceData, false);
                }

                balanceData.Add((Coins, value));

                return (balanceData, true);
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to request {endpoint} data from pool. Reason: {ex.Message}");

                return (balanceData, false);
            }
        }

        protected virtual async Task<(List<(Coin coin, double balance)> balances, bool success)> GetBalanceAsync(CancellationToken token)
        {
            List<(Coin, double)> balances = new List<(Coin, double)>();

            var result = await GetDataAsync("balance", token);

            if(!result.success)
            {
                return (balances, false);
            }

            balances.AddRange(result.value);

            return (balances, true);
        }

        protected virtual async Task<(List<(Coin coin, double reward)> balances, bool success)> GetRewardAsync(CancellationToken token)
        {
            List<(Coin, double)> rewards = new List<(Coin, double)>();

            var result = await GetDataAsync("rewards", token);

            if (!result.success)
            {
                return (rewards, false);
            }

            rewards.AddRange(result.value);

            return (rewards, true);

        }

        protected virtual async Task<List<OreHQPoolStake>> GetStakingInformationAsync(CancellationToken token)
        {
            try
            {
                using var response = await _client.GetAsync($"/v2/miner/boost/stake-accounts?pubkey={_publicKey}", token);

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    return default;
                }

                string data = await response.Content.ReadAsStringAsync();

                try
                {
                    return JsonConvert.DeserializeObject<List<OreHQPoolStake>>(data);

                }
                catch(JsonReaderException) //Typically invalid pubkey
                {
                    return null;
                }
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to grab staking information from pool. Reason: {ex.Message}");

                return null;
            }
        }

        protected virtual async Task<(bool success, string message)> ClaimStakeRewards(string claimWallet, string mintId, ulong amount, CancellationToken token)
        {
            try
            {
                using var response = await _client.PostAsync($"/v3/claim-stake-rewards?pubkey={claimWallet}&mint={mintId}&amount={amount}", null, token);
                string data = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return (false, String.IsNullOrEmpty(data) ? $"Server returned status code {response.StatusCode}" : data);
                }

                switch (data)
                {
                    case "SUCCESS":
                        return (true, $"Successfully queued stake claim request to {claimWallet}. Balances will update after transaction is finalized");
                    case "QUEUED":
                        return (true, "Claim is already queued");
                    default:
                        if (ulong.TryParse(data, out ulong time))
                        {
                            ulong timeLeft = 1800 - time;
                            ulong secs = timeLeft % 60;
                            ulong mins = (timeLeft / 60) % 60;

                            return (false, $"You cannot claim until the time is up. Time left until next claim available: {mins}m {secs}s");
                        }

                        return (false, data);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Failed to claim stake rewards. Result: {ex.Message}");
            }
        }

        protected virtual async Task<(bool success, string message)> ClaimMiningRewards(Coin coin, string claimWallet, ulong amount, CancellationToken token)
        {
            try
            {
                if(!await UpdateTimestampAsync(token))
                {
                    return (false, $"Failed to get timestamp from server");
                }

                Base58Encoder _encoder = new Base58Encoder();

                byte[] tBytes = new byte[8+32+8];
                BinaryPrimitives.WriteUInt64LittleEndian(tBytes, _timestamp);
                _encoder.DecodeData(claimWallet).CopyTo(tBytes, 8);
                BinaryPrimitives.WriteUInt64LittleEndian(tBytes.AsSpan().Slice(40, 8), amount);

                byte[] sigBytes = _wallet.Sign(tBytes);
                string sig = _encoder.EncodeData(sigBytes);

                string authHeader = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_publicKey}:{sig}"))}";

                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/v2/claim?timestamp={_timestamp}&receiver_pubkey={claimWallet}&amount={amount}");
                requestMessage.Headers.TryAddWithoutValidation("Authorization", authHeader);

                using var response = await _client.SendAsync(requestMessage);
                string data = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return (false, String.IsNullOrEmpty(data) ? $"Server returned status code {response.StatusCode}" : data);
                }

                switch (data)
                {
                    case "SUCCESS":
                        return (true, $"Successfully queued claim request to {claimWallet}. Balances will update after transaction is finalized");
                    case "QUEUED":
                        return (true, "Claim is already queued");
                    default:
                        if (ulong.TryParse(data, out ulong time))
                        {
                            ulong timeLeft = 1800 - time;
                            ulong secs = timeLeft % 60;
                            ulong mins = (timeLeft / 60) % 60;

                            return (false, $"You cannot claim until the time is up. Time left until next claim available: {mins}m {secs}s");
                        }

                        return (false, data);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Unknown exception occurred while claiming. Result: {ex.Message}");
            }
        }

        #endregion

        #region WS Responses

        protected virtual void HandleNewChallenge(OreHQChallengeResponse challengeResponse)
        {
            _currentBestDifficulty = 0;
            _bestDifficulty = null;
            _serverSentMineTransactions = 0;

            OnChallengeUpdate?.Invoke(this, new NewChallengeInfo
            {
                ChallengeId = GenerateChallengeId(challengeResponse.Challenge),
                Challenge = challengeResponse.Challenge,
                StartNonce = challengeResponse.StartNonce,
                EndNonce = challengeResponse.EndNonce,
                TotalCPUNonces = (ulong)((challengeResponse.EndNonce - challengeResponse.StartNonce) * _poolSettings.CPUNonceRatio)
            });
        }

        protected virtual async void HandleSubmissionResult(ArraySegment<byte> buffer)
        {
            OreHQPoolSubmissionResponse submissionResponse = new OreHQPoolSubmissionResponse();
            submissionResponse.Deserialize(buffer);

            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            //This data takes ~20s to update properly, so everything is off by 1 update
            await RefreshStakeBalancesAsync(false, cts.Token, false);

            OnMinerUpdate?.Invoke(this, ([
                DateTime.Now.ToShortTimeString(), 
                GenerateChallengeId(submissionResponse.Challenge).ToString(),
                $"{submissionResponse.MinerSuppliedDifficulty}/{submissionResponse.Difficulty}",
                $"{submissionResponse.MinerEarnedRewards:0.00000000000}",
                //$"{_minerInformation.TotalStakeRewards.BalanceChangeSinceUpdate:0.00000000000}",
                $"{submissionResponse.TotalRewards:0.00000000000}",
                $"{_minerInformation.TotalMiningRewards[Coins]:0.00000000000}",
                $"{_minerInformation.TotalStakeRewards[Coins]:0.00000000000}",
            ], buffer.ToArray()));
        }

        protected virtual int GenerateChallengeId(byte[] data)
        {
            unchecked
            {
                const int p = 16777619;
                int hash = (int)2166136261;

                for (int i = 0; i < data.Length; i++)
                {
                    hash = (hash ^ data[i]) * p;
                }

                //Reduce to an easier number
                return Math.Abs(hash % 65536);
            }
        }

        #endregion

        #region WS Requests

        protected virtual async Task<bool> SendReadyUp(bool updateTime = true)
        {
            PauseMining?.Invoke(this, EventArgs.Empty);

            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            if(updateTime && !await UpdateTimestampAsync(cts.Token))
            {
                return false;
            }

            byte[] tBytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tBytes, _timestamp);
            byte[] sigBytes = RequiresKeypair ? _wallet.Sign(tBytes) : new byte[0];

            bool result = await SendMessageAsync(new ReadyRequestMessage
            {
                PublicKey = _wallet?.Account.PublicKey ?? new PublicKey(_publicKey),
                Timestamp = _timestamp,
                Signature = sigBytes
            });

            if(result)
            {
                _logger.Log(LogLevel.Debug, $"Waiting for new challenge [{_serverSentMineTransactions}]");

                return result;
            }

            _logger.Log(LogLevel.Warn, $"Failed to send ready up message");

            return result;
        }

        protected virtual async Task<bool> SendPoolSubmission(DifficultyInfo info)
        {
            _logger.Log(LogLevel.Debug, $"[{(info.IsCPU ? "CPU" : "GPU")}] Sending solution. Diff: {info.BestDifficulty}. Challenge Id: {info.ChallengeId}. Nonce: {info.BestNonce}");

            byte[] nonce = new byte[24];
            info.BestSolution.CopyTo(nonce, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan().Slice(16, 8), info.BestNonce);

            Base58Encoder encoder = new Base58Encoder();

            bool result = await SendMessageAsync(new DiffSubmissionRequest
            {
                Digest = info.BestSolution,
                Nonce = info.BestNonce,
                PublicKey = RequiresKeypair ? _wallet.Account.PublicKey : new PublicKey(_publicKey),
                B58Signature = RequiresKeypair ? encoder.EncodeData(_wallet.Account.Sign(nonce)) : String.Empty
            });

            if(result)
            {
                if(_bestDifficulty == null || info.BestDifficulty > _bestDifficulty.BestDifficulty)
                {
                    _bestDifficulty = info;
                }
            }

            return result;
        }

        #endregion

        #region Classes

        protected class MinerPoolInformation
        {
            public ConcurrentDictionary<Coin, BalanceTracker<double>> TotalStakeRewards { get; private set; } = new ConcurrentDictionary<Coin, BalanceTracker<double>>();
            public ConcurrentDictionary<Coin, BalanceTracker<double>> TotalMiningRewards { get; private set; } = new ConcurrentDictionary<Coin, BalanceTracker<double>>();
            public ConcurrentDictionary<Coin, BalanceTracker<double>> WalletBalance { get; private set; } = new ConcurrentDictionary<Coin, BalanceTracker<double>>();

            public List<OreHQPoolStake> Stakes { get; set; }

            private Coin _coin;

            public MinerPoolInformation(Coin coin)
            {
                foreach(Coin c in Enum.GetValues(typeof(Coin)))
                {
                    if(coin.HasFlag(c))
                    {
                        TotalStakeRewards.TryAdd(c, new BalanceTracker<double>());
                        TotalMiningRewards.TryAdd(c, new BalanceTracker<double>());
                        WalletBalance.TryAdd(c, new BalanceTracker<double>());
                    }
                }

                _coin = coin;
            }

            //public void UpdateStakes(List<OreHQPoolStake> stakes)
            //{
            //    if(stakes == null)
            //    {
            //        return;
            //    }

            //    Stakes = stakes;

            //    double totalStakeRewards = 0;

            //    var stakeMints = _coin.HasFlag(Coin.Ore) ? OreProgram.BoostMints : null;

            //    if(stakeMints == null)
            //    {
            //        return;
            //    }

            //    var boostAccounts = _coin.HasFlag(Coin.Ore) ? OreProgram.BoostMints : null;
                
            //    foreach(OreHQPoolStake stake in stakes)
            //    {
            //        if(boostAccounts != null && boostAccounts.TryGetValue(new PublicKey(stake.MintPubkey), out var d))
            //        {
            //            stake.PoolName = d.name;
            //            stake.Decimals = Math.Pow(10, d.decimals);
            //        }

            //        totalStakeRewards += stake.RewardsBalance / (_coin.HasFlag(Coin.Ore) ? OreProgram.OreDecimals : CoalProgram.CoalDecimals);
            //    }

            //    TotalStakeRewards[Coin.Ore].Update(totalStakeRewards);
            //}

            public void UpdateMiningRewards(Coin coin, double rewards)
            {
                TotalMiningRewards[coin].Update(rewards);
            }

            public void UpdateWalletBalance(Coin coin, double balance)
            {
                WalletBalance[coin].Update(balance);
            }
        }

        protected class HQPoolSettings : PoolSettings
        {
            public string ClaimWallet { get; set; }

            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string CustomDomain { get; set; }

            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public bool? IsHttps { get; set; }

            //This is how many of the returned nonces will be used for the CPU
            public double CPUNonceRatio { get; set; } = 0.33;

            public HQPoolSettings(string poolName) : base(poolName)
            {
            }
        }

        #endregion
    }
}
