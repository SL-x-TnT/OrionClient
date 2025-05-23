using NLog;
using OrionClientLib.CoinPrograms.Ore;
using Solnet.Programs;
using Solnet.Programs.Abstract;
using Solnet.Programs.Utilities;
using Solnet.Rpc;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

// example of an Bitz Mining TX: https://eclipsescan.xyz/tx/5KuQytCrs6wkBjvCaCK8QwrtkBuQuzK5TTMc8jBns2WGkUKSJ74Jia8LLo4QzAg8fZyrpgV1toE1zXfFVEaCCTmd
// example of an ORE Mining TX: https://solscan.io/tx/2wbbfSjp4jtD3TMHFz21DkpWF5QjMsAUF4RqwcGyfPPkK2emkT8iadjjPjHJHCnVfNvKXRpLHmECdnvXTUuAZoWu

namespace OrionClientLib.CoinPrograms
{
    public class BitzProgram
    {
        private static readonly Logger _logger = LogManager.GetLogger("Main");
        private static readonly PublicKey SlotHashesKey = new("SysvarS1otHashes111111111111111111111111111"); // remains unchanged from ORE
        private static readonly PublicKey Instructions = new("Sysvar1nstructions1111111111111111111111111"); // remains unchanged from ORE

        public enum Errors
        {
            Unknown = -1,
            NeedsReset = 0,
            HashInvalid = 1,
            HashTooEasy = 2,
            ClaimTooLarge = 3,
            ClockInvalid = 4,
            Spam = 5,
            MaxSupply = 6,
            AuthFailed = 7,
            MiningDisabled = 8,
            BlockhashNotFound = 1000
        };

        private enum Instruction { Claim = 0, Close, Mine, Open, Reset, Stake, Update, Upgrade, Initialize = 100 };

        public static readonly PublicKey[] BusIds = new PublicKey[8];

        public static PublicKey ConfigAddress;
        public static PublicKey TreasuryId;
        public static PublicKey TreasuryATAId;
        public static PublicKey TreasuryBitzATAId;
        public static PublicKey MintId;

        public static PublicKey ProgramId = new PublicKey("EorefDWqzJK31vLxaqkDGsx3CRKqPVpWfuJL7qBQMZYd"); // Bitz Program ID
        public static readonly PublicKey NoopId = new PublicKey("F1ULBrY2Tjsmb1L4Wt4vX6UtiWRikLoRFWooSpxMM6nR"); // Bitz Noop ID
        public static readonly PublicKey BoostProgramId = new PublicKey("eBoFjsXMceooxywb8MeCqRCkQ2JEEsd5AUELXbaQfh8"); // Bitz Boost Program
        public static readonly PublicKey BoostAuthority = new PublicKey("bitzHk1APR3UaTAKE4hwx48VkQEHzoPMg7HEQWaukLx");

        // public static readonly PublicKey BoostCheckpointId = new PublicKey("6qWtSWTmWRgzmLMMpPAgzckKy73BkzXWqoZun6usqdCM"); // having a hard time finding this for ORE
        // could be AJZFPrGAMJkrYqZh9FDwmjPkXUQcdt72zLJJMJ8uAjoS or 49WndhATfh4KVdWgnCpwTzAgDzN4CcYPhBBYJu1HToJp or HWDgoDT8yevoq7g82pvZZGsbXSxT78D8kRirwq4AmduQ
        // https://eclipsescan.xyz/tx/21DcuphDPjDRzsTrkT7hA87FSGZwe19FcCDeaS3TXrgybSSeZrEGazF8HLLDHSVEF8DwF6rb4ENnobUSPbKbCt6c
        // https://eclipsescan.xyz/account/8BpjztAxesjvA9t4qUdEuPfzLBytyFLoeAuwHDg5WvAr

        public static PublicKey BoostConfig;

        // public static readonly List<BoostInformation> Boosts = new List<BoostInformation>()
        // {
        //     new BoostInformation("oreoU2P8bN6jkk3jbaiVxYnG1dCXcYxwhwyK9jSybcp", 11, "Ore", BoostInformation.PoolType.Ore, null, "ore"),
        // };

        public static readonly double BitzDecimals = Math.Pow(10, 11); //remains unchanged from ORE
        private static readonly byte[] MintNoise = new byte[] { 89, 157, 88, 232, 243, 249, 197, 132, 199, 49, 19, 234, 91, 94, 150, 41 };

        private static IRpcClient _rpcClient;
        private static IStreamingRpcClient _streamingClient;
        private static Settings.BitzRPCSettings _bitzRpcSettings;

        static BitzProgram()
        {
            Initialize(ProgramId);
        }

        public static void Initialize(PublicKey programId)
        {
            ProgramId = programId;

            //Generate busses
            for (int i = 0; i < BusIds.Length; i++)
            {
                PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("bus"), new byte[] { (byte)i } }, ProgramId, out var publicKey, out byte nonce);
                BusIds[i] = publicKey;
            }

            // PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("treasury") }, ProgramId, out var b, out var n);
            // TreasuryId = b;

            // PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("mint"), MintNoise }, ProgramId, out b, out n);
            // MintId = b;

            TreasuryId = new PublicKey("Feh8eCUQaHGfdPyGEARmVse3m4NBGgaVYwMiKE3CdcPz"); // Bitz Treasury Address
            MintId = new PublicKey("64mggk2nXg6vHC1qCdsZdEFzd5QGN4id54Vbho4PswCF"); // Bitz Token Mint Address

            PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("config") }, ProgramId, out var b, out var n);
            ConfigAddress = b;

            PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("config") }, BoostProgramId, out b, out n);
            BoostConfig = b;

            TreasuryATAId = new PublicKey("BHbThAP7qggM34iznUGwwC23jUtNaFRz5DrPxMGr56bS"); // Bitz Treasury ATA Address
        }

        public static void SetBitzRpcSettings(Settings.BitzRPCSettings settings)
        {
            _bitzRpcSettings = settings;
            SetRpcClient(settings.Url);
        }

        public static void SetRpcClient(string rpcUrl)
        {
            _rpcClient = ClientFactory.GetClient(rpcUrl);
            _streamingClient = ClientFactory.GetStreamingClient(rpcUrl.Replace("http", "ws"));
        }

        public static IRpcClient GetRpcClient()
        {
            if (_rpcClient == null)
            {
                string defaultUrl = _bitzRpcSettings?.Url ?? Settings.BitzRPCSettings.DefaultRPC;
                SetRpcClient(defaultUrl);
            }
            return _rpcClient;
        }

        public static IStreamingRpcClient GetStreamingRpcClient()
        {
            if (_streamingClient == null)
            {
                string defaultUrl = _bitzRpcSettings?.Url ?? Settings.BitzRPCSettings.DefaultRPC;
                SetRpcClient(defaultUrl);
            }
            return _streamingClient;
        }

        public static (PublicKey key, uint nonce) GetProofKey(PublicKey signer, PublicKey programId)
        {
            if (PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("proof"), signer.KeyBytes }, programId, out PublicKey address, out byte nonce))
            {
                return (address, nonce);
            }

            return default;
        }

        public static TransactionInstruction Register(PublicKey programId, PublicKey signer, PublicKey minerAuthority, PublicKey fundingWallet, PublicKey systemProgram, PublicKey slotHashes)
        {
            var proof = GetProofKey(signer, programId);

            List<AccountMeta> keys = new()
            {
                AccountMeta.Writable(signer, true),
                AccountMeta.ReadOnly(minerAuthority, false),
                AccountMeta.Writable(fundingWallet, true),
                AccountMeta.Writable(proof.key, false),
                AccountMeta.ReadOnly(systemProgram, false),
                AccountMeta.ReadOnly(slotHashes, false),
            };

            byte[] data = new byte[2];
            data[0] = (byte)Instruction.Open;
            data.WriteU8((byte)proof.nonce, 1);

            return new TransactionInstruction
            {
                ProgramId = programId,
                Keys = keys,
                Data = data
            };
        }

        public static TransactionInstruction Close(PublicKey programId, PublicKey signer, PublicKey systemProgram)
        {
            var proof = GetProofKey(signer, programId);

            List<AccountMeta> keys = new()
            {
                AccountMeta.Writable(signer, true),
                AccountMeta.Writable(proof.key, false),
                AccountMeta.ReadOnly(systemProgram, false),
            };

            byte[] data = new byte[1];
            data[0] = (byte)Instruction.Close;

            return new TransactionInstruction
            {
                ProgramId = programId,
                Keys = keys,
                Data = data
            };
        }

        public static TransactionInstruction Mine(PublicKey programId, PublicKey signer, PublicKey bus, PublicKey proof, byte[] solution, ulong nonce)
        {
            List<AccountMeta> keys = new()
            {
                AccountMeta.Writable(signer, true),
                AccountMeta.Writable(bus, false),
                AccountMeta.ReadOnly(ConfigAddress, false), // 44ewsha1UDV9DLwcZ6tHT9wFHmaHxJDD6SvYmhundtyv
                AccountMeta.Writable(proof, false),
                AccountMeta.ReadOnly(Instructions, false),
                AccountMeta.ReadOnly(SlotHashesKey, false),
                AccountMeta.ReadOnly(new PublicKey("5wpgyJFziVdB2RHW3qUx7teZxCax5FsniZxELdxiKUFD"), false),    // Account #7 - Static Bitz account
                AccountMeta.Writable(new PublicKey("3YiLgGTS23imzTfkTZhfTzNDtiz1mrrQoB4f3yyFUByE"), false),     // Account #8 - Static Bitz account (writable)
            };

            byte[] data = new byte[25];
            data[0] = (byte)Instruction.Mine;
            data.WriteSpan(solution, 1); //16 bytes
            data.WriteU64(nonce, 17); //8 bytes

            return new TransactionInstruction
            {
                ProgramId = programId,
                Keys = keys,
                Data = data
            };
        }

        public static TransactionInstruction Claim(PublicKey programId, PublicKey signer, PublicKey beneficiary, PublicKey proof, PublicKey teasury, PublicKey teasuryATA, PublicKey splTokenProgram, ulong claimAmount)
        {
            List<AccountMeta> keys = new()
            {
                AccountMeta.Writable(signer, true),
                AccountMeta.Writable(beneficiary, false),
                AccountMeta.Writable(proof, false),
                AccountMeta.ReadOnly(teasury, false),
                AccountMeta.Writable(teasuryATA, false),
                AccountMeta.ReadOnly(splTokenProgram, false),
            };

            byte[] data = new byte[9];
            data[0] = (byte)Instruction.Claim;
            data.WriteU64(claimAmount, 1);

            return new TransactionInstruction
            {
                ProgramId = programId,
                Keys = keys,
                Data = data
            };
        }

        public static TransactionInstruction Auth(PublicKey proof)
        {
            List<AccountMeta> keys = new();

            byte[] data = proof.KeyBytes;

            return new TransactionInstruction
            {
                ProgramId = NoopId,
                Keys = keys,
                Data = data
            };
        }

        public static PublicKey DeriveBoost(PublicKey mint)
        {
            if (PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("boost"), mint.KeyBytes }, BoostProgramId, out PublicKey address, out byte nonce))
            {
                return address;
            }

            return default;
        }

        public static PublicKey DeriveCheckpoint(PublicKey boost)
        {
            if (PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("checkpoint"), boost.KeyBytes }, BoostProgramId, out PublicKey address, out byte nonce))
            {
                return address;
            }

            return default;
        }

        public static PublicKey DeriveStakeAccount(PublicKey boost, PublicKey authority)
        {
            if (PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("stake"), authority.KeyBytes, boost.KeyBytes }, BoostProgramId, out PublicKey address, out byte nonce))
            {
                return address;
            }

            return default;
        }
    }
}