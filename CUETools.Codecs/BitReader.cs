using System;

namespace CUETools.Codecs
{
	unsafe public class BitReader
    {
        #region Static Methods

        public static int Log2i(int v)
        {
            return Log2i((uint)v);
        }

        public static int Log2i(ulong v)
        {
            int n = 0;
            if (0 != (v & 0xffffffff00000000)) { v >>= 32; n += 32; }
            if (0 != (v & 0xffff0000)) { v >>= 16; n += 16; }
            if (0 != (v & 0xff00)) { v >>= 8; n += 8; }
            return n + byte_to_log2_table[v];
        }

        public static int Log2i(uint v)
        {
            int n = 0;
            if (0 != (v & 0xffff0000)) { v >>= 16; n += 16; }
            if (0 != (v & 0xff00)) { v >>= 8; n += 8; }
            return n + byte_to_log2_table[v];
        }

        public static readonly byte[] byte_to_unary_table = new byte[]
		{
			8, 7, 6, 6, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4,
			3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
			2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
			2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
			1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
		};

        public static readonly byte[] byte_to_log2_table = new byte[]
		{
			0,0,1,1,2,2,2,2,3,3,3,3,3,3,3,3,
			4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,
			5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,
			5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,
			6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
			6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
			6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
			6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
			7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
			7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
			7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
			7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
			7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
			7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
			7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
			7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7
		};

        #endregion
        private int len;
        private int _bitaccumulator;
        private uint cache;

        public int Position { get; private set; }

        public byte* Buffer { get; private set; }

        public BitReader()
		{
			Buffer = null;
			Position = 0;
			len = 0;
			_bitaccumulator = 0;
			cache = 0;
		}

		public BitReader(byte* _buffer, int _pos, int _len)
		{
			Reset(_buffer, _pos, _len);
		}

		public void Reset(byte* _buffer, int _pos, int _len)
		{
			Buffer = _buffer;
			Position = _pos;
			len = _len;
			_bitaccumulator = 0;
			cache = Peek4();
		}

		public uint Peek4()
		{
			//uint result = ((((uint)buffer[pos]) << 24) | (((uint)buffer[pos + 1]) << 16) | (((uint)buffer[pos + 2]) << 8) | ((uint)buffer[pos + 3])) << _bitaccumulator;
			byte* b = Buffer + Position;
			uint result = *(b++);
			result = (result << 8) + *(b++);
			result = (result << 8) + *(b++);
			result = (result << 8) + *(b++);
			result <<= _bitaccumulator;
			return result;
		}

		/* skip any number of bits */
		public void Skipbits(int bits)
		{
			int new_accumulator = (_bitaccumulator + bits);
			Position += (new_accumulator >> 3);
			_bitaccumulator = (new_accumulator & 7);
			cache = Peek4();
		}

		/* skip up to 16 bits */
		public void Skipbits16(int bits)
		{
			cache <<= bits;
			int new_accumulator = (_bitaccumulator + bits);
			Position += (new_accumulator >> 3);
			_bitaccumulator = (new_accumulator & 7);
			cache |= ((((uint)Buffer[Position + 2] << 8) + (uint)Buffer[Position + 3]) << _bitaccumulator);
		}

		/* skip up to 8 bits */
		public void Skipbits8(int bits)
		{
			cache <<= bits;
			int new_accumulator = (_bitaccumulator + bits);
			Position += (new_accumulator >> 3);
			_bitaccumulator = (new_accumulator & 7);
			cache |= ((uint)Buffer[Position + 3] << _bitaccumulator);
		}

		/* supports reading 1 to 24 bits, in big endian format */
		public uint Readbits24(int bits)
		{
			//uint result = peek4() >> (32 - bits);
			uint result = cache >> (32 - bits);
			Skipbits(bits);
			return result;
		}

		public uint Peekbits24(int bits)
		{
			return cache >> 32 - bits;
		}

		/* supports reading 1 to 32 bits, in big endian format */
		public uint Readbits(int bits)
		{
			uint result = cache >> 32 - bits;
			if (bits <= 24)
			{
				Skipbits(bits);
				return result;
			}
			Skipbits(24);
			result |= cache >> 56 - bits;
			Skipbits(bits - 24);
			return result;
		}

		public ulong Readbits64(int bits)
		{
			if (bits <= 24)
				return Readbits24(bits);
			ulong result = Readbits24(24);
			bits -= 24;
			if (bits <= 24)
				return (result << bits) | Readbits24(bits);
			result = (result << 24) | Readbits24(24);
			bits -= 24;
			return (result << bits) | Readbits24(bits);
		}

		/* reads a single bit */
		public uint Readbit()
		{
			uint result = cache >> 31;
			Skipbits8(1);
			return result;
		}

		public uint Read_unary()
		{
			uint val = 0;

			uint result = cache >> 24;
			while (result == 0)
			{
				val += 8;
				Skipbits8(8);
				result = cache >> 24;
			}

			val += byte_to_unary_table[result];
			Skipbits8((int)(val & 7) + 1);
			return val;
		}

		public void Flush()
		{
			if (_bitaccumulator > 0)
				Skipbits8(8 - _bitaccumulator);
		}

		public int Readbits_signed(int bits)
		{
			int val = (int)Readbits(bits);
			val <<= (32 - bits);
			val >>= (32 - bits);
			return val;
		}

		public uint Read_utf8()
		{
			uint x = Readbits(8);
			uint v;
			int i;
			if (0 == (x & 0x80))
			{
				v = x;
				i = 0;
			}
			else if (0xC0 == (x & 0xE0)) /* 110xxxxx */
			{
				v = x & 0x1F;
				i = 1;
			}
			else if (0xE0 == (x & 0xF0)) /* 1110xxxx */
			{
				v = x & 0x0F;
				i = 2;
			}
			else if (0xF0 == (x & 0xF8)) /* 11110xxx */
			{
				v = x & 0x07;
				i = 3;
			}
			else if (0xF8 == (x & 0xFC)) /* 111110xx */
			{
				v = x & 0x03;
				i = 4;
			}
			else if (0xFC == (x & 0xFE)) /* 1111110x */
			{
				v = x & 0x01;
				i = 5;
			}
            else if (0xFE == x) /* 11111110 */
            {
                v = 0;
                i = 6;
            }
            else
            {
                throw new Exception("invalid utf8 encoding");
            }
			for (; i > 0; i--)
			{
				x = Readbits(8);
				if (0x80 != (x & 0xC0))  /* 10xxxxxx */
					throw new Exception("invalid utf8 encoding");
				v <<= 6;
				v |= (x & 0x3F);
			}
			return v;
		}

		public int Read_rice_signed(int k)
		{
			uint msbs = Read_unary();
			uint lsbs = Readbits24(k);
			uint uval = (msbs << k) | lsbs;
			return (int)(uval >> 1 ^ -(int)(uval & 1));
		}

		public int Read_unary_signed()
		{
			uint uval = Read_unary();
			return (int)(uval >> 1 ^ -(int)(uval & 1));
		}

		public void Read_rice_block(int n, int k, int* r)
		{
			fixed (byte* unary_table = byte_to_unary_table)
			{
				uint mask = (1U << k) - 1;
                if (k == 0)
                {
                    for (int i = n; i > 0; i--)
                    {
                        *(r++) = Read_unary_signed();
                    }
                }
                else if (k <= 8)
                {
                    for (int i = n; i > 0; i--)
                    {
                        //*(r++) = read_rice_signed((int)k);
                        uint bits = unary_table[cache >> 24];
                        uint msbs = bits;
                        while (bits == 8)
                        {
                            Skipbits8(8);
                            bits = unary_table[cache >> 24];
                            msbs += bits;
                        }
                        int btsk = k + (int)bits + 1;
                        uint uval = (msbs << k) | ((cache >> (32 - btsk)) & mask);
                        Skipbits16(btsk);
                        *(r++) = (int)(uval >> 1 ^ -(int)(uval & 1));
                    }
                }
                else if (k <= 16)
                {
                    for (int i = n; i > 0; i--)
                    {
                        //*(r++) = read_rice_signed((int)k);
                        uint bits = unary_table[cache >> 24];
                        uint msbs = bits;
                        while (bits == 8)
                        {
                            Skipbits8(8);
                            bits = unary_table[cache >> 24];
                            msbs += bits;
                        }
                        int btsk = k + (int)bits + 1;
                        uint uval = (msbs << k) | ((cache >> (32 - btsk)) & mask);
                        Skipbits(btsk);
                        *(r++) = (int)(uval >> 1 ^ -(int)(uval & 1));
                    }
                }
                else
                {
                    for (int i = n; i > 0; i--)
                    {
                        //*(r++) = read_rice_signed((int)k);
                        uint bits = unary_table[cache >> 24];
                        uint msbs = bits;
                        while (bits == 8)
                        {
                            Skipbits8(8);
                            bits = unary_table[cache >> 24];
                            msbs += bits;
                        }
                        Skipbits8((int)(msbs & 7) + 1);
                        uint uval = (msbs << k) | ((cache >> (32 - k)));
                        Skipbits(k);
                        *(r++) = (int)(uval >> 1 ^ -(int)(uval & 1));
                    }
                }
			}
		}
	}
}
