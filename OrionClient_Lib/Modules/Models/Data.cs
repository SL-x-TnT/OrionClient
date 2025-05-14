using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using OrionClientLib.Hashers;
using OrionClientLib.Hashers.CPU;
using OrionClientLib.Hashers.GPU.AMDBaseline;
using OrionClientLib.Hashers.GPU.Baseline;
using OrionClientLib.Pools;
using OrionEventLib;
using System.Collections.ObjectModel;
using System.Runtime.Intrinsics.X86;

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
                Hashers.FirstOrDefault(x => x.Name == Settings.CPUSetting.CPUHasher && x.HardwareType == IHasher.Hardware.CPU) ?? Hashers.FirstOrDefault(x => x is DisabledCPUHasher),
                Hashers.FirstOrDefault(x => x.Name == Settings.GPUSetting.GPUHasher && x.HardwareType == IHasher.Hardware.GPU) ?? Hashers.FirstOrDefault(x => x is DisabledGPUHasher)
                );
        }

        public IHasher GetBestCPUHasher()
        {
            IHasher bestHasher = null;

            //if (Avx512DQ.IsSupported)
            //{
            //    bestHasher = Hashers.FirstOrDefault(x => x is AVX512CPUHasher);
            //}
            //else if (Avx2.IsSupported)
            //{
            //    bestHasher = Hashers.FirstOrDefault(x => x is PartialCPUHasherAVX2);
            //}

            //if (bestHasher == null)
            //{
            //    bestHasher = Hashers.FirstOrDefault(x => x is ManagedCPUHasher);
            //}

            return bestHasher ?? Hashers.FirstOrDefault(x => x is DisabledCPUHasher);
        }

        public (IHasher hasher, List<int> devices) GetGPUSettingInfo(bool forceAMD)
        {
            var gpuHasher = Hashers.FirstOrDefault(x => x is BaseGPUHasher) as BaseGPUHasher;

            if(gpuHasher == null)
            {
                return (null, new List<int>());
            }

            var allDevices = gpuHasher.GetDevices(false);
            List<int> gpuDevices = new List<int>();
            IHasher chosenHasher = null;

            if (!forceAMD && allDevices.Any(x => x is CudaDevice))
            {
                for (int i = 0; i < allDevices.Count; i++)
                {
                    if (allDevices[i] is CudaDevice cudaDevice)
                    {
                        gpuDevices.Add(i);
                    }
                }

                chosenHasher = Hashers.FirstOrDefault(x => x is CudaHarakaGPUHasher);
            }
            else if (forceAMD || allDevices.Any(x => x is CLDevice))
            {
                for (int i = 0; i < allDevices.Count; i++)
                {
                    if (allDevices[i] is CLDevice cudaDevice)
                    {
                        gpuDevices.Add(i);
                    }
                }

                chosenHasher = Hashers.FirstOrDefault(x => x is OpenCLOptEmulationGPUHasher);
            }

            return (chosenHasher, gpuDevices);
        }
    }
}

