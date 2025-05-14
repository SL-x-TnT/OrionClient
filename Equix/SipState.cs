using Blake2Sharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DrillX
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct SipState
    {
        public const int Size = 32;

        public ulong V0;
        public ulong V1;
        public ulong V2;
        public ulong V3;

        private static Blake2BConfig _config = new Blake2BConfig
        {
            Salt = Encoding.UTF8.GetBytes("HashX v1"),
            OutputSizeInBytes = 64
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (SipState key0, SipState key1) PairFromSeed(byte[] seed)
        {
            byte[] data = Blake2B.ComputeHash(seed, _config);

            var t = MemoryMarshal.Cast<byte, ulong>(data.AsSpan());

            return (Create(t.Slice(0, 4)), Create(t.Slice(4, 4)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static SipState Create(ReadOnlySpan<ulong> bytes)
        {
            return new SipState
            {
                V0 = bytes[0],
                V1 = bytes[1],
                V2 = bytes[2],
                V3 = bytes[3]
            };

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static SipState Create(ulong v0, ulong v1, ulong v2, ulong v3)
        {
            return new SipState
            {
                V0 = v0,
                V1 = v1,
                V2 = v2,
                V3 = v3
            };

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SipRound()
        {
            unchecked
            {
                V0 = V0 + V1;
                V2 = V2 + V3;
                V1 = V1.Rol(13);
                V3 = V3.Rol(16);
                V1 ^= V0;
                V3 ^= V2;
                V0 = V0.Rol(32);

                V2 = V2 + V1;
                V0 = V0 + V3;
                V1 = V1.Rol(17);
                V3 = V3.Rol(21);
                V1 ^= V2;
                V3 ^= V0;
                V2 = V2.Rol(32);
            }
        }
    }

    public static class RotateEx
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Rol(this ulong ul, int N) => (ul << N) ^ (ul >> (64 - N));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Rol(this uint ul, int N) => (ul << N) ^ (ul >> (32 - N));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Rol_2(ref this ulong ul, int N) => ul = (ul << N) | (ul >> (64 - N));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Ror(this ulong ul, int N) => (ul << (64 - N)) ^ (ul >> N);
    }

}
