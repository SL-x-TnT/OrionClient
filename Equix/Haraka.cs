
using DrillX;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.Intrinsics.Arm.AdvSimd;

namespace Equix
{
    public unsafe class Haraka
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static HarakaSiphash BuildKey(Span<byte> fullChallenge, Span<HarakaSiphash> output)
        {
            Blake2Fast.Blake2s.ComputeAndWriteHash(32, fullChallenge, MemoryMarshal.Cast<HarakaSiphash, byte>(output));

            return output[0];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static ulong HashInput(ulong input, ulong* roundConstants, HarakaSiphash sipKey)
        {
            //Siphash24ctr

            ulong v0 = sipKey.V[0];
            ulong v1 = sipKey.V[1] ^ 0xee;
            ulong v2 = sipKey.V[2];
            ulong v3 = sipKey.V[3] ^ input;

            SipRoundLow();
            SipRoundLow();

            v0 ^= input;
            v2 ^= 0xee;

            SipRoundLow();
            SipRoundLow();
            SipRoundLow();
            SipRoundLow();


            ulong v4 = v0;
            ulong v5 = v1 ^ 0xdd;
            ulong v6 = v2;
            ulong v7 = v3;

            SipRoundHigh();
            SipRoundHigh();
            SipRoundHigh();
            SipRoundHigh();

            return Haraka512(v0, v1, v2, v3, v4, v5, v6, v7, roundConstants);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SipRoundLow()
            {
                v0 = v0 + v1;
                v2 = v2 + v3;
                v1 = v1.Rol(13);
                v3 = v3.Rol(16);
                v1 ^= v0;
                v3 ^= v2;
                v0 = v0.Rol(32);

                v2 = v2 + v1;
                v0 = v0 + v3;
                v1 = v1.Rol(17);
                v3 = v3.Rol(21);
                v1 ^= v2;
                v3 ^= v0;
                v2 = v2.Rol(32);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SipRoundHigh()
            {
                v4 = v4 + v5;
                v6 = v6 + v7;
                v5 = v5.Rol(13);
                v7 = v7.Rol(16);
                v5 ^= v4;
                v7 ^= v6;
                v4 = v4.Rol(32);

                v6 = v6 + v5;
                v4 = v4 + v7;
                v5 = v5.Rol(17);
                v7 = v7.Rol(21);
                v5 ^= v6;
                v7 ^= v4;
                v6 = v6.Rol(32);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Haraka512(ulong v0, ulong v1, ulong v2, ulong v3, ulong v4, ulong v5, ulong v6, ulong v7, ulong* roundConstants)
        {
            Vector128<byte> s0 = Vector128.Create(v0, v1).AsByte();
            Vector128<byte> s1 = Vector128.Create(v2, v3).AsByte();
            Vector128<byte> s2 = Vector128.Create(v4, v5).AsByte();
            Vector128<byte> s3 = Vector128.Create(v6, v7).AsByte();

            {
                //if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    AESEncrypt(roundConstants);
                    AESEncrypt(roundConstants + 16);
                    AESEncrypt(roundConstants + 32);
                    AESEncrypt(roundConstants + 48);
                    AESEncrypt(roundConstants + 64);
                    AESEncrypt(roundConstants + 80);
                }
                //else
                //{
                //    AESEncryptArm(roundConstants);
                //    AESEncryptArm(roundConstants + 16);
                //    AESEncryptArm(roundConstants + 32);
                //    AESEncryptArm(roundConstants + 48);
                //    AESEncryptArm(roundConstants + 64);
                //    AESEncryptArm(roundConstants + 80);
                //}

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    void AESEncrypt(ulong* roundConstants)
                    {
                            s0 = Aes.Encrypt(s0, Vector128.Load(roundConstants).AsByte());
                            s1 = Aes.Encrypt(s1, Vector128.Load(roundConstants + 2).AsByte());
                            s2 = Aes.Encrypt(s2, Vector128.Load(roundConstants + 4).AsByte());
                            s3 = Aes.Encrypt(s3, Vector128.Load(roundConstants + 6).AsByte());

                            s0 = Aes.Encrypt(s0, Vector128.Load(roundConstants + 8).AsByte());
                            s1 = Aes.Encrypt(s1, Vector128.Load(roundConstants + 10).AsByte());
                            s2 = Aes.Encrypt(s2, Vector128.Load(roundConstants + 12).AsByte());
                            s3 = Aes.Encrypt(s3, Vector128.Load(roundConstants + 14).AsByte());


                            var tmp = Avx.UnpackLow(s0.AsInt32(), s1.AsInt32()).AsByte();
                            s0 = Avx.UnpackHigh(s0.AsInt32(), s1.AsInt32()).AsByte();

                            s1 = Avx.UnpackLow(s2.AsInt32(), s3.AsInt32()).AsByte();
                            s2 = Avx.UnpackHigh(s2.AsInt32(), s3.AsInt32()).AsByte();

                            s3 = Avx.UnpackLow(s0.AsInt32(), s2.AsInt32()).AsByte();
                            s0 = Avx.UnpackHigh(s0.AsInt32(), s2.AsInt32()).AsByte();

                            s2 = Avx.UnpackHigh(s1.AsInt32(), tmp.AsInt32()).AsByte();
                            s1 = Avx.UnpackLow(s1.AsInt32(), tmp.AsInt32()).AsByte();
                    }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                void AESEncryptArm(ulong* roundConstants)
                {
                    s0 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s0, Vector128.Load(roundConstants).AsByte());
                    s1 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s1, Vector128.Load(roundConstants + 2).AsByte());
                    s2 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s2, Vector128.Load(roundConstants + 4).AsByte());
                    s3 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s3, Vector128.Load(roundConstants + 6).AsByte());

                    s0 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s0, Vector128.Load(roundConstants + 8).AsByte());
                    s1 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s1, Vector128.Load(roundConstants + 10).AsByte());
                    s2 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s2, Vector128.Load(roundConstants + 12).AsByte());
                    s3 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s3, Vector128.Load(roundConstants + 14).AsByte());



                    var tmp = Arm64.ZipLow(s0.AsInt32(), s1.AsInt32()).AsByte();
                    s0 = Arm64.ZipHigh(s0.AsInt32(), s1.AsInt32()).AsByte();

                    s1 = Arm64.ZipLow(s2.AsInt32(), s3.AsInt32()).AsByte();
                    s2 = Arm64.ZipHigh(s2.AsInt32(), s3.AsInt32()).AsByte();

                    s3 = Arm64.ZipLow(s0.AsInt32(), s2.AsInt32()).AsByte();
                    s0 = Arm64.ZipHigh(s0.AsInt32(), s2.AsInt32()).AsByte();

                    s2 = Arm64.ZipHigh(s1.AsInt32(), tmp.AsInt32()).AsByte();
                    s1 = Arm64.ZipLow(s1.AsInt32(), tmp.AsInt32()).AsByte();
                }

                Vector128<byte> t0 = Vector128.Create(v0, v1).AsByte();
                Vector128<byte> t1 = Vector128.Create(v2, v3).AsByte();
                Vector128<byte> t2 = Vector128.Create(v4, v5).AsByte();
                Vector128<byte> t3 = Vector128.Create(v6, v7).AsByte();

                t0 = Vector128.Xor(s0, t0);
                t1 = Vector128.Xor(s1, t1);
                t2 = Vector128.Xor(s2, t2);
                t3 = Vector128.Xor(s3, t3);

                //if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    var a0 = Avx2.UnpackHigh(t0.AsUInt64(), t1.AsUInt64());
                    var a1 = Avx2.UnpackLow(t2.AsUInt64(), t3.AsUInt64());

                    var b0 = Avx2.Xor(a0, a1);
                    var b0_reversed = Sse2.Shuffle(b0.AsDouble(), b0.AsDouble(), 0b01_01).AsUInt64();

                    return Vector128.Xor(b0, b0_reversed)[0];
                }
                //else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                //{
                //    var a0 = Arm64.ZipHigh(t0.AsUInt64(), t1.AsUInt64());
                //    var a1 = Arm64.ZipLow(t2.AsUInt64(), t3.AsUInt64());

                //    return a0[0] ^ a0[1] ^ a1[0] ^ a1[1];
                //}

                throw new NotSupportedException();
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static ulong HashInputArm(ulong input, ulong* roundConstants, HarakaSiphash sipKey)
        {
            //Siphash24ctr

            ulong v0 = sipKey.V[0];
            ulong v1 = sipKey.V[1] ^ 0xee;
            ulong v2 = sipKey.V[2];
            ulong v3 = sipKey.V[3] ^ input;

            SipRoundLow();
            SipRoundLow();

            v0 ^= input;
            v2 ^= 0xee;

            SipRoundLow();
            SipRoundLow();
            SipRoundLow();
            SipRoundLow();


            ulong v4 = v0;
            ulong v5 = v1 ^ 0xdd;
            ulong v6 = v2;
            ulong v7 = v3;

            SipRoundHigh();
            SipRoundHigh();
            SipRoundHigh();
            SipRoundHigh();

            return Haraka512Arm(v0, v1, v2, v3, v4, v5, v6, v7, roundConstants);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SipRoundLow()
            {
                v0 = v0 + v1;
                v2 = v2 + v3;
                v1 = v1.Rol(13);
                v3 = v3.Rol(16);
                v1 ^= v0;
                v3 ^= v2;
                v0 = v0.Rol(32);

                v2 = v2 + v1;
                v0 = v0 + v3;
                v1 = v1.Rol(17);
                v3 = v3.Rol(21);
                v1 ^= v2;
                v3 ^= v0;
                v2 = v2.Rol(32);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SipRoundHigh()
            {
                v4 = v4 + v5;
                v6 = v6 + v7;
                v5 = v5.Rol(13);
                v7 = v7.Rol(16);
                v5 ^= v4;
                v7 ^= v6;
                v4 = v4.Rol(32);

                v6 = v6 + v5;
                v4 = v4 + v7;
                v5 = v5.Rol(17);
                v7 = v7.Rol(21);
                v5 ^= v6;
                v7 ^= v4;
                v6 = v6.Rol(32);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Haraka512Arm(ulong v0, ulong v1, ulong v2, ulong v3, ulong v4, ulong v5, ulong v6, ulong v7, ulong* roundConstants)
        {
            Vector128<byte> s0 = Vector128.Create(v0, v1).AsByte();
            Vector128<byte> s1 = Vector128.Create(v2, v3).AsByte();
            Vector128<byte> s2 = Vector128.Create(v4, v5).AsByte();
            Vector128<byte> s3 = Vector128.Create(v6, v7).AsByte();

            {
                AESEncryptArm(roundConstants);
                AESEncryptArm(roundConstants + 16);
                AESEncryptArm(roundConstants + 32);
                AESEncryptArm(roundConstants + 48);
                AESEncryptArm(roundConstants + 64);
                AESEncryptArm(roundConstants + 80);

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                void AESEncryptArm(ulong* roundConstants)
                {
                    s0 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s0, Vector128.Load(roundConstants).AsByte());
                    s1 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s1, Vector128.Load(roundConstants + 2).AsByte());
                    s2 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s2, Vector128.Load(roundConstants + 4).AsByte());
                    s3 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s3, Vector128.Load(roundConstants + 6).AsByte());

                    s0 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s0, Vector128.Load(roundConstants + 8).AsByte());
                    s1 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s1, Vector128.Load(roundConstants + 10).AsByte());
                    s2 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s2, Vector128.Load(roundConstants + 12).AsByte());
                    s3 = System.Runtime.Intrinsics.Arm.Aes.Encrypt(s3, Vector128.Load(roundConstants + 14).AsByte());



                    var tmp = Arm64.ZipLow(s0.AsInt32(), s1.AsInt32()).AsByte();
                    s0 = Arm64.ZipHigh(s0.AsInt32(), s1.AsInt32()).AsByte();

                    s1 = Arm64.ZipLow(s2.AsInt32(), s3.AsInt32()).AsByte();
                    s2 = Arm64.ZipHigh(s2.AsInt32(), s3.AsInt32()).AsByte();

                    s3 = Arm64.ZipLow(s0.AsInt32(), s2.AsInt32()).AsByte();
                    s0 = Arm64.ZipHigh(s0.AsInt32(), s2.AsInt32()).AsByte();

                    s2 = Arm64.ZipHigh(s1.AsInt32(), tmp.AsInt32()).AsByte();
                    s1 = Arm64.ZipLow(s1.AsInt32(), tmp.AsInt32()).AsByte();
                }

                Vector128<byte> t0 = Vector128.Create(v0, v1).AsByte();
                Vector128<byte> t1 = Vector128.Create(v2, v3).AsByte();
                Vector128<byte> t2 = Vector128.Create(v4, v5).AsByte();
                Vector128<byte> t3 = Vector128.Create(v6, v7).AsByte();

                t0 = Vector128.Xor(s0, t0);
                t1 = Vector128.Xor(s1, t1);
                t2 = Vector128.Xor(s2, t2);
                t3 = Vector128.Xor(s3, t3);

                var a0 = Arm64.ZipHigh(t0.AsUInt64(), t1.AsUInt64());
                var a1 = Arm64.ZipLow(t2.AsUInt64(), t3.AsUInt64());

                return a0[0] ^ a0[1] ^ a1[0] ^ a1[1];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void HashInput_AVX2(ulong startInput, ulong* roundConstants, ulong* output, HarakaSiphash sipKey)
        {
            var vv0 = Vector256.Create(sipKey.V[0]);
            var vv1 = Vector256.Create(sipKey.V[1] ^ 0xee);
            var vv2 = Vector256.Create(sipKey.V[2]);
            var vv3 = Vector256.Create(sipKey.V[3]);

            Vector256<ulong> s0 = vv0;
            Vector256<ulong> s1 = vv1;
            Vector256<ulong> s2 = vv2;
            Vector256<ulong> input = Vector256.Create(startInput, startInput + 1, startInput + 2, startInput + 3);
            Vector256<ulong> s3 = Avx2.Xor(vv3, input);

            SipRound();
            SipRound();

            s0 = Avx2.Xor(s0, input);
            s2 = Avx2.Xor(s2, Vector256.Create((ulong)0xee));

            SipRound();
            SipRound();
            SipRound();
            SipRound();

            var b0 = Avx2.UnpackLow(s0, s1);
            var b1 = Avx2.UnpackLow(s2, s3);
            var b2 = Avx2.UnpackHigh(s0, s1);
            var b3 = Avx2.UnpackHigh(s2, s3);

            var a = Avx2.Permute2x128(b0, b1, 0x20);
            var b = Avx2.Permute2x128(b2, b3, 0x20);
            var c = Avx2.Permute2x128(b0, b1, 0x31);
            var d = Avx2.Permute2x128(b2, b3, 0x31);

            var r0 = Avx2.Permute2x128(b0, b1, 0x20);
            var r2 = Avx2.Permute2x128(b2, b3, 0x20);
            var r4 = Avx2.Permute2x128(b0, b1, 0x31);
            var r6 = Avx2.Permute2x128(b2, b3, 0x31);

            s1 = Avx2.Xor(s1, Vector256.Create((ulong)0xdd));

            SipRound();
            SipRound();
            SipRound();
            SipRound();

            b0 = Avx2.UnpackLow(s0, s1);
            b1 = Avx2.UnpackLow(s2, s3);
            b2 = Avx2.UnpackHigh(s0, s1);
            b3 = Avx2.UnpackHigh(s2, s3);

            var r1 = Avx2.Permute2x128(b0, b1, 0x20);
            var r3 = Avx2.Permute2x128(b2, b3, 0x20);
            var r5 = Avx2.Permute2x128(b0, b1, 0x31);
            var r7 = Avx2.Permute2x128(b2, b3, 0x31);

            Haraka512_AVX2(r0.GetLower().AsByte(), r0.GetUpper().AsByte(), r1.GetLower().AsByte(), r1.GetUpper().AsByte(), roundConstants, output);
            Haraka512_AVX2(r2.GetLower().AsByte(), r2.GetUpper().AsByte(), r3.GetLower().AsByte(), r3.GetUpper().AsByte(), roundConstants, output + 1);
            Haraka512_AVX2(r4.GetLower().AsByte(), r4.GetUpper().AsByte(), r5.GetLower().AsByte(), r5.GetUpper().AsByte(), roundConstants, output + 2);
            Haraka512_AVX2(r6.GetLower().AsByte(), r6.GetUpper().AsByte(), r7.GetLower().AsByte(), r7.GetUpper().AsByte(), roundConstants, output + 3);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SipRound()
            {
                s0 = Avx2.Add(s0, s1);
                s2 = Avx2.Add(s2, s3);
                s1 = Avx2.Or(Avx2.ShiftLeftLogical(s1, 13), Avx2.ShiftRightLogical(s1, (64 - 13)));

                s3 = Avx2.Shuffle(s3.AsSByte(), Vector256.Create(6, 7, 0, 1, 2, 3, 4, 5, 14, 15, 8, 9, 10, 11, 12, 13, 22, 23, 16, 17, 18, 19, 20, 21, 30, 31, 24, 25, 26, 27, 28, 29)).AsUInt64();
                s1 = Avx2.Xor(s1, s0);
                s3 = Avx2.Xor(s3, s2);
                s0 = Avx2.Shuffle(s0.AsInt32(), 177).AsUInt64();

                s2 = Avx2.Add(s2, s1);
                s0 = Avx2.Add(s0, s3);
                s1 = Avx2.Or(Avx2.ShiftLeftLogical(s1, 17), Avx2.ShiftRightLogical(s1, (64 - 17)));
                s3 = Avx2.Or(Avx2.ShiftLeftLogical(s3, 21), Avx2.ShiftRightLogical(s3, (64 - 21)));
                s1 = Avx2.Xor(s1, s2);
                s3 = Avx2.Xor(s3, s0);
                s2 = Avx2.Shuffle(s2.AsInt32(), 177).AsUInt64();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Haraka512_AVX2(Vector128<byte> s0, Vector128<byte> s1, Vector128<byte> s2, Vector128<byte> s3, ulong* roundConstants, ulong* output)
        {
            //s0 = Vector128.Create(23453245, 846635534, 834873546, 86567342).AsByte();
            //s1 = Vector128.Create(0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f).AsByte();
            //s2 = Vector128.Create(0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2d, 0x2e, 0x2f).AsByte();
            //s3 = Vector128.Create(0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f).AsByte();

            var t0 = s0;
            var t1 = s1;
            var t2 = s2;
            var t3 = s3;

            {
                AESEncrypt(roundConstants);
                AESEncrypt(roundConstants + 16);
                AESEncrypt(roundConstants + 32);
                AESEncrypt(roundConstants + 48);
                AESEncrypt(roundConstants + 64);
                AESEncrypt(roundConstants + 80);

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                void AESEncrypt(ulong* roundConstants)
                {
                    s0 = Aes.Encrypt(s0, Avx.LoadVector128(roundConstants).AsByte());
                    s1 = Aes.Encrypt(s1, Avx.LoadVector128(roundConstants + 2).AsByte());
                    s2 = Aes.Encrypt(s2, Avx.LoadVector128(roundConstants + 4).AsByte());
                    s3 = Aes.Encrypt(s3, Avx.LoadVector128(roundConstants + 6).AsByte());

                    s0 = Aes.Encrypt(s0, Avx.LoadVector128(roundConstants + 8).AsByte());
                    s1 = Aes.Encrypt(s1, Avx.LoadVector128(roundConstants + 10).AsByte());
                    s2 = Aes.Encrypt(s2, Avx.LoadVector128(roundConstants + 12).AsByte());
                    s3 = Aes.Encrypt(s3, Avx.LoadVector128(roundConstants + 14).AsByte());

                    var tmp = Avx.UnpackLow(s0.AsInt32(), s1.AsInt32()).AsByte();
                    s0 = Avx.UnpackHigh(s0.AsInt32(), s1.AsInt32()).AsByte();

                    s1 = Avx.UnpackLow(s2.AsInt32(), s3.AsInt32()).AsByte();
                    s2 = Avx.UnpackHigh(s2.AsInt32(), s3.AsInt32()).AsByte();

                    s3 = Avx.UnpackLow(s0.AsInt32(), s2.AsInt32()).AsByte();
                    s0 = Avx.UnpackHigh(s0.AsInt32(), s2.AsInt32()).AsByte();

                    s2 = Avx.UnpackHigh(s1.AsInt32(), tmp.AsInt32()).AsByte();
                    s1 = Avx.UnpackLow(s1.AsInt32(), tmp.AsInt32()).AsByte();
                }


                t0 = Avx.Xor(s0, t0);
                t1 = Avx.Xor(s1, t1);
                t2 = Avx.Xor(s2, t2);
                t3 = Avx.Xor(s3, t3);

                var a0 = Avx2.UnpackHigh(t0.AsUInt64(), t1.AsUInt64());
                var a1 = Avx2.UnpackLow(t2.AsUInt64(), t3.AsUInt64());

                var b0 = Avx2.Xor(a0, a1);
                var b0_reversed = Sse2.Shuffle(b0.AsDouble(), b0.AsDouble(), 0b01_01).AsUInt64();

                output[0] = Avx2.Xor(b0, b0_reversed)[0];
            }
        }


        public struct HarakaSiphash
        {
            public fixed ulong V[4];

            public static HarakaSiphash Create(ReadOnlySpan<byte> input)
            {
                return MemoryMarshal.Cast<byte, HarakaSiphash>(input)[0];
            }
        }
    }
}
