using Newtonsoft.Json;
using NLog;
using OrionClientLib.CoinPrograms;
using OrionClientLib.Pools.CoalPool;
using OrionClientLib.Pools.HQPool;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using Spectre.Console;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public class ShinystCoalPool : OreHQPool
    {
        private static readonly Logger _logger = LogManager.GetLogger("Main");

        public override event EventHandler<(string[] columns, byte[] data)> OnMinerUpdate;
        public override string Name { get; } = "Excalivator Pool";
        public override string DisplayName => Name;
        public override bool DisplaySetting => true;
        public override string Description => $"[Cyan]{Coin.Coal}[/]/[green]{Coin.Ore}[/] pool using Ore-HQ implementation. Operator (discord): Shinyst";
        public override string ArgName => "excalivator";

        public override Uri WebsocketUrl => new Uri($"wss://{HostName}/v2/ws-pubkey?pubkey={_publicKey}&timestamp={_timestamp}");
        public override Coin Coins { get; } = Coin.Coal | Coin.Ore | Coin.Chromium;
        public override bool RequiresKeypair => false;

        public override Dictionary<string, string> Features { get; } = new Dictionary<string, string>();

        public override bool HideOnPoolList { get; } = false;
        public override string HostName { get; protected set; } = "pool.excalivator.xyz";

        public override Dictionary<Coin, double> MiniumumRewardPayout { get; } = new Dictionary<Coin, double> { { Coin.Coal, 5 }, { Coin.Ore, 0.05 }, { Coin.Chromium, 0.0 } };

        public override string Website => "https://mine.coal-pool.xyz/miner/balance-stats?key={0}";
        public override bool StakingEnabled => false;

        protected override async Task ClaimRewardsOptionAsync(Coin coin, CancellationToken token)
        {
            string message = String.Empty;


            if(!RequiresKeypair && _wallet == null)
            {
                _errorMessage = $"[red]A full keypair is required to initiate a claim. Can claim through the website [cyan]{String.Format(Website, _publicKey)}[/][/]";

                return;
            }

            await RefreshStakeBalancesAsync(true, token);

            AnsiConsole.Clear();

            //Check if there's enough
            bool enoughCoal = _minerInformation.TotalMiningRewards[Coin.Coal].CurrentBalance >= MiniumumRewardPayout[Coin.Coal];
            bool enoughOre = _minerInformation.TotalMiningRewards[Coin.Ore].CurrentBalance >= MiniumumRewardPayout[Coin.Ore];

            if(!enoughCoal && !enoughOre)
            {
                _errorMessage = $"[red]You must have a minimum of {MiniumumRewardPayout[Coin.Coal]} {Coin.Coal} or {MiniumumRewardPayout[Coin.Ore]} {Coin.Ore} to start a claim[/]";

                return;
            }

            double totalCoal = _minerInformation.TotalMiningRewards[Coin.Coal].CurrentBalance;
            double totalOre = _minerInformation.TotalMiningRewards[Coin.Ore].CurrentBalance;
            double totalChromium = _minerInformation.TotalMiningRewards[Coin.Chromium].CurrentBalance;

            double coalClaim = totalCoal > 0 ? await GetClaimAmount(Coin.Coal) : 0;
            double oreClaim = totalOre > 0 ? await GetClaimAmount(Coin.Ore) : 0;
            
            if(coalClaim < MiniumumRewardPayout[Coin.Coal] && oreClaim < MiniumumRewardPayout[Coin.Ore])
            {
                _errorMessage = $"[red]You must claim a minimum of {MiniumumRewardPayout[Coin.Coal]} {Coin.Coal} or {MiniumumRewardPayout[Coin.Ore]} {Coin.Ore}[/]";

                return;
            }

            double chromiumClaim = totalChromium > 0 ? await GetClaimAmount(Coin.Chromium) : 0;

            async Task<double> GetClaimAmount(Coin coin)
            {
                //Display claim prompt
                TextPrompt<double> claimPrompt = new TextPrompt<double>($"Claim [green]{coin}[/] amount (Min: {MiniumumRewardPayout[coin]}): ");
                claimPrompt.DefaultValue(_minerInformation.TotalMiningRewards[coin].CurrentBalance);
                claimPrompt.Validate((v) =>
                {
                    return v >= 0 && v <= _minerInformation.TotalMiningRewards[coin].CurrentBalance;
                });

                return await claimPrompt.ShowAsync(AnsiConsole.Console, token);
            }

            //Confirmation
            string claimWallet = _poolSettings.ClaimWallet ?? _wallet.Account.PublicKey;
            bool isSame = claimWallet == _wallet.Account.PublicKey;

            ConfirmationPrompt confirmationPrompt = new ConfirmationPrompt($"Claim {totalCoal} {Coin.Coal}, {totalOre} {Coin.Ore}, and {totalChromium} {Coin.Chromium} mining rewards to {claimWallet}" +
                $"{(!isSame ? $"\n[yellow]Warning: Claim wallet {claimWallet} is different from mining wallet {_wallet.Account.PublicKey}. Continue?[/]" : String.Empty)}?");
            confirmationPrompt.DefaultValue = false;

            if (await confirmationPrompt.ShowAsync(AnsiConsole.Console, token))
            {
                //Try claim
                (bool success, string message) claimResult = await ClaimMiningRewards(
                    (ulong)(totalCoal * CoalProgram.CoalDecimals), 
                    (ulong)(totalOre * OreProgram.OreDecimals), 
                    (ulong)(totalChromium * CoalProgram.CoalDecimals), 
                    claimWallet, token);

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

        protected async Task<(bool success, string message)> ClaimMiningRewards(ulong coalClaim, ulong oreClaim, ulong chromiumClaim, string claimWallet, CancellationToken token)
        {
            try
            {
                if (!await UpdateTimestampAsync(token))
                {
                    return (false, $"Failed to get timestamp from server");
                }

                Base58Encoder _encoder = new Base58Encoder();

                byte[] tBytes = new byte[8 + 32 + 8 + 8 + 8];
                BinaryPrimitives.WriteUInt64LittleEndian(tBytes, _timestamp);
                _encoder.DecodeData(claimWallet).CopyTo(tBytes, 8);

                BinaryPrimitives.WriteUInt64LittleEndian(tBytes.AsSpan().Slice(40, 8), coalClaim);
                BinaryPrimitives.WriteUInt64LittleEndian(tBytes.AsSpan().Slice(48, 8), oreClaim);
                BinaryPrimitives.WriteUInt64LittleEndian(tBytes.AsSpan().Slice(56, 8), chromiumClaim);

                byte[] sigBytes = _wallet.Sign(tBytes);
                string sig = _encoder.EncodeData(sigBytes);

                string authHeader = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_publicKey}:{sig}"))}";

                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/v2/claim?timestamp={_timestamp}&receiver_pubkey={claimWallet}&amount_coal={coalClaim}&amount_ore={oreClaim}&amount_chromium={chromiumClaim}");
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

        protected override async Task<(List<(Coin, double)> value, bool success)> GetDataAsync(string endpoint, CancellationToken token)
        {
            List<(Coin, double)> balanceData = new List<(Coin, double)>();

            try
            {
                using var response = await _client.GetAsync($"/miner/{endpoint}?pubkey={_publicKey}");

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return (balanceData, false);
                }

                string data = await response.Content.ReadAsStringAsync();

                var coinValues = JsonConvert.DeserializeObject<Dictionary<string, double>>(data);

                foreach(var kvp in coinValues)
                {
                    if(Enum.TryParse(kvp.Key, ignoreCase: true, out Coin result))
                    {
                        balanceData.Add((result, kvp.Value));
                    }

                }

                return (balanceData, true);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to request {endpoint} data from pool. Reason: {ex.Message}");

                return (balanceData, false);
            }
        }

        protected override async void HandleSubmissionResult(ArraySegment<byte> buffer)
        {
            CoalHQPoolSubmissionResponse submissionResponse = new CoalHQPoolSubmissionResponse();
            submissionResponse.Deserialize(buffer);

            //Not using it right now
            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            //This data takes ~20s to update properly, so everything is off by 1 update
            await RefreshStakeBalancesAsync(false, cts.Token, false);

            OnMinerUpdate?.Invoke(this, ([
                DateTime.Now.ToShortTimeString(),
                GenerateChallengeId(submissionResponse.Challenge).ToString(),
                $"{submissionResponse.CoalDetail.RewardDetails.MinerSuppliedDifficulty}/{submissionResponse.Difficulty}",
                $"[cyan]{submissionResponse.CoalDetail.RewardDetails.MinerEarnedRewards:0.00000000000}[/]\n[green]{submissionResponse.OreDetail.RewardDetails.MinerEarnedRewards:0.00000000000}[/]",
                //$"{_minerInformation.TotalStakeRewards.BalanceChangeSinceUpdate:0.00000000000}",
                $"[cyan]{submissionResponse.CoalDetail.RewardDetails.TotalRewards:0.00000000000}[/]\n[green]{submissionResponse.OreDetail.RewardDetails.TotalRewards:0.00000000000}[/]",
                $"[cyan]{_minerInformation.TotalMiningRewards[Coin.Coal]:0.00000000000}[/]\n[green]{_minerInformation.TotalMiningRewards[Coin.Ore]:0.00000000000}[/]",
                //$"{_minerInformation.TotalStakeRewards:0.00000000000}",
            ], buffer.ToArray()));
        }

        public override string[] TableHeaders()
        {
            return ["Time", "Id", "Diff", "Mining Rewards\n[cyan]Coal[/]/[green]Ore[/]", $"Pool Rewards", "Unclaimed Rewards"];
        }
    }
}
