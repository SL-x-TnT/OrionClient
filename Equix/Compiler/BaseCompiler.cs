﻿namespace Equix.Compiler
{
    public class BaseCompiler
    {
        public static readonly uint CodeSize = (uint)AlignSize(HashxProgramMaxSize * CompAvgInstrSize + CompReserveSize, CompPageSize);

        protected const int HashxProgramMaxSize = 512;
        protected const int CompReserveSize = 1024 * 25;
        protected const int CompAvgInstrSize = 8;
        protected const int CompPageSize = 4096;

        protected static int AlignSize(int pos, int align)
        {
            return ((((pos) - 1) / (align) + 1) * (align));
        }

    }
}
