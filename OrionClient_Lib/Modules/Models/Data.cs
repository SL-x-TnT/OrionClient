﻿using OrionClientLib;
using OrionClientLib.Hashers;
using OrionClientLib.Pools;
using OrionEventLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Models
{
    public class Data
    {
        public ReadOnlyCollection<IHasher> Hashers { get; }
        public ReadOnlyCollection<IPool> Pools { get; }
        public Settings Settings { get; }
        public bool AutoStarted { get; }
        public OrionEventHandler EventHandler { get; }

        public Data(IList<IHasher> hashers, IList<IPool> pools, Settings settings, OrionEventHandler eventHandler, bool autoStarted = false)
        {
            Hashers = hashers.AsReadOnly();
            Pools = pools.AsReadOnly();
            Settings = settings;
            AutoStarted = autoStarted;
            EventHandler = eventHandler;
        }


        public IPool GetChosenPool()
        {
            if (String.IsNullOrEmpty(Settings.Pool))
            {
                return null;
            }

            return Pools.FirstOrDefault(x => x.Name == Settings.Pool);
        }

        public (IHasher? cpu, IHasher? gpu) GetChosenHasher()
        {
            return (
                Hashers.FirstOrDefault(x => x.Name == Settings.CPUSetting.CPUHasher && x.HardwareType == IHasher.Hardware.CPU), 
                Hashers.FirstOrDefault(x => x.Name == Settings.GPUSetting.GPUHasher && x.HardwareType == IHasher.Hardware.GPU)
                );
        }

    }
}
