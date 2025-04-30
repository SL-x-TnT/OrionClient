﻿using DrillX;
using DrillX.Compiler;
using DrillX.Solver;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using OrionClientLib.Hashers.GPU.Baseline;
using Solnet.Rpc.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers.GPU.AMDBaseline
{
    public partial class OpenCLOptEmulationGPUHasher : BaseGPUHasher
    {
        public override string Name => "OpenCL (Emulation)";
        public override string Description => "OpenCL hasher using an emulation kernel for hashx";
        public override bool Experimental => false;

        public override Action<ArrayView<Instruction>, ArrayView<SipState>, ArrayView<ulong>> HashxKernel()
        {
            _offsetCounter = 0;

            return Hashx;
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

            return devices.Where(x => x.AcceleratorType != AcceleratorType.CPU && x is CLDevice && x.MaxNumThreadsPerGroup >= 512).ToList();
        }

        public override KernelConfig GetHashXKernelConfig(Device device, int maxNonces, Settings settings)
        {
            int iterationCount = maxNonces * (ushort.MaxValue + 1);
            int groupSize = HashxBlockSize;

            var g = Math.Log2(groupSize);

            //Invalid setting
            if ((int)g != g)
            {
                groupSize = 128;
            }

            return new KernelConfig(
                new Index3D((iterationCount + groupSize - 1) / groupSize, 1, 1),
                new Index3D(groupSize, 1, 1)
                );
        }

        public override KernelConfig GetEquihashKernelConfig(Device device, int maxNonces, Settings settings)
        {
            int groupSize = BlockSize;
            int iterationCount = groupSize * maxNonces;

            return new KernelConfig(
                new Index3D((iterationCount + groupSize - 1) / groupSize, 1, 1),
                new Index3D(groupSize, 1, 1)
                );
        }

        public override (CudaCacheConfiguration, CudaCacheConfiguration) CudaCacheOption()
        {
            return (CudaCacheConfiguration.PreferShared, CudaCacheConfiguration.PreferShared);
        }
    }
}
