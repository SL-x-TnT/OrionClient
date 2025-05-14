using DrillX.Solver;
using ILGPU;
using System.Runtime.CompilerServices;

namespace OrionClientLib.Hashers.GPU.Baseline
{
    //This equihash implementation needs to be rewritten as currently 4% of solutions are invalid
    //Potential opts:
    //  - Split loops to take advantage of larger block sizes
    //  - Remove scratch usage
    public partial class CudaHarakaGPUHasher
    {
        public const int BlockSize = 512;
        public const int TotalValues = ushort.MaxValue + 1;

        const int numBuckets = 256;
        const int sharedBucketItems = 16; //8 are for a buffer
        const int extendedBucketItems = 18;
        const int totalSharedBucketItems = numBuckets * sharedBucketItems;
        const int totalInnerSharedBucketItems = totalSharedBucketItems;
        const int passes = 32;
        const int maxBucketItems = 512; //512 or 256

        const ulong mask60bit = (1ul << 60) - 1;
        const ulong mask45bit = (1ul << 45) - 1;
        const ulong mask30bit = (1ul << 30) - 1;

        public const long HeapSize = stageValueSize * 3 + stageIndiceSize * 2;
        public const int stageValueSize = TotalValues * sizeof(ulong);
        public const int stageIndiceSize = TotalValues * sizeof(uint);

        private static void Equihash(ArrayView<ulong> values, ArrayView<EquixSolution> solutions, ArrayView<ushort> globalHeap, ArrayView<uint> solutionCount)
        {
            var grid = Grid.GlobalIndex;
            var group = Group.Dimension;

            int blockIndex = (grid.X * group.Y + grid.Y);
            int block = blockIndex / BlockSize;

            values = values.SubView(block * TotalValues, TotalValues);

            var idx = Group.IdxX;

            if (Group.IsFirstThread)
            {
                solutionCount[block] = 0;
            }

            //Group.Barrier();

            #region Global Memory

            var heap = globalHeap.Cast<byte>().SubView(HeapSize * block, HeapSize);


            //1 MB of data
            var tempStage = heap.SubView(TotalValues * 0, stageValueSize * 2).Cast<ulong>();

            //512KB
            var stageBValues = heap.SubView(stageValueSize * 2, stageValueSize).Cast<ulong>();

            //512KB
            var stage1Indices = heap.SubView(stageValueSize * 3, stageIndiceSize).Cast<uint>();

            //256KB
            var tempIndices = heap.SubView(stageValueSize * 3 + stageIndiceSize, stageIndiceSize).Cast<ushort>();

            #endregion

            #region Shared Memory

            //1KB
            var sharedCounts = SharedMemory.Allocate<int>(numBuckets);  // Count for each time

            //1KB
            var fullCount = SharedMemory.Allocate<int>(numBuckets + 1); //Full counts

            //Allows for more values on tile sorting to inccrease solution rate
            var fullShared = SharedMemory.Allocate<byte>(totalSharedBucketItems * sizeof(ulong) + totalSharedBucketItems * sizeof(ushort));

            var sharedValues = fullShared.SubView(0, (totalSharedBucketItems + (extendedBucketItems - sharedBucketItems) * numBuckets) * sizeof(ulong)).Cast<ulong>();// Shared memory for each block
            var sharedIndices = fullShared.SubView(totalSharedBucketItems * sizeof(ulong), totalSharedBucketItems * sizeof(ushort)).Cast<ushort>();// Shared memory for each block

            #endregion


            int totalNum = SortPass1(idx, values, sharedCounts, fullCount, sharedValues, sharedIndices, tempStage, tempIndices, stageBValues, stage1Indices, TotalValues);
            int totalNum1 = SortPass2(idx, stageBValues, sharedCounts, fullCount, sharedValues, sharedIndices, tempStage, values, totalNum);
            SortPass3(idx, values, sharedCounts, fullCount, sharedValues, tempStage, stage1Indices, totalNum1, solutions.Cast<EquixSolution>().SubView(block * 8, 8), solutionCount.SubView(block, 1));

        }

        #region Pass 1

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SortPass1(int idx, ArrayView<ulong> values, ArrayView<int> sharedCounts, ArrayView<int> fullCount,
            ArrayView<ulong> sharedValues, ArrayView<ushort> sharedIndices, ArrayView<ulong> tempStage, ArrayView<ushort> tempIndices, ArrayView<ulong> stageBValues, ArrayView<uint> stage1Indices,
            int totalItems)
        {
            if (idx < numBuckets)
            {
                fullCount[idx] = 0;
            }

            #region Low 8 bits

            for (int x = 0; x < passes; x++)
            {
                //Clear counts
                if (idx < numBuckets)
                {
                    sharedCounts[idx] = 0;
                }

                Group.Barrier();

                //2048 tiles
                int tileItems = totalItems / passes;

                //Scatter to buckets
                for (int i = idx; i < tileItems; i += BlockSize)
                {
                    var index = x * tileItems + i;
                    var value = values[index] & mask60bit;
                    var bucket = (int)value & 0xFF;

                    var loc = Atomic.Add(ref sharedCounts[bucket], 1);
                    loc = Math.Min(extendedBucketItems - 1, loc);

                    //TileLocation = 11 bits
                    //Index = TileLocation * pass
                    ulong v = value >> 8;
                    ulong tileLocation = (ulong)i << 52;

                    sharedValues[loc + bucket * extendedBucketItems] = v | tileLocation;
                }

                Group.Barrier();

                //Verification of indexes
                //if (idx < 256)
                //{
                //    var totalBucketItems = Math.Min(tempSharedBucketItems, sharedCounts[idx]);

                //    for (int i = 0; i < totalBucketItems; i++)
                //    {
                //        var v = sharedValues[i + idx * tempSharedBucketItems];

                //        var tLocation = (int)((v) >> 52);
                //        var index = tLocation + (x * tileItems);

                //        var vv = v & ((1ul << 52) - 1);

                //        var globalValue = (values[(int)index] & mask60bit) >> 8;

                //        if (globalValue != vv)
                //        {
                //            //PrintHex(vv);
                //            Interop.WriteLine("{0} != {1} |||| {2}", vv, globalValue, index);
                //        }
                //    }

                //    //Interop.WriteLine("==== END ===");
                //}


                //Group.Barrier();

                //Transfer over chunks
                var totalIterations = totalSharedBucketItems + (extendedBucketItems - sharedBucketItems) * numBuckets;
                for (int i = idx; i < totalIterations; i += BlockSize)
                {
                    //bucket = i / extendedBucketItems
                    int bucket = (int)(((ulong)i * (ulong)0xE38F) >> 20);
                    int itemIndex = i - (bucket * extendedBucketItems);


                    int actualItems = sharedCounts[bucket];
                    int maxItems = Math.Min(extendedBucketItems, actualItems);
                    ulong value = sharedValues[i];

                    //First thread in bucket
                    if (itemIndex == 0)
                    {
                        Atomic.Add(ref fullCount[bucket], maxItems);
                        //Atomic.Add(ref fullCount[256], actualItems);
                    }

                    Group.Barrier();

                    int startIndex = fullCount[bucket] - maxItems;

                    //Only transfer needed
                    if (itemIndex < maxItems)
                    {
                        int index = startIndex + itemIndex;
                        index = Math.Min(maxBucketItems - 1, index);

                        var shortenedValue = value & ((1ul << 52) - 1); //Value without the 8 bytes for bucket
                        var actualValue = (shortenedValue << 8) | (uint)bucket; //Add bucket back in to make things simple
                        var tLocation = (int)(value >> 52);
                        var globalIndex = tLocation + x * tileItems;

                        tempStage[index + bucket * maxBucketItems] = actualValue;
                        tempIndices[index + bucket * maxBucketItems] = (ushort)globalIndex;
                    }
                }
            }

            #endregion

            Group.Barrier();

            #region High 7 bits

            const int smallBucketItems = totalInnerSharedBucketItems / 2;
            const int smallBuckets = numBuckets / 2;

            //Will be 128 buckets (7 bits) with 16 values each
            var bucketASValues = sharedValues.SubView(0, smallBucketItems);
            var bucketASIndices = sharedIndices.SubView(0, smallBucketItems);
            var bucketACounts = sharedCounts.SubView(0, smallBuckets);

            var bucketBSValues = sharedValues.SubView(smallBucketItems, smallBucketItems);
            var bucketBSIndices = sharedIndices.SubView(smallBucketItems, smallBucketItems);
            var bucketBCounts = sharedCounts.SubView(smallBuckets, smallBuckets);
            var globalMemoryIndex = SharedMemory.Allocate<int>(1);

            if (idx == 0)
            {
                globalMemoryIndex[0] = 0;
            }

            for (int i = 0; i < numBuckets / 2; i++)
            {
                int bucketAIndex = i;
                int bucketBIndex = -i & 0xFF;

                var totalBucketAItems = Math.Min(maxBucketItems, fullCount[bucketAIndex]);
                var totalBucketBItems = Math.Min(maxBucketItems, fullCount[bucketBIndex]);

                //Clear counts
                if (idx < numBuckets)
                {
                    sharedCounts[idx] = 0;
                }

                Group.Barrier();

                var aValues = tempStage.SubView(bucketAIndex * maxBucketItems, maxBucketItems);
                var bValues = tempStage.SubView(bucketBIndex * maxBucketItems, maxBucketItems);
                var aIndices = tempIndices.SubView(bucketAIndex * maxBucketItems, maxBucketItems);
                var bIndices = tempIndices.SubView(bucketBIndex * maxBucketItems, maxBucketItems);

                RadixSort512_15_Pass1(idx, totalBucketAItems, bucketACounts, aValues, aIndices, bucketASValues, bucketASIndices);

                Warp.Barrier();

                RadixSort512_15_Pass1(idx, totalBucketBItems, bucketBCounts, bValues, bIndices, bucketBSValues, bucketBSIndices);

                Group.Barrier();


                //Buckets have no issues


                //8 loops
                //16 items per bucket
                for (int z = idx; z < smallBucketItems; z += BlockSize)
                {
                    int innerBucketA = z / sharedBucketItems;
                    int innerBucketAIndex = z & (sharedBucketItems - 1);

                    int inverseBucketB = -innerBucketA & 0x7F;

                    inverseBucketB = i != 0 ? (inverseBucketB - 1) & 0x7F : inverseBucketB;

                    var bucketACount = Math.Min(sharedBucketItems, bucketACounts[innerBucketA]);
                    var bucketBCount = Math.Min(sharedBucketItems, bucketBCounts[inverseBucketB]);

                    if (bucketACount > innerBucketAIndex)
                    {
                        var bucketAValue = bucketASValues[z];
                        var bucketAIndice = bucketASIndices[z];

                        var initialLoc = Atomic.Add(ref globalMemoryIndex[0], bucketBCount);

                        initialLoc = Math.Min(initialLoc, TotalValues);

                        if (initialLoc + bucketBCount >= TotalValues)
                        {
                            bucketBCount = TotalValues - initialLoc;
                        }

                        for (int x = 0; x < bucketBCount; x++)
                        {
                            var bucketBValue = bucketBSValues[inverseBucketB * sharedBucketItems + x];
                            var bucketBIndice = bucketBSIndices[inverseBucketB * sharedBucketItems + x];
                            var combined = bucketAValue + bucketBValue;

                            //if (initialLoc + x == 0)
                            //{
                            //    var t1 = values[(int)bucketAIndice] & mask60bit;
                            //    var t2 = values[(int)bucketBIndice] & mask60bit;
                            //    var tt = (uint)(bucketAIndice << 16) | bucketBIndice;

                            //    var i1 = tt & 0xFFFF;
                            //    var i2 = tt >> 16;
                            //    Interop.WriteLine("[{2}] Potential indices: {0} {1}. Combined Value: {3}. Shifted Value: {4}. Global Combined: {5}. Indices: {6} {7}", (uint)bucketAIndice, (uint)bucketBIndice, initialLoc + x, combined, combined >> 15, (t1 + t2), i1, i2);


                            //}

                            //45 bits
                            stageBValues[initialLoc + x] = combined >> 15;
                            stage1Indices[initialLoc + x] = (uint)(bucketAIndice << 16) | bucketBIndice;
                        }
                    }
                }

                Group.Barrier();
            }

            #endregion

            Group.Barrier();

            int totalValues = Math.Min(TotalValues, globalMemoryIndex[0]);

            return totalValues;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RadixSort512_15_Pass1(int idx, int max, ArrayView<int> counts, ArrayView<ulong> values, ArrayView<ushort> indices, ArrayView<ulong> output, ArrayView<ushort> indiceOutput)
        {
            //Grab bucket
            {
                ulong value = values[idx];
                ushort indice = indices[idx];

                int bucket = (int)((value >> 8) & 0x7F);

                if (idx < max)
                {
                    //Scatter
                    int loc = Atomic.Add(ref counts[bucket], 1);

                    loc = Math.Min(sharedBucketItems - 1, loc);

                    //It's possible to store the indice within the value
                    output[bucket * sharedBucketItems + loc] = value;
                    indiceOutput[bucket * sharedBucketItems + loc] = indice;
                }
            }
        }

        #endregion

        #region Pass 2

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SortPass2(int idx, ArrayView<ulong> values, ArrayView<int> sharedCounts, ArrayView<int> fullCount,
    ArrayView<ulong> sharedValues, ArrayView<ushort> sharedIndices, ArrayView<ulong> tempStage, ArrayView<ulong> stageBValues, int totalItems)
        {

            if (idx < numBuckets)
            {
                fullCount[idx] = 0;
            }

            #region Low 8 bits

            for (int x = 0; x < passes; x++)
            {
                //Clear counts
                if (idx < numBuckets)
                {
                    sharedCounts[idx] = 0;
                }

                Group.Barrier();

                //2048 tiles
                int tileItems = totalItems / passes;

                //Scatter to buckets
                for (int i = idx; i < tileItems; i += BlockSize)
                {
                    var index = x * tileItems + i;
                    var value = values[index] & mask45bit;
                    var bucket = (int)value & 0xFF;

                    var loc = Atomic.Add(ref sharedCounts[bucket], 1);
                    loc = Math.Min(extendedBucketItems - 1, loc);

                    //TileLocation = 11 bits
                    //Index = TileLocation * pass
                    ulong v = value >> 8;
                    ulong tileLocation = (ulong)i << 52;

                    sharedValues[loc + bucket * extendedBucketItems] = v | tileLocation;
                }

                Group.Barrier();

                //Verification of indexes
                //if (idx < 256)
                //{
                //    var totalBucketItems = Math.Min(tempSharedBucketItems, sharedCounts[idx]);

                //    for (int i = 0; i < totalBucketItems; i++)
                //    {
                //        var v = sharedValues[i + idx * tempSharedBucketItems];

                //        var tLocation = (int)((v) >> 52);
                //        var index = tLocation + (x * tileItems);

                //        var vv = v & ((1ul << 52) - 1);

                //        var globalValue = (values[(int)index] & mask60bit) >> 8;

                //        if (globalValue != vv)
                //        {
                //            //PrintHex(vv);
                //            Interop.WriteLine("{0} != {1} |||| {2}", vv, globalValue, index);
                //        }
                //    }

                //    //Interop.WriteLine("==== END ===");
                //}


                //Group.Barrier();

                //Transfer over chunks
                var totalIterations = totalSharedBucketItems + (extendedBucketItems - sharedBucketItems) * numBuckets;
                for (int i = idx; i < totalIterations; i += BlockSize)
                {
                    //bucket = i / extendedBucketItems
                    int bucket = (int)(((ulong)i * (ulong)0xE38F) >> 20);
                    int itemIndex = i - (bucket * extendedBucketItems);


                    int actualItems = sharedCounts[bucket];
                    int maxItems = Math.Min(extendedBucketItems, actualItems);
                    ulong value = sharedValues[i];

                    //First thread in bucket
                    if (itemIndex == 0)
                    {
                        Atomic.Add(ref fullCount[bucket], maxItems);
                        //Atomic.Add(ref fullCount[256], actualItems);
                    }

                    Group.Barrier();

                    int startIndex = fullCount[bucket] - maxItems;

                    //Only transfer needed
                    if (itemIndex < maxItems)
                    {
                        int index = startIndex + itemIndex;
                        index = Math.Min(maxBucketItems - 1, index);

                        var shortenedValue = value & ((1ul << 52) - 1); //Value without the 8 bytes for bucket
                        var actualValue = (shortenedValue << 8) | (uint)bucket; //Add bucket back in to make things simple
                        var tLocation = (int)(value >> 52);
                        var globalIndex = tLocation + x * tileItems;

                        tempStage[index + bucket * maxBucketItems] = actualValue | ((ulong)globalIndex << 48);
                    }
                }
            }

            #endregion

            Group.Barrier();

            #region High 7 bits

            const int smallBucketItems = totalInnerSharedBucketItems / 2;
            const int smallBuckets = numBuckets / 2;

            //Will be 128 buckets (7 bits) with 16 values each
            var bucketASValues = sharedValues.SubView(0, smallBucketItems);
            var bucketASIndices = sharedIndices.SubView(0, smallBucketItems);
            var bucketACounts = sharedCounts.SubView(0, smallBuckets);

            var bucketBSValues = sharedValues.SubView(smallBucketItems, smallBucketItems);
            var bucketBSIndices = sharedIndices.SubView(smallBucketItems, smallBucketItems);
            var bucketBCounts = sharedCounts.SubView(smallBuckets, smallBuckets);
            var globalMemoryIndex = SharedMemory.Allocate<int>(1);

            if (idx == 0)
            {
                globalMemoryIndex[0] = 0;
            }

            for (int i = 0; i < numBuckets / 2; i++)
            {
                int bucketAIndex = i;
                int bucketBIndex = -i & 0xFF;

                var totalBucketAItems = Math.Min(maxBucketItems, fullCount[bucketAIndex]);
                var totalBucketBItems = Math.Min(maxBucketItems, fullCount[bucketBIndex]);

                //Clear counts
                if (idx < numBuckets)
                {
                    sharedCounts[idx] = 0;
                }

                Group.Barrier();

                var aValues = tempStage.SubView(bucketAIndex * maxBucketItems, maxBucketItems);
                var bValues = tempStage.SubView(bucketBIndex * maxBucketItems, maxBucketItems);

                RadixSort512_15_Pass2(idx, totalBucketAItems, bucketACounts, aValues, bucketASValues);

                Warp.Barrier();

                RadixSort512_15_Pass2(idx, totalBucketBItems, bucketBCounts, bValues, bucketBSValues);

                Group.Barrier();


                //Buckets have no issues


                //8 loops
                //16 items per bucket
                for (int z = idx; z < smallBucketItems; z += BlockSize)
                {
                    int innerBucketA = z / sharedBucketItems;
                    int innerBucketAIndex = z & (sharedBucketItems - 1);

                    int inverseBucketB = -innerBucketA & 0x7F;

                    inverseBucketB = i != 0 ? (inverseBucketB - 1) & 0x7F : inverseBucketB;

                    var bucketACount = Math.Min(sharedBucketItems, bucketACounts[innerBucketA]);
                    var bucketBCount = Math.Min(sharedBucketItems, bucketBCounts[inverseBucketB]);

                    if (bucketACount > innerBucketAIndex)
                    {
                        var bucketAValue = bucketASValues[z];
                        var bucketAIndice = (ushort)(bucketAValue >> 48);

                        var initialLoc = Atomic.Add(ref globalMemoryIndex[0], bucketBCount);

                        initialLoc = Math.Min(initialLoc, TotalValues);

                        if (initialLoc + bucketBCount >= TotalValues)
                        {
                            bucketBCount = TotalValues - initialLoc;
                        }

                        for (int x = 0; x < bucketBCount; x++)
                        {
                            var bucketBValue = bucketBSValues[inverseBucketB * sharedBucketItems + x];
                            var bucketBIndice = (ushort)(bucketBValue >> 48);
                            var combined = bucketAValue + bucketBValue;

                            //if (initialLoc + x == 0)
                            //{
                            //    var t1 = values[(int)bucketAIndice] & mask60bit;
                            //    var t2 = values[(int)bucketBIndice] & mask60bit;
                            //    var tt = (uint)(bucketAIndice << 16) | bucketBIndice;

                            //    var i1 = tt & 0xFFFF;
                            //    var i2 = tt >> 16;
                            //    Interop.WriteLine("[{2}] Potential indices: {0} {1}. Combined Value: {3}. Shifted Value: {4}. Global Combined: {5}. Indices: {6} {7}", (uint)bucketAIndice, (uint)bucketBIndice, initialLoc + x, combined, combined >> 15, (t1 + t2), i1, i2);


                            //}

                            var combinedIndices = (uint)(bucketAIndice << 16) | bucketBIndice;
                            stageBValues[initialLoc + x] = ((combined >> 15) & mask30bit) | ((ulong)combinedIndices << 32);
                        }
                    }
                }

                Group.Barrier();
            }

            #endregion

            Group.Barrier();

            int totalValues = Math.Min(TotalValues, globalMemoryIndex[0]);

            return totalValues;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RadixSort512_15_Pass2(int idx, int max, ArrayView<int> counts, ArrayView<ulong> values, ArrayView<ulong> output)
        {
            //Grab bucket
            {
                ulong value = values[idx];

                int bucket = (int)((value >> 8) & 0x7F);

                if (idx < max)
                {
                    //Scatter
                    int loc = Atomic.Add(ref counts[bucket], 1);

                    loc = Math.Min(sharedBucketItems - 1, loc);

                    output[bucket * sharedBucketItems + loc] = value;
                }
            }
        }

        #endregion

        #region Pass 3

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SortPass3(int idx, ArrayView<ulong> values, ArrayView<int> sharedCounts, ArrayView<int> fullCount,
                                        ArrayView<ulong> sharedValues, ArrayView<ulong> tempStage, ArrayView<uint> stage1Indices,
                                        int totalItems, ArrayView<EquixSolution> solutions, ArrayView<uint> solutionCount)
        {
            if (idx < numBuckets)
            {
                fullCount[idx] = 0;
            }

            #region Low 8 bits

            for (int x = 0; x < passes; x++)
            {
                //Clear counts
                if (idx < numBuckets)
                {
                    sharedCounts[idx] = 0;
                }

                Group.Barrier();

                //2048 tiles
                int tileItems = totalItems / passes;

                //Scatter to buckets
                for (int i = idx; i < tileItems; i += BlockSize)
                {
                    var index = x * tileItems + i;
                    var value = values[index] & mask30bit;
                    var bucket = (int)value & 0xFF;

                    var loc = Atomic.Add(ref sharedCounts[bucket], 1);
                    loc = Math.Min(extendedBucketItems - 1, loc);

                    //TileLocation = 11 bits
                    //Index = TileLocation * pass
                    ulong v = value >> 8;
                    ulong tileLocation = (ulong)i << 52;

                    sharedValues[loc + bucket * extendedBucketItems] = v | tileLocation;
                }

                Group.Barrier();

                //Verification of indexes
                //if (idx < 256)
                //{
                //    var totalBucketItems = Math.Min(tempSharedBucketItems, sharedCounts[idx]);

                //    for (int i = 0; i < totalBucketItems; i++)
                //    {
                //        var v = sharedValues[i + idx * tempSharedBucketItems];

                //        var tLocation = (int)((v) >> 52);
                //        var index = tLocation + (x * tileItems);

                //        var vv = v & ((1ul << 52) - 1);

                //        var globalValue = (values[(int)index] & mask60bit) >> 8;

                //        if (globalValue != vv)
                //        {
                //            //PrintHex(vv);
                //            Interop.WriteLine("{0} != {1} |||| {2}", vv, globalValue, index);
                //        }
                //    }

                //    //Interop.WriteLine("==== END ===");
                //}


                //Group.Barrier();

                //Transfer over chunks
                var totalIterations = totalSharedBucketItems + (extendedBucketItems - sharedBucketItems) * numBuckets;
                for (int i = idx; i < totalIterations; i += BlockSize)
                {
                    //bucket = i / extendedBucketItems
                    int bucket = (int)(((ulong)i * (ulong)0xE38F) >> 20);
                    int itemIndex = i - (bucket * extendedBucketItems);


                    int actualItems = sharedCounts[bucket];
                    int maxItems = Math.Min(extendedBucketItems, actualItems);
                    ulong value = sharedValues[i];

                    //First thread in bucket
                    if (itemIndex == 0)
                    {
                        Atomic.Add(ref fullCount[bucket], maxItems);
                        //Atomic.Add(ref fullCount[256], actualItems);
                    }

                    Group.Barrier();

                    int startIndex = fullCount[bucket] - maxItems;

                    //Only transfer needed
                    if (itemIndex < maxItems)
                    {
                        int index = startIndex + itemIndex;
                        index = Math.Min(maxBucketItems - 1, index);

                        var shortenedValue = value & ((1ul << 52) - 1); //Value without the 8 bytes for bucket
                        var actualValue = (shortenedValue << 8) | (uint)bucket; //Add bucket back in to make things simple
                        var tLocation = (int)(value >> 52);
                        var globalIndex = tLocation + x * tileItems;

                        tempStage[index + bucket * maxBucketItems] = actualValue | ((ulong)globalIndex << 32);
                        //tempIndices[index + bucket * maxBucketItems] = (ushort)globalIndex;
                    }
                }
            }

            #endregion

            Group.Barrier();

            #region High 7 bits

            const int smallBucketItems = totalInnerSharedBucketItems / 2;
            const int smallBuckets = numBuckets / 2;

            //Will be 128 buckets (7 bits) with 16 values each
            var bucketASValues = sharedValues.SubView(0, smallBucketItems);
            //var bucketASIndices = sharedIndices.SubView(0, smallBucketItems);
            var bucketACounts = sharedCounts.SubView(0, smallBuckets);

            var bucketBSValues = sharedValues.SubView(smallBucketItems, smallBucketItems);
            //var bucketBSIndices = sharedIndices.SubView(smallBucketItems, smallBucketItems);
            var bucketBCounts = sharedCounts.SubView(smallBuckets, smallBuckets);
            var globalMemoryIndex = SharedMemory.Allocate<int>(1);

            if (idx == 0)
            {
                globalMemoryIndex[0] = 0;
            }

            for (int i = 0; i < numBuckets / 2; i++)
            {
                int bucketAIndex = i;
                int bucketBIndex = -i & 0xFF;

                var totalBucketAItems = Math.Min(maxBucketItems, fullCount[bucketAIndex]);
                var totalBucketBItems = Math.Min(maxBucketItems, fullCount[bucketBIndex]);

                //Clear counts
                if (idx < numBuckets)
                {
                    sharedCounts[idx] = 0;
                }

                Group.Barrier();

                var aValues = tempStage.SubView(bucketAIndex * maxBucketItems, maxBucketItems);
                var bValues = tempStage.SubView(bucketBIndex * maxBucketItems, maxBucketItems);

                RadixSort512_15_Pass3(idx, totalBucketAItems, bucketACounts, aValues, bucketASValues);

                Warp.Barrier();

                RadixSort512_15_Pass3(idx, totalBucketBItems, bucketBCounts, bValues, bucketBSValues);

                Group.Barrier();


                //Buckets have no issues


                //8 loops
                //16 items per bucket

                for (int z = idx; z < smallBucketItems; z += BlockSize)
                {
                    int innerBucketA = z / sharedBucketItems;
                    int innerBucketAIndex = z & (sharedBucketItems - 1);

                    int inverseBucketB = -innerBucketA & 0x7F;

                    inverseBucketB = i != 0 ? (inverseBucketB - 1) & 0x7F : inverseBucketB;

                    var bucketACount = Math.Min(sharedBucketItems, bucketACounts[innerBucketA]);
                    var bucketBCount = Math.Min(sharedBucketItems, bucketBCounts[inverseBucketB]);

                    if (bucketACount > innerBucketAIndex)
                    {
                        var bucketAValue = bucketASValues[z];
                        var bucketAIndice = (int)(bucketAValue >> 32);

                        for (int x = 0; x < bucketBCount; x++)
                        {
                            var bucketBValue = bucketBSValues[inverseBucketB * sharedBucketItems + x];
                            var bucketBIndice = (int)(bucketBValue >> 32);
                            var combined = bucketAValue + bucketBValue;

                            if ((combined & ((1ul << 30) - 1)) == 0)
                            {
                                //Grab indices
                                var stage2A = values[bucketAIndice] >> 32;
                                var stage2B = values[bucketBIndice] >> 32;

                                //Interop.WriteLine("{0} {1} | {2} {3}", stage2A, stage2B, stage2A2, stage2B2);
                                var stage2A_indexA = stage2A & 0xFFFF;
                                var stage2A_indexB = stage2A >> 16;
                                var stage2B_indexA = stage2B & 0xFFFF;
                                var stage2B_indexB = stage2B >> 16;

                                var stage1AA = stage1Indices[(int)stage2A_indexA];
                                var stage1AB = stage1Indices[(int)stage2A_indexB];
                                var stage1BA = stage1Indices[(int)stage2B_indexA];
                                var stage1BB = stage1Indices[(int)stage2B_indexB];


                                var index0 = stage1AA & 0xFFFF;
                                var index1 = stage1AA >> 16;


                                var index2 = stage1AB & 0xFFFF;
                                var index3 = stage1AB >> 16;


                                var index4 = stage1BA & 0xFFFF;
                                var index5 = stage1BA >> 16;


                                var index6 = stage1BB & 0xFFFF;
                                var index7 = stage1BB >> 16;

                                var solutionIndex = Atomic.Add(ref solutionCount[0], 1);

                                //Max of 8 solutions
                                solutionIndex = Math.Min(7, solutionIndex);
                                solutions[(int)solutionIndex] = new EquixSolution
                                {
                                    V0 = (ushort)index0,
                                    V1 = (ushort)index1,
                                    V2 = (ushort)index2,
                                    V3 = (ushort)index3,
                                    V4 = (ushort)index4,
                                    V5 = (ushort)index5,
                                    V6 = (ushort)index6,
                                    V7 = (ushort)index7
                                };
                            }
                        }
                    }
                }

                Group.Barrier();
            }

            #endregion

            Group.Barrier();

            int totalValues = Math.Min(TotalValues, globalMemoryIndex[0]);

            return totalValues;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RadixSort512_15_Pass3(int idx, int max, ArrayView<int> counts, ArrayView<ulong> values, ArrayView<ulong> output)
        {
            //Grab bucket
            {
                ulong value = values[idx];

                int bucket = (int)((value >> 8) & 0x7F);

                if (idx < max)
                {
                    //Scatter
                    int loc = Atomic.Add(ref counts[bucket], 1);

                    loc = Math.Min(sharedBucketItems - 1, loc);

                    output[bucket * sharedBucketItems + loc] = value;
                }
            }
        }

        #endregion
    }
}