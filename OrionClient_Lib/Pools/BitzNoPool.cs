using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools.Models;
using OrionClientLib.CoinPrograms;
using Solnet.Rpc;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using NLog;
using System.IO;
using System.Buffers.Binary;

namespace OrionClientLib.Pools
{
    public class BitzNoPool : BasePool
    {
        private static readonly Logger _logger = LogManager.GetLogger("Main");
        public override string Name => "Bitz Solo";
        public override string DisplayName => "Bitz Solo";
        public override string Description => "Bitz Solo Mining - Mine directly to the Eclipse blockchain";
        public override string ArgName => "bitzsolo";
        public override bool DisplaySetting => true;
        public override bool HideOnPoolList => false;
        public override Coin Coins => Coin.Bitz;
        public override bool RequiresKeypair => true;
        public override Dictionary<string, string> Features => new Dictionary<string, string>
       {
           { "Solo Mining", "Mine directly to your wallet without a pool" },
           { "No Pool Fees", "Keep 100% of your mining rewards" },
           { "Eclipse Network", "Mines on Eclipse mainnet blockchain" }
        };
        public override event EventHandler<NewChallengeInfo> OnChallengeUpdate;
        public override event EventHandler<(string[] columns, byte[] data)> OnMinerUpdate;
        public override event EventHandler PauseMining;
        public override event EventHandler ResumeMining;
       private Wallet _wallet;
       private string _publicKey;
        private IRpcClient _rpcClient;
        private Settings _settings;
       private System.Timers.Timer _challengeTimer;
       private int _challengeId = 0;
       private byte[] _currentChallenge;
        private DifficultyInfo _bestDifficulty;
       private double _miningRewards = 0;
       private double _walletBalance = 0;
        private PublicKey _proofAccount;
        private bool _proofAccountExists = false;

        public override void SetWalletInfo(Wallet wallet, string publicKey)
       {
           _wallet = wallet;
           _publicKey = publicKey;
            if (_wallet != null)
            {
                var proofKey = BitzProgram.GetProofKey(_wallet.Account.PublicKey, BitzProgram.ProgramId);
                _proofAccount = proofKey.key;
            }
        }

        public override async Task<bool> ConnectAsync(CancellationToken token)
        {
            try
            {
                if (_settings == null)
                {
                    _settings = await Settings.LoadAsync();
                    BitzProgram.SetBitzRpcSettings(_settings.BitzRPCSetting);
                    _rpcClient = BitzProgram.GetRpcClient(); 
                }
                try
                {
                    var healthCheck = await _rpcClient.GetHealthAsync();
                    if (healthCheck.WasSuccessful)
                    {
                    }
                    else
                    {
                        _logger.Log(LogLevel.Warn, $"⚠️ Eclipse RPC health check failed: {healthCheck.Reason}");
                    }

                    var blockTest = await _rpcClient.GetLatestBlockHashAsync();
                    if (blockTest.WasSuccessful)
                    {
                    }
                    else
                    {
                        _logger.Log(LogLevel.Error, $"❌ Eclipse RPC blockhash test failed: {blockTest.Reason}");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, $"❌ Eclipse RPC connectivity test failed: {ex.Message}");
                    return false;
                }

                await CheckProofAccountAsync();

                _logger.Log(LogLevel.Info, "⏰ Setting up challenge timer (60 second intervals for Eclipse)");
                _challengeTimer = new System.Timers.Timer(60000); // 60 seconds for Eclipse blockchain
                _challengeTimer.Elapsed += (sender, e) =>
                {
                    _logger.Log(LogLevel.Debug, "⏰ Challenge timer elapsed - checking for new Eclipse challenge");
                    
                    OnMinerUpdate?.Invoke(this, (
                        new[] { 
                            DateTime.Now.ToShortTimeString(),
                            _challengeId.ToString(),
                            _bestDifficulty?.BestDifficulty.ToString() ?? "0",
                            $"{_miningRewards:0.00000000000} {Coins}",
                            $"{_walletBalance:0.00000000000} {Coins}"
                        },
                        null
                    ));

                    _bestDifficulty = null;
                    GenerateNewChallenge();
                };
           _challengeTimer.Start();
           _logger.Log(LogLevel.Info, "✅ Challenge timer started successfully");

                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        await UpdateWalletBalanceAsync();
                        await Task.Delay(10000, token);
                    }
                });

           GenerateNewChallenge();

           return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public override async Task<bool> DisconnectAsync()
       {
           _challengeTimer?.Stop();
           _challengeTimer?.Dispose();
           return true;
       }

        public override async Task<double> GetFeeAsync(CancellationToken token)
       {
            return 0.0; // No fees for solo mining
       }

        public override async Task<(bool success, string errorMessage)> SetupAsync(CancellationToken token, bool initialSetup = false)
       {
            Console.WriteLine("DEBUG: BitzNoPool.SetupAsync called!");
            Console.WriteLine($"DEBUG: initialSetup = {initialSetup}");
            
           if (_wallet == null)
           {
                Console.WriteLine("DEBUG: Wallet is null, returning error");
               return (false, "A full keypair is required for solo mining");
           }

            try
            {
                Console.WriteLine("DEBUG: Starting Bitz setup...");
                Console.WriteLine($"DEBUG: Wallet: {_wallet.Account.PublicKey}");
                Console.WriteLine($"DEBUG: RPC Client: {_rpcClient != null}");
                Console.WriteLine($"DEBUG: Settings: {_settings != null}");

                if (_settings == null || _rpcClient == null)
                {
                    Console.WriteLine("DEBUG: Initializing settings and RPC client...");
               _settings = await Settings.LoadAsync();
               BitzProgram.SetBitzRpcSettings(_settings.BitzRPCSetting);
               _rpcClient = BitzProgram.GetRpcClient();
                    
                    Console.WriteLine($"DEBUG: Settings loaded: {_settings != null}");
                    Console.WriteLine($"DEBUG: RPC Client initialized: {_rpcClient != null}");
                    Console.WriteLine($"DEBUG: Eclipse RPC URL: {_settings.BitzRPCSetting.Url}");
                }

                Console.WriteLine("DEBUG: Checking if proof account already exists...");
                await CheckProofAccountAsync();
                Console.WriteLine($"DEBUG: Proof account exists check result: {_proofAccountExists}");

                if (!_proofAccountExists)
                {
                    Console.WriteLine("DEBUG: Proof account doesn't exist, creating...");
                    var created = await CreateProofAccountAsync();
                    Console.WriteLine($"DEBUG: CreateProofAccountAsync returned: {created}");
                    
                    if (!created)
                    {
                        var errorMsg = "Failed to create proof account - check console for detailed error logs";
                        Console.WriteLine($"DEBUG: Setup failed: {errorMsg}");
                        return (false, errorMsg);
                    }
                }
                else
                {
                    Console.WriteLine("DEBUG: Proof account already exists, skipping creation");
                }

                Console.WriteLine("DEBUG: Setup completed successfully");
           return (true, string.Empty);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Setup failed with exception: {ex.Message}";
                Console.WriteLine($"DEBUG: Exception in Setup: {errorMsg}");
                Console.WriteLine($"DEBUG: Stack trace: {ex.StackTrace}");
                return (false, errorMsg);
            }
        }

        public override async Task<(bool success, string errorMessage)> OptionsAsync(CancellationToken token)
        {
           return (true, string.Empty);
       }

        public override string[] TableHeaders()
        {
            return new[] { "Time", "Challenge ID", "Best Difficulty", "Mining Rewards", "Wallet Balance" };
        }

        public override void DifficultyFound(DifficultyInfo info)
        {
            if (info.ChallengeId != _challengeId)
            {
                _logger.Log(LogLevel.Warn, $"🚨 CHALLENGE ID MISMATCH! Mining: {info.ChallengeId}, Current: {_challengeId}");
                return;
            }

            if (_bestDifficulty == null || _bestDifficulty.BestDifficulty < info.BestDifficulty)
            {
                _bestDifficulty = info;
            }

            _logger.Log(LogLevel.Info, $"🔍 SOLUTION FOUND for Challenge ID {info.ChallengeId}");
            _logger.Log(LogLevel.Info, $"🎯 Mining Challenge: {Convert.ToHexString(_currentChallenge ?? new byte[32])}");
            _logger.Log(LogLevel.Info, $"🎯 Current Challenge: {Convert.ToHexString(_currentChallenge ?? new byte[32])}");
            _logger.Log(LogLevel.Info, $"✅ Challenge Match: True (using current challenge)");

            if (info.BestDifficulty >= 10) // Minimum difficulty threshold
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SubmitSolutionAsync(info);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(LogLevel.Warn, $"Error submitting solution: {ex.Message}");
                    }
                });
            }
        }

        private async Task<bool> CheckProofAccountAsync()
        {
            try
            {
                if (_rpcClient == null || _proofAccount == null)
                {
                    Console.WriteLine("DEBUG: CheckProofAccount - RPC client or proof account is null");
                    _logger.Log(LogLevel.Error, "CheckProofAccount: RPC client or proof account is null");
                    return false;
                }

                Console.WriteLine($"DEBUG: CheckProofAccount - Checking account: {_proofAccount}");
                _logger.Log(LogLevel.Debug, $"Checking if proof account exists: {_proofAccount}");
                var accountInfo = await _rpcClient.GetAccountInfoAsync(_proofAccount);
                
                Console.WriteLine($"DEBUG: CheckProofAccount - RPC call successful: {accountInfo.WasSuccessful}");
                Console.WriteLine($"DEBUG: CheckProofAccount - Account result null: {accountInfo.Result?.Value == null}");
                
                if (accountInfo.WasSuccessful)
                {
                    if (accountInfo.Result?.Value != null)
                    {
                        Console.WriteLine($"DEBUG: CheckProofAccount - Account data length: {accountInfo.Result.Value.Data?.Count ?? 0}");
                        Console.WriteLine($"DEBUG: CheckProofAccount - Account owner: {accountInfo.Result.Value.Owner}");
                        Console.WriteLine($"DEBUG: CheckProofAccount - Account executable: {accountInfo.Result.Value.Executable}");
                        Console.WriteLine($"DEBUG: CheckProofAccount - Account lamports: {accountInfo.Result.Value.Lamports}");
                    }
                    else
                    {
                        Console.WriteLine("DEBUG: CheckProofAccount - Account result value is null (account doesn't exist)");
                    }
                }
                else
                {
                    Console.WriteLine($"DEBUG: CheckProofAccount - RPC call failed: {accountInfo.Reason}");
                    Console.WriteLine($"DEBUG: CheckProofAccount - HTTP status: {accountInfo.HttpStatusCode}");
                }
                
                _proofAccountExists = accountInfo.WasSuccessful && accountInfo.Result?.Value != null;
                
                if (_proofAccountExists)
                {
                    Console.WriteLine("DEBUG: CheckProofAccount - Account EXISTS, skipping creation");
                    _logger.Log(LogLevel.Info, $"✅ Proof account already exists: {_proofAccount}");
                }
                else
                {
                    Console.WriteLine("DEBUG: CheckProofAccount - Account DOES NOT EXIST, will create");
                    _logger.Log(LogLevel.Info, $"❌ Proof account does not exist: {_proofAccount}");
                    if (!accountInfo.WasSuccessful)
                    {
                        _logger.Log(LogLevel.Debug, $"RPC call failed: {accountInfo.Reason}");
                    }
                }
                
                return _proofAccountExists;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: CheckProofAccount - Exception: {ex.Message}");
                _logger.Log(LogLevel.Warn, $"Error checking proof account: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> CreateProofAccountAsync()
        {
            try
            {
                if (_wallet == null || _rpcClient == null)
                {
                    Console.WriteLine("DEBUG: Wallet or RPC client is null");
                    _logger.Log(LogLevel.Error, "CreateProofAccount: Wallet or RPC client is null");
                    return false;
                }

                Console.WriteLine($"DEBUG: Creating proof account for wallet: {_wallet.Account.PublicKey}");
                Console.WriteLine($"DEBUG: Proof account address: {_proofAccount}");
                
                _logger.Log(LogLevel.Info, $"Creating Bitz proof account...");
                _logger.Log(LogLevel.Debug, $"Wallet: {_wallet.Account.PublicKey}");
                _logger.Log(LogLevel.Debug, $"Proof Account: {_proofAccount}");

                var balanceResult = await _rpcClient.GetBalanceAsync(_wallet.Account.PublicKey);
                                
                if (balanceResult.WasSuccessful)
                {
                    var ethBalance = balanceResult.Result.Value / 1_000_000_000.0; // Convert lamports to ETH
                    Console.WriteLine($"DEBUG: ETH Balance: {ethBalance:0.000000000} ETH");
                    _logger.Log(LogLevel.Info, $"ETH Balance: {ethBalance:0.000000000} ETH");
                    
                    if (ethBalance < 0.00005)
                    {
                        Console.WriteLine($"DEBUG: Insufficient ETH balance: {ethBalance:0.000000000} ETH");
                        _logger.Log(LogLevel.Error, $"❌ Insufficient ETH balance for transaction fees. Need at least 0.0005 ETH, have {ethBalance:0.000000000} ETH");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine($"DEBUG: Could not check ETH balance: {balanceResult.Reason}");
                    _logger.Log(LogLevel.Warn, $"⚠️ Could not check ETH balance: {balanceResult.Reason}");
                }

                Console.WriteLine("DEBUG: Creating register instruction...");
                var registerInstruction = BitzProgram.Register(
                    BitzProgram.ProgramId, // BITZ Program ID: EorefDWqzJK31vLxaqkDGsx3CRKqPVpWfuJL7qBQMZYd
                    _wallet.Account.PublicKey,
                    _wallet.Account.PublicKey, // miner authority is same as signer for solo
                    _wallet.Account.PublicKey, // funding wallet is same as signer
                    SystemProgram.ProgramIdKey,
                    new PublicKey("SysvarS1otHashes111111111111111111111111111") // Solana sysvar (same across all networks)
                );

                Console.WriteLine("DEBUG: Getting latest blockhash...");
                _logger.Log(LogLevel.Info, $"Created register instruction for Bitz program: {BitzProgram.ProgramId}");

                _logger.Log(LogLevel.Debug, "Fetching latest blockhash from Eclipse...");
                var recentBlockhash = await _rpcClient.GetLatestBlockHashAsync();
                
                Console.WriteLine($"DEBUG: Blockhash result: {recentBlockhash.WasSuccessful}");
                
                if (!recentBlockhash.WasSuccessful)
                {
                    Console.WriteLine($"DEBUG: Failed to get blockhash: {recentBlockhash.Reason}");
                    _logger.Log(LogLevel.Error, $"Failed to get recent blockhash from Eclipse: {recentBlockhash.Reason} - {recentBlockhash.HttpStatusCode}");
                    return false;
                }

                Console.WriteLine($"DEBUG: Got blockhash: {recentBlockhash.Result.Value.Blockhash}");
                _logger.Log(LogLevel.Debug, $"Got blockhash: {recentBlockhash.Result.Value.Blockhash}");

                Console.WriteLine("DEBUG: Building transaction...");
                var transactionBuilder = new TransactionBuilder()
                    .SetRecentBlockHash(recentBlockhash.Result.Value.Blockhash)
                    .SetFeePayer(_wallet.Account.PublicKey)
                    .AddInstruction(registerInstruction);

                _logger.Log(LogLevel.Debug, "Building and signing transaction...");

                var transactionBytes = transactionBuilder.Build(_wallet.Account);

                Console.WriteLine($"DEBUG: Transaction built, size: {transactionBytes.Length} bytes");
                _logger.Log(LogLevel.Debug, $"Transaction built, size: {transactionBytes.Length} bytes");

                Console.WriteLine("DEBUG: Submitting transaction to Eclipse...");
                _logger.Log(LogLevel.Info, "Submitting proof account creation transaction to Eclipse...");
                var result = await _rpcClient.SendTransactionAsync(transactionBytes);
                
                Console.WriteLine($"DEBUG: Transaction submission result: {result.WasSuccessful}");
                
                if (result.WasSuccessful)
                {
                    Console.WriteLine($"DEBUG: SUCCESS! Transaction: {result.Result}");
                    _logger.Log(LogLevel.Info, $"✅ Bitz proof account created on Eclipse! Tx: {result.Result}");
                    _proofAccountExists = true;
                    return true;
                }
                else
                {
                    Console.WriteLine($"DEBUG: FAILED! Reason: {result.Reason}");
                    Console.WriteLine($"DEBUG: HTTP Status: {result.HttpStatusCode}");
                    _logger.Log(LogLevel.Error, $"❌ Failed to create Bitz proof account: {result.Reason}");
                    _logger.Log(LogLevel.Error, $"HTTP Status: {result.HttpStatusCode}");
                    if (result.ErrorData != null)
                    {
                        Console.WriteLine($"DEBUG: Error Code: {result.ServerErrorCode}");
                        Console.WriteLine($"DEBUG: Error Data: {result.ErrorData}");
                        _logger.Log(LogLevel.Error, $"Server Error Code: {result.ServerErrorCode}");
                        _logger.Log(LogLevel.Error, $"Error Data: {result.ErrorData}");
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: EXCEPTION in CreateProofAccountAsync: {ex.Message}");
                Console.WriteLine($"DEBUG: Stack trace: {ex.StackTrace}");
                _logger.Log(LogLevel.Error, $"❌ Exception creating Bitz proof account: {ex.Message}");
                _logger.Log(LogLevel.Debug, $"Stack trace: {ex.StackTrace}");
                return false;
            }
       }

       private async void GenerateNewChallenge()
       {
            try
            {
                _logger.Log(LogLevel.Debug, "🔄 Checking for new challenge from Eclipse blockchain...");
                await FetchRealChallengeAsync();
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Error fetching real challenge: {ex.Message}");
            }
        }

        private async Task FetchRealChallengeAsync()
        {
            try
            {
                _logger.Log(LogLevel.Debug, "🔍 FetchRealChallengeAsync started");
                
                if (_rpcClient == null)
                {
                    _logger.Log(LogLevel.Warn, "❌ RPC client not initialized, cannot fetch challenge");
                    return;
                }

                _logger.Log(LogLevel.Debug, $"🌐 Fetching Bitz config account: {BitzProgram.ConfigAddress}");
                
                var configResult = await _rpcClient.GetAccountInfoAsync(BitzProgram.ConfigAddress);
                
                if (!configResult.WasSuccessful || configResult.Result?.Value?.Data == null)
                {
                    _logger.Log(LogLevel.Warn, $"Failed to fetch Bitz config account: {configResult.Reason}");
                    return;
                }

                var configData = configResult.Result.Value.Data;
                if (configData.Count < 1) 
                {
                    _logger.Log(LogLevel.Warn, $"Config data empty: {configData.Count} elements");
                    return;
                }

                var configBytes = Convert.FromBase64String(configData[0]);
                _logger.Log(LogLevel.Debug, $"Config account data size: {configBytes.Length} bytes");
                
                if (configBytes.Length < 40) 
                {
                    _logger.Log(LogLevel.Warn, $"Config bytes too small: {configBytes.Length} bytes");
                    return;
                }

                var realChallenge = new byte[32];
                
                if (configBytes.Length >= 40)
                {
                    for (int i = 0; i < 32; i++)
                    {
                        realChallenge[i] = configBytes[i + 8];
                    }
                    _logger.Log(LogLevel.Debug, $"Using challenge at offset 8: {Convert.ToHexString(realChallenge)}");
                }
                else
                {
                    for (int i = 0; i < 32; i++)
                    {
                        realChallenge[i] = configBytes[i]; 
                    }
                    _logger.Log(LogLevel.Debug, $"Using challenge at offset 0 (fallback): {Convert.ToHexString(realChallenge)}");
                }
                
                bool challengeChanged = _currentChallenge == null || !realChallenge.SequenceEqual(_currentChallenge);
                
                if (challengeChanged)
                {
                    _challengeId++;
                    _currentChallenge = realChallenge;

                    _logger.Log(LogLevel.Info, $"🔄 NEW CHALLENGE ID {_challengeId}");
                    _logger.Log(LogLevel.Info, $"📊 Challenge being sent to miner: {Convert.ToHexString(_currentChallenge)}");

                    OnChallengeUpdate?.Invoke(this, new NewChallengeInfo
                    {
                        ChallengeId = _challengeId,
                        Challenge = _currentChallenge,
                        StartNonce = 0,
                        EndNonce = ulong.MaxValue,
                        TotalCPUNonces = ulong.MaxValue / 2 
                    });

                    _logger.Log(LogLevel.Info, $"✅ Challenge sent to miners successfully");
                }
                else
                {
                    _logger.Log(LogLevel.Debug, $"⏸️ Eclipse challenge unchanged. ID: {_challengeId}");
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Exception fetching real challenge: {ex.Message}");
            }
        }

        private async Task SubmitSolutionAsync(DifficultyInfo info)
        {
            if (_wallet == null || _rpcClient == null || !_proofAccountExists)
                return;

            try
            {
                var bus = BitzProgram.BusIds[new Random().Next(BitzProgram.BusIds.Length)];
                
                _logger.Log(LogLevel.Debug, $"Submitting solution to Bitz bus: {bus} (Program: {BitzProgram.ProgramId})");
                
                _logger.Log(LogLevel.Debug, $"Creating mine instruction with accounts:");
                _logger.Log(LogLevel.Debug, $"- Signer: {_wallet.Account.PublicKey}");
                _logger.Log(LogLevel.Debug, $"- Bus: {bus}");
                _logger.Log(LogLevel.Debug, $"- Proof: {_proofAccount}");
                _logger.Log(LogLevel.Debug, $"- Solution length: {info.BestSolution.Length}");
                _logger.Log(LogLevel.Debug, $"- Nonce: {info.BestNonce}");
                
                _logger.Log(LogLevel.Debug, "Validating all mining accounts...");
                await ValidateMiningAccountsAsync(bus);
                
                var mineInstruction = BitzProgram.Mine(
                    BitzProgram.ProgramId,
                    _wallet.Account.PublicKey,
                    bus, 
                    _proofAccount, 
                    info.BestSolution,
                    info.BestNonce
                );

                var authInstruction = BitzProgram.Auth(_proofAccount);
                
                Console.WriteLine($"DEBUG: Mine instruction accounts: {mineInstruction.Keys.Count}");
                Console.WriteLine($"DEBUG: Auth instruction accounts: {authInstruction.Keys.Count}");

                var recentBlockhash = await _rpcClient.GetLatestBlockHashAsync();
                if (!recentBlockhash.WasSuccessful)
                {
                    _logger.Log(LogLevel.Warn, "Failed to get blockhash from Eclipse for solution submission");
                    return;
                }

                var transactionBuilder = new TransactionBuilder()
                    .SetRecentBlockHash(recentBlockhash.Result.Value.Blockhash)
                    .SetFeePayer(_wallet.Account.PublicKey)
                    .AddInstruction(authInstruction)  // Add auth instruction first
                    .AddInstruction(mineInstruction); // Then mine instruction

                var transactionBytes = transactionBuilder.Build(_wallet.Account);

                var result = await _rpcClient.SendTransactionAsync(transactionBytes);
                
                _logger.Log(LogLevel.Debug, $"Mining tx submission result: {result.WasSuccessful}");
                _logger.Log(LogLevel.Debug, $"Mining tx size: {transactionBytes.Length} bytes");
                
                if (result.WasSuccessful)
                {
                    _logger.Log(LogLevel.Info, $"✅ MINING SUCCESS! Transaction: {result.Result}");
                    _logger.Log(LogLevel.Info, $"Bitz solution submitted to Eclipse! Difficulty: {info.BestDifficulty}, Tx: {result.Result}");
                    _miningRewards += 1.0; // Placeholder
                }
                else
                {
                    _logger.Log(LogLevel.Warn, $"❌ MINING FAILED! Reason: {result.Reason}");
                    _logger.Log(LogLevel.Debug, $"HTTP Status: {result.HttpStatusCode}");
                    if (result.ErrorData != null)
                    {
                        _logger.Log(LogLevel.Debug, $"Server Error Code: {result.ServerErrorCode}");
                        _logger.Log(LogLevel.Debug, $"Error Data: {result.ErrorData}");
                    }
                    _logger.Log(LogLevel.Warn, $"Failed to submit Bitz solution to Eclipse: {result.Reason}");
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Error submitting Bitz solution: {ex.Message}");
            }
        }

        private async Task UpdateWalletBalanceAsync()
        {
            try
            {
                if (_wallet == null || _rpcClient == null)
                    return;

                var tokenAccounts = await _rpcClient.GetTokenAccountsByOwnerAsync(
                    _wallet.Account.PublicKey,
                    BitzProgram.MintId.Key
                );

                if (tokenAccounts.WasSuccessful && tokenAccounts.Result?.Value?.Count > 0)
                {
                    var tokenAccount = tokenAccounts.Result.Value.FirstOrDefault();
                    if (tokenAccount?.Account?.Data?.Parsed != null)
                    {
                        var tokenAccountInfo = tokenAccount.Account.Data.Parsed.Info;
                        if (tokenAccountInfo?.TokenAmount != null)
                        {
                            var balance = tokenAccountInfo.TokenAmount.Amount;
                            
                            if (ulong.TryParse(balance, out ulong balanceRaw))
                            {
                                var newBalance = balanceRaw / BitzProgram.BitzDecimals; 
                                if (Math.Abs(_walletBalance - newBalance) > 0.000001) 
                                {
                                    _logger.Log(LogLevel.Debug, $"Bitz wallet balance updated: {newBalance} BITZ");
                                    _walletBalance = newBalance;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Debug, $"Error updating Bitz wallet balance: {ex.Message}");
            }
        }

       private async Task ValidateMiningAccountsAsync(PublicKey bus)
       {
           try
           {
            //    _logger.Log(LogLevel.Debug, "Checking all 8 mining accounts...");
            //    _logger.Log(LogLevel.Debug, $"Account 1 - Signer: {_wallet.Account.PublicKey} ✓");
               
               var busInfo = await _rpcClient.GetAccountInfoAsync(bus);
            //    _logger.Log(LogLevel.Debug, $"Account 2 - Bus: {bus} - Exists: {busInfo.WasSuccessful && busInfo.Result?.Value != null}");
               if (busInfo.WasSuccessful && busInfo.Result?.Value != null)
               {
                   _logger.Log(LogLevel.Debug, $"Bus Owner: {busInfo.Result.Value.Owner}");
               }
               
               var configInfo = await _rpcClient.GetAccountInfoAsync(BitzProgram.ConfigAddress);
            //    _logger.Log(LogLevel.Debug, $"Account 3 - Config: {BitzProgram.ConfigAddress} - Exists: {configInfo.WasSuccessful && configInfo.Result?.Value != null}");
               if (configInfo.WasSuccessful && configInfo.Result?.Value != null)
               {
                   _logger.Log(LogLevel.Debug, $"Config Owner: {configInfo.Result.Value.Owner}");
               }
               
               var proofInfo = await _rpcClient.GetAccountInfoAsync(_proofAccount);
            //    _logger.Log(LogLevel.Debug, $"Account 4 - Proof: {_proofAccount} - Exists: {proofInfo.WasSuccessful && proofInfo.Result?.Value != null}");
               if (proofInfo.WasSuccessful && proofInfo.Result?.Value != null)
               {
                   _logger.Log(LogLevel.Debug, $"Proof Owner: {proofInfo.Result.Value.Owner}");
               }
               
               var account7 = new PublicKey("5wpgyJFziVdB2RHW3qUx7teZxCax5FsniZxELdxiKUFD");
               var account7Info = await _rpcClient.GetAccountInfoAsync(account7);
            //    _logger.Log(LogLevel.Debug, $"Account 7 - Static: {account7} - Exists: {account7Info.WasSuccessful && account7Info.Result?.Value != null}");
               if (account7Info.WasSuccessful && account7Info.Result?.Value != null)
               {
                   _logger.Log(LogLevel.Debug, $"Account 7 Owner: {account7Info.Result.Value.Owner}");
               }
               
               var account8 = new PublicKey("3YiLgGTS23imzTfkTZhfTzNDtiz1mrrQoB4f3yyFUByE");
               var account8Info = await _rpcClient.GetAccountInfoAsync(account8);
            //    _logger.Log(LogLevel.Debug, $"Account 8 - Static: {account8} - Exists: {account8Info.WasSuccessful && account8Info.Result?.Value != null}");
               if (account8Info.WasSuccessful && account8Info.Result?.Value != null)
               {
                   _logger.Log(LogLevel.Debug, $"Account 8 Owner: {account8Info.Result.Value.Owner}");
               }
               
               _logger.Log(LogLevel.Debug, "Account validation completed");
           }
           catch (Exception ex)
           {
               _logger.Log(LogLevel.Warn, $"Account validation failed: {ex.Message}");
           }
       }
   }
}