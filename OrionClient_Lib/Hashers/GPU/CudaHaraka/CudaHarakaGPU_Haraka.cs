using DrillX;
using DrillX.Compiler;
using ILGPU;
using ILGPU.Backends;
using ILGPU.Backends.PTX;
using ILGPU.IR;
using ILGPU.IR.Intrinsics;
using System.Runtime.CompilerServices;

namespace OrionClientLib.Hashers.GPU.Baseline
{
    public partial class CudaHarakaGPUHasher
    {
        private static int _offsetCounter = 0;
        public const int HashxBlockSize = 1024;

        private static void Haraka(ArrayView<uint> lookupTable, ArrayView<SipState> key, ArrayView<ulong> results, ArrayView<ulong> roundConstants)
        {
            var grid = Grid.GlobalIndex;
            var group = Group.Dimension;

            int index = (grid.X * group.Y + grid.Y);

            var idx = Group.IdxX;

            const int lookupTableSize = 256 * 32;

            var sharedProgram = SharedMemory.Allocate<uint>(lookupTableSize);
            var sharedConstants = SharedMemory.Allocate<uint>(192);

            SipState nonceKey = LoadSipKey(ref key.SubView(index / (ushort.MaxValue + 1)).Cast<ulong>()[0]);

            //Need 8
            var tTable = lookupTable.Cast<ulong>();
            var tShared = sharedProgram.Cast<ulong>();

            var s_0 = tTable[idx];
            var s_1 = tTable[idx + HashxBlockSize];
            var s_2 = tTable[idx + HashxBlockSize * 2];
            var s_3 = tTable[idx + HashxBlockSize * 3];
            //var s_4 = tTable[idx + HashxBlockSize * 4];
            //var s_5 = tTable[idx + HashxBlockSize * 5];
            //var s_6 = tTable[idx + HashxBlockSize * 6];
            //var s_7 = tTable[idx + HashxBlockSize * 7];

            tShared[idx] = s_0;
            tShared[idx + HashxBlockSize] = s_1;
            tShared[idx + HashxBlockSize * 2] = s_2;
            tShared[idx + HashxBlockSize * 3] = s_3;
            //tShared[idx + HashxBlockSize * 4] = s_4;
            //tShared[idx + HashxBlockSize * 5] = s_5;
            //tShared[idx + HashxBlockSize * 6] = s_6;
            //tShared[idx + HashxBlockSize * 7] = s_7;


            for (int i = idx; i < 192; i += HashxBlockSize)
            {
                sharedConstants[i] = roundConstants.Cast<uint>()[i];
            }


            Group.Barrier();

            //SipState nonceKey = key.SubView(index / (ushort.MaxValue + 1))[0];
            ulong nonce = (ulong)(index % (ushort.MaxValue + 1));

            results[index] = Haraka(nonceKey, nonce, sharedConstants.Cast<uint>(), sharedProgram);
        }

        #region SipHash


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Haraka(SipState s, ulong input, ArrayView<uint> roundConstants, ArrayView<uint> lookupTable)
        {
            s.V1 ^= 0xee;
            s.V3 ^= input;

            s.SipRound();
            s.SipRound();

            s.V0 ^= input;
            s.V2 ^= 0xee;

            s.SipRound();
            s.SipRound();
            s.SipRound();
            s.SipRound();

            ulong v0 = s.V0;
            ulong v1 = s.V1;
            ulong v2 = s.V2;
            ulong v3 = s.V3;

            s.V1 ^= 0xdd;

            s.SipRound();
            s.SipRound();
            s.SipRound();
            s.SipRound();

            //AES Encrypt

            ulong xor = v1 ^ v3 ^ s.V0 ^ s.V2;

            var v0P = IntrinsicMath.Decompose(v0);
            var v1P = IntrinsicMath.Decompose(v1);
            var v2P = IntrinsicMath.Decompose(v2);
            var v3P = IntrinsicMath.Decompose(v3);
            var v4P = IntrinsicMath.Decompose(s.V0);
            var v5P = IntrinsicMath.Decompose(s.V1);
            var v6P = IntrinsicMath.Decompose(s.V2);
            var v7P = IntrinsicMath.Decompose(s.V3);

            var tester = LocalMemory.Allocate<uint>(16);

            int roundStart = 0;
            for (int i = 0; i < 6; i++)
            {

                #region Round 1

                {
                    uint temp0_0 = LoadValue(lookupTable, ExtractByte3(v1P.Upper), 24) ^ LoadValue(lookupTable, ExtractByte2(v1P.Lower), 16) ^ LoadValue(lookupTable, ExtractByte1(v0P.Upper), 8) ^ LoadValue(lookupTable, ExtractByte0(v0P.Lower)) ^ roundConstants[roundStart + 0];
                    uint temp1_0 = LoadValue(lookupTable, ExtractByte3(v0P.Lower), 24) ^ LoadValue(lookupTable, ExtractByte2(v1P.Upper), 16) ^ LoadValue(lookupTable, ExtractByte1(v1P.Lower), 8) ^ LoadValue(lookupTable, ExtractByte0(v0P.Upper)) ^ roundConstants[roundStart + 1];
                    uint temp2_0 = LoadValue(lookupTable, ExtractByte3(v0P.Upper), 24) ^ LoadValue(lookupTable, ExtractByte2(v0P.Lower), 16) ^ LoadValue(lookupTable, ExtractByte1(v1P.Upper), 8) ^ LoadValue(lookupTable, ExtractByte0(v1P.Lower)) ^ roundConstants[roundStart + 2];
                    uint temp3_0 = LoadValue(lookupTable, ExtractByte3(v1P.Lower), 24) ^ LoadValue(lookupTable, ExtractByte2(v0P.Upper), 16) ^ LoadValue(lookupTable, ExtractByte1(v0P.Lower), 8) ^ LoadValue(lookupTable, ExtractByte0(v1P.Upper)) ^ roundConstants[roundStart + 3];
                    tester[0] = LoadValue(lookupTable, ExtractByte3(temp3_0), 24) ^ LoadValue(lookupTable, ExtractByte2(temp2_0), 16) ^ LoadValue(lookupTable, ExtractByte1(temp1_0), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp0_0))) ^ roundConstants[roundStart + 16];
                    tester[1] = LoadValue(lookupTable, ExtractByte3(temp0_0), 24) ^ LoadValue(lookupTable, ExtractByte2(temp3_0), 16) ^ LoadValue(lookupTable, ExtractByte1(temp2_0), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp1_0))) ^ roundConstants[roundStart + 17];
                    tester[2] = LoadValue(lookupTable, ExtractByte3(temp1_0), 24) ^ LoadValue(lookupTable, ExtractByte2(temp0_0), 16) ^ LoadValue(lookupTable, ExtractByte1(temp3_0), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp2_0))) ^ roundConstants[roundStart + 18];
                    v0P.Lower = LoadValue(lookupTable, ExtractByte3(temp2_0), 24) ^ LoadValue(lookupTable, ExtractByte2(temp1_0), 16) ^ LoadValue(lookupTable, ExtractByte1(temp0_0), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp3_0))) ^ roundConstants[roundStart + 19];

                    uint temp0_1 = LoadValue(lookupTable, ExtractByte3(v3P.Upper), 24) ^ LoadValue(lookupTable, ExtractByte2(v3P.Lower), 16) ^ LoadValue(lookupTable, ExtractByte1(v2P.Upper), 8) ^ LoadValue(lookupTable, ExtractByte0(v2P.Lower)) ^ roundConstants[roundStart + 4];
                    uint temp1_1 = LoadValue(lookupTable, ExtractByte3(v2P.Lower), 24) ^ LoadValue(lookupTable, ExtractByte2(v3P.Upper), 16) ^ LoadValue(lookupTable, ExtractByte1(v3P.Lower), 8) ^ LoadValue(lookupTable, ExtractByte0(v2P.Upper)) ^ roundConstants[roundStart + 5];
                    uint temp2_1 = LoadValue(lookupTable, ExtractByte3(v2P.Upper), 24) ^ LoadValue(lookupTable, ExtractByte2(v2P.Lower), 16) ^ LoadValue(lookupTable, ExtractByte1(v3P.Upper), 8) ^ LoadValue(lookupTable, ExtractByte0(v3P.Lower)) ^ roundConstants[roundStart + 6];
                    uint temp3_1 = LoadValue(lookupTable, ExtractByte3(v3P.Lower), 24) ^ LoadValue(lookupTable, ExtractByte2(v2P.Upper), 16) ^ LoadValue(lookupTable, ExtractByte1(v2P.Lower), 8) ^ LoadValue(lookupTable, ExtractByte0(v3P.Upper)) ^ roundConstants[roundStart + 7];
                    v3P.Upper = LoadValue(lookupTable, ExtractByte3(temp3_1), 24) ^ LoadValue(lookupTable, ExtractByte2(temp2_1), 16) ^ LoadValue(lookupTable, ExtractByte1(temp1_1), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp0_1))) ^ roundConstants[roundStart + 20];
                    tester[5] = LoadValue(lookupTable, ExtractByte3(temp0_1), 24) ^ LoadValue(lookupTable, ExtractByte2(temp3_1), 16) ^ LoadValue(lookupTable, ExtractByte1(temp2_1), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp1_1))) ^ roundConstants[roundStart + 21];
                    tester[6] = LoadValue(lookupTable, ExtractByte3(temp1_1), 24) ^ LoadValue(lookupTable, ExtractByte2(temp0_1), 16) ^ LoadValue(lookupTable, ExtractByte1(temp3_1), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp2_1))) ^ roundConstants[roundStart + 22];
                    v1P.Lower = LoadValue(lookupTable, ExtractByte3(temp2_1), 24) ^ LoadValue(lookupTable, ExtractByte2(temp1_1), 16) ^ LoadValue(lookupTable, ExtractByte1(temp0_1), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp3_1))) ^ roundConstants[roundStart + 23];

                    v2P.Upper = tester[0];//4

                    uint temp0_2 = LoadValue(lookupTable, ExtractByte3(v5P.Upper), 24) ^ LoadValue(lookupTable, ExtractByte2(v5P.Lower), 16) ^ LoadValue(lookupTable, ExtractByte1(v4P.Upper), 8) ^ LoadValue(lookupTable, ExtractByte0(v4P.Lower)) ^ roundConstants[roundStart + 8];
                    uint temp1_2 = LoadValue(lookupTable, ExtractByte3(v4P.Lower), 24) ^ LoadValue(lookupTable, ExtractByte2(v5P.Upper), 16) ^ LoadValue(lookupTable, ExtractByte1(v5P.Lower), 8) ^ LoadValue(lookupTable, ExtractByte0(v4P.Upper)) ^ roundConstants[roundStart + 9];
                    uint temp2_2 = LoadValue(lookupTable, ExtractByte3(v4P.Upper), 24) ^ LoadValue(lookupTable, ExtractByte2(v4P.Lower), 16) ^ LoadValue(lookupTable, ExtractByte1(v5P.Upper), 8) ^ LoadValue(lookupTable, ExtractByte0(v5P.Lower)) ^ roundConstants[roundStart + 10];
                    uint temp3_2 = LoadValue(lookupTable, ExtractByte3(v5P.Lower), 24) ^ LoadValue(lookupTable, ExtractByte2(v4P.Upper), 16) ^ LoadValue(lookupTable, ExtractByte1(v4P.Lower), 8) ^ LoadValue(lookupTable, ExtractByte0(v5P.Upper)) ^ roundConstants[roundStart + 11];
                    v2P.Lower = LoadValue(lookupTable, ExtractByte3(temp3_2), 24) ^ LoadValue(lookupTable, ExtractByte2(temp2_2), 16) ^ LoadValue(lookupTable, ExtractByte1(temp1_2), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp0_2))) ^ roundConstants[roundStart + 24];
                    v4P.Lower = LoadValue(lookupTable, ExtractByte3(temp0_2), 24) ^ LoadValue(lookupTable, ExtractByte2(temp3_2), 16) ^ LoadValue(lookupTable, ExtractByte1(temp2_2), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp1_2))) ^ roundConstants[roundStart + 25];
                    tester[10] = LoadValue(lookupTable, ExtractByte3(temp1_2), 24) ^ LoadValue(lookupTable, ExtractByte2(temp0_2), 16) ^ LoadValue(lookupTable, ExtractByte1(temp3_2), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp2_2))) ^ roundConstants[roundStart + 26];
                    v0P.Upper = LoadValue(lookupTable, ExtractByte3(temp2_2), 24) ^ LoadValue(lookupTable, ExtractByte2(temp1_2), 16) ^ LoadValue(lookupTable, ExtractByte1(temp0_2), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp3_2))) ^ roundConstants[roundStart + 27];
 
                    v4P.Upper = tester[1];//3
                    v5P.Upper = tester[5];

                    uint temp0_3 = LoadValue(lookupTable, ExtractByte3(v7P.Upper), 24) ^ LoadValue(lookupTable, ExtractByte2(v7P.Lower), 16) ^ LoadValue(lookupTable, ExtractByte1(v6P.Upper), 8) ^ LoadValue(lookupTable, ExtractByte0(v6P.Lower)) ^ roundConstants[roundStart + 12];
                    uint temp1_3 = LoadValue(lookupTable, ExtractByte3(v6P.Lower), 24) ^ LoadValue(lookupTable, ExtractByte2(v7P.Upper), 16) ^ LoadValue(lookupTable, ExtractByte1(v7P.Lower), 8) ^ LoadValue(lookupTable, ExtractByte0(v6P.Upper)) ^ roundConstants[roundStart + 13];
                    uint temp2_3 = LoadValue(lookupTable, ExtractByte3(v6P.Upper), 24) ^ LoadValue(lookupTable, ExtractByte2(v6P.Lower), 16) ^ LoadValue(lookupTable, ExtractByte1(v7P.Upper), 8) ^ LoadValue(lookupTable, ExtractByte0(v7P.Lower)) ^ roundConstants[roundStart + 14];
                    uint temp3_3 = LoadValue(lookupTable, ExtractByte3(v7P.Lower), 24) ^ LoadValue(lookupTable, ExtractByte2(v6P.Upper), 16) ^ LoadValue(lookupTable, ExtractByte1(v6P.Lower), 8) ^ LoadValue(lookupTable, ExtractByte0(v7P.Upper)) ^ roundConstants[roundStart + 15];

                    v3P.Lower = LoadValue(lookupTable, ExtractByte3(temp3_3), 24) ^ LoadValue(lookupTable, ExtractByte2(temp2_3), 16) ^ LoadValue(lookupTable, ExtractByte1(temp1_3), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp0_3))) ^ roundConstants[roundStart + 28];
                    v5P.Lower = LoadValue(lookupTable, ExtractByte3(temp0_3), 24) ^ LoadValue(lookupTable, ExtractByte2(temp3_3), 16) ^ LoadValue(lookupTable, ExtractByte1(temp2_3), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp1_3))) ^ roundConstants[roundStart + 29];
                    v7P.Upper = LoadValue(lookupTable, ExtractByte3(temp1_3), 24) ^ LoadValue(lookupTable, ExtractByte2(temp0_3), 16) ^ LoadValue(lookupTable, ExtractByte1(temp3_3), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp2_3))) ^ roundConstants[roundStart + 30];
                    v1P.Upper = LoadValue(lookupTable, ExtractByte3(temp2_3), 24) ^ LoadValue(lookupTable, ExtractByte2(temp1_3), 16) ^ LoadValue(lookupTable, ExtractByte1(temp0_3), 8) ^ LoadValue(lookupTable, (ExtractByte0(temp3_3))) ^ roundConstants[roundStart + 31];


                    v6P.Lower = tester[2];//2
                    v6P.Upper = tester[10];//1
                    v7P.Lower = tester[6];//1

                    //var t0 = v0P;
                    //var t1 = v1P;
                    //var t2 = v2P;
                    //var t3 = v3P;
                    //var t4 = v4P;
                    //var t5 = v5P;
                    //var t6 = v6P;
                    //var t7 = v7P;








                    roundStart += 32;

                    Warp.Barrier();
                }

                #endregion

            }

            var temp = IntrinsicMath.Decompose(xor);

            return IntrinsicMath.ComposeULong(new IntrinsicMath.IntegerParts(temp.Lower ^ v1P.Lower ^ v3P.Lower ^ v4P.Lower ^ v6P.Lower, temp.Upper ^ v1P.Upper ^ v3P.Upper ^ v4P.Upper ^ v6P.Upper));
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint LoadValue(ArrayView<uint> lookupTable, int location, int rotation)
        {
            uint v = lookupTable[location * 32 + Warp.LaneIdx].Rol(rotation);

            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint LoadValue(ArrayView<uint> lookupTable, int location)
        {
            uint v = lookupTable[location * 32 + Warp.LaneIdx];

            return v;
        }

        [IntrinsicMethod(nameof(LoadGlobalV4_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UIntx4 LoadGlobalV4(ref uint startLocation)
        {
            return default;
        }


        private static void LoadGlobalV4_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var startRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);

            var returnValue = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Allocate(value);
            var command = codeGenerator.BeginCommand($"ld.global.v4.s32 {{" +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[0])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[1])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[2])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[3])}" +
                $"}}, " +
                $"[%{PTXRegisterAllocator.GetStringRepresentation(startRef)}]");
            command.Dispose();
        }

        [IntrinsicMethod(nameof(LoadSipKey_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SipState LoadSipKey(ref ulong startLocation)
        {
            return default;
        }


        private static void LoadSipKey_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var startRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);

            var returnValue = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Allocate(value);
            var command = codeGenerator.BeginCommand($"ld.global.v2.s64 {{" +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[0])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[1])}" +
                $"}}, " +
                $"[%{PTXRegisterAllocator.GetStringRepresentation(startRef)}]");
            command.Dispose();

            command = codeGenerator.BeginCommand($"ld.global.v2.s64 {{" +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[2])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[3])}" +
                $"}}, " +
                $"[%{PTXRegisterAllocator.GetStringRepresentation(startRef)} + 16]");
            command.Dispose();

        }

        [IntrinsicMethod(nameof(ExtractByte0_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ExtractByte0(uint val)
        {
            return (int)(val & 0xFF);
        }


        private static void ExtractByte0_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var a = codeGenerator.LoadPrimitive(value[0]);
            var returnValue = codeGenerator.AllocateHardware(value);

            var command = codeGenerator.BeginCommand($"bfe.u32");
            command.AppendArgument(returnValue);
            command.AppendArgument(a);
            command.AppendConstant(0);
            command.AppendConstant(8);
            command.Dispose();
        }

        [IntrinsicMethod(nameof(ExtractByte1_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ExtractByte1(uint val)
        {
            return (int)((val >> 8) & 0xFF);
        }


        private static void ExtractByte1_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var a = codeGenerator.LoadPrimitive(value[0]);
            var returnValue = codeGenerator.AllocateHardware(value);

            var command = codeGenerator.BeginCommand($"bfe.u32");
            command.AppendArgument(returnValue);
            command.AppendArgument(a);
            command.AppendConstant(8);
            command.AppendConstant(8);
            command.Dispose();
        }
        [IntrinsicMethod(nameof(ExtractByte2_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ExtractByte2(uint val)
        {
            return (int)((val >> 16) & 0xFF);
        }


        private static void ExtractByte2_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var a = codeGenerator.LoadPrimitive(value[0]);
            var returnValue = codeGenerator.AllocateHardware(value);

            var command = codeGenerator.BeginCommand($"bfe.u32");
            command.AppendArgument(returnValue);
            command.AppendArgument(a);
            command.AppendConstant(16);
            command.AppendConstant(8);
            command.Dispose();
        }
        [IntrinsicMethod(nameof(ExtractByte3_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ExtractByte3(uint val)
        {
            return (int)((val >> 24) & 0xFF);
        }


        private static void ExtractByte3_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var a = codeGenerator.LoadPrimitive(value[0]);
            var returnValue = codeGenerator.AllocateHardware(value);

            var command = codeGenerator.BeginCommand($"bfe.u32");
            command.AppendArgument(returnValue);
            command.AppendArgument(a);
            command.AppendConstant(24);
            command.AppendConstant(8);
            command.Dispose();
        }

        #endregion

        private struct UIntx4
        {
            public uint V0;
            public uint V1;
            public uint V2;
            public uint V3;
        }

        private struct ULongx4
        {
            public ulong V0;
            public ulong V1;
            public ulong V2;
            public ulong V3;
        }
    }
}
