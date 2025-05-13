using DrillX;
using DrillX.Solver;
using Equix;
using OrionClientLib.Hashers.Models;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace OrionClientLib.Hashers.CPU
{
    public unsafe class ManagedCPUHarakaHasherAVX2 : BaseCPUHasher
    {
        public override string Name => "AES-NI + AVX2 [[C#]]";
        public override string Description => "AES-NI implementation of DrillX w/ Haraka algorithm & AVX2 optimizations";
        public override bool DisplaySetting => true;

        protected override void ExecuteThread(ExecutionData data)
        {
            if (!_solverQueue.TryDequeue(out Solver solver))
            {
                return;
            }

            try
            {
                int bestDifficulty = 0;
                byte[] bestSolution = null;
                ulong bestNonce = 0;

                byte[] fullChallenge = new byte[64];
                Span<byte> fullChallengeSpan = new Span<byte>(fullChallenge);
                EquixSolution* solutions = stackalloc EquixSolution[EquixSolution.MaxLength];
                Span<ushort> testSolution = stackalloc ushort[8];
                Span<byte> hashOutput = stackalloc byte[32];
                Span<Haraka.HarakaSiphash> sipKey = stackalloc Haraka.HarakaSiphash[1];

                _info.Challenge.AsSpan().CopyTo(fullChallenge.AsSpan().Slice(8));

                ulong hashesChecked = 0;

                fixed (ulong* rk = HarakaConstants.RoundKeys2)
                {
                    for (long i = data.Range.Item1; i < data.Range.Item2; i++)
                    {
                        ulong currentNonce = (ulong)i + _info.StartNonce;

                        if (i % 128 == 0)
                        {
                            hashesChecked = (ulong)(i - data.Range.Item1) - hashesChecked;

                            if (!ShouldContinueExecution())
                            {
                                return;
                            }

                            _info.UpdateDifficulty(bestDifficulty, bestSolution, bestNonce);
                            data.Callback(hashesChecked);
                        }


                        BinaryPrimitives.WriteUInt64LittleEndian(fullChallengeSpan, currentNonce);

                        Haraka.HarakaSiphash harakaKey = Haraka.BuildKey(fullChallenge, sipKey);

                        int solutionCount = solver.Solve_Opt_Com_Haraka(harakaKey, (ulong*)solver.ComputeSolutions, (byte*)solver.Heap, solutions, rk);

                        //Calculate difficulty
                        for (int z = 0; z < solutionCount; z++)
                        {
                            Span<EquixSolution> s = new Span<EquixSolution>(solutions, EquixSolution.MaxLength);

                            Span<ushort> eSolution = MemoryMarshal.Cast<EquixSolution, ushort>(s.Slice(z, 1));
                            eSolution.CopyTo(testSolution);

                            testSolution.Sort();

                            SHA3.Sha3Hash(testSolution, currentNonce, hashOutput);

                            int difficulty = CalculateTarget(hashOutput);

                            if (difficulty > bestDifficulty)
                            {
                                //Makes sure the ordering for the solution is valid
                                Reorder(s.Slice(z, 1));

                                bestDifficulty = difficulty;
                                bestNonce = currentNonce;
                                bestSolution = MemoryMarshal.Cast<ushort, byte>(eSolution).ToArray();
                            }
                        }

                        _info.AddSolutionCount((ulong)solutionCount);
                    }
                }
            }
            catch (Exception e)
            {
                data.Exceptions.Enqueue(e);
            }
            finally
            {
                _solverQueue.Enqueue(solver);
            }
        }

        public override bool IsSupported()
        {
            //if (RuntimeInformation.OSArchitecture == Architecture.Arm)
            //{
            //    return System.Runtime.Intrinsics.Arm.Aes.IsSupported;
            //}

            {
                return System.Runtime.Intrinsics.X86.Aes.IsSupported && Avx2.IsSupported;
            }
        }
    }
}
