using DrillX;
using DrillX.Compiler;
using DrillX.Solver;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace OrionClientLib.Hashers.GPU.Baseline
{
    public partial class CudaHarakaGPUHasher : BaseGPUHasher
    {
        public override string Name => "Cuda Haraka";
        public override string Description => "Cuda haraka implementation";
        public override bool DisplaySetting => true;
        public override HashingAlgorithm Algorithm => HashingAlgorithm.Haraka;

        public override Action<ArrayView<uint>, ArrayView<SipState>, ArrayView<ulong>, ArrayView<ulong>> HarakaKernel()
        {
            return Haraka;
        }

        public override Action<ArrayView<ulong>, ArrayView<EquixSolution>, ArrayView<ushort>, ArrayView<uint>> EquihashKernel()
        {
            return Equihash;
        }

        protected override List<Device> GetValidDevices(IEnumerable<Device> devices)
        {
            if (devices == null)
            {
                return new List<Device>();
            }

            return devices.Where(x => x.AcceleratorType != AcceleratorType.CPU && x.MaxNumThreadsPerGroup >= 512 && x is CudaDevice).ToList();
        }

        public override KernelConfig GetHashXKernelConfig(Device device, int maxNonces, Settings settings)
        {
            int iterationCount = maxNonces * (ushort.MaxValue + 1);
            int groupSize = HashxBlockSize;

            var g = Math.Log2(groupSize);

            //Invalid setting
            if ((int)g != g)
            {
                groupSize = 1024;
            }

            return new KernelConfig(
                new Index3D((iterationCount + groupSize - 1) / groupSize, 1, 1),
                new Index3D(groupSize, 1, 1)
                );
        }

        public override KernelConfig GetEquihashKernelConfig(Device device, int maxNonces, Settings settings)
        {
            int groupSize = 512;
            int iterationCount = groupSize * maxNonces;

            return new KernelConfig(
                new Index3D((iterationCount + groupSize - 1) / groupSize, 1, 1),
                new Index3D(groupSize, 1, 1)
                );
        }

        public override (CudaCacheConfiguration, CudaCacheConfiguration) CudaCacheOption()
        {
            return (CudaCacheConfiguration.PreferL1, CudaCacheConfiguration.PreferShared);
        }
    }
}
