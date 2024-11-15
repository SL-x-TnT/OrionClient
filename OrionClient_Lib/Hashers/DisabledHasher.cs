﻿using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers
{
    public abstract class DisabledHasher : IHasher
    {
        public abstract IHasher.Hardware HardwareType { get; }

        public string Name => $"Disabled";
        public string Description => $"Disables {HardwareType} hasher";
        public bool Initialized => true;

        public TimeSpan CurrentChallengeTime { get; private set; } = TimeSpan.FromMinutes(5);


        public event EventHandler<HashrateInfo> OnHashrateUpdate;

        public bool Initialize(IPool pool, int threads)
        {
            return true;
        }

        public bool IsSupported()
        {
            return true;
        }

        public bool NewChallenge(int challengeId, Span<byte> challenge, ulong startNonce, ulong endNonce)
        {
            return true;
        }

        public void PauseMining()
        {

        }

        public void ResumeMining()
        {

        }

        public void SetThreads(int totalThreads)
        {

        }

        public async Task StopAsync()
        {

        }
    }

    public class DisabledCPUHasher : DisabledHasher
    {
        public override IHasher.Hardware HardwareType => IHasher.Hardware.CPU;
    }

    public class DisabledGPUHasher : DisabledHasher
    {
        public override IHasher.Hardware HardwareType => IHasher.Hardware.GPU;
    }
}
