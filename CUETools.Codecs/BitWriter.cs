using System;

namespace CUETools.Codecs
{
    public class BitWriter
    {
        private uint bit_buf;
        private int bit_left;
        private readonly int buf_start;
        private int buf_ptr;
        private readonly int buf_end;
        private bool eof;

        public byte[] Buffer { get; }

        public int Length
        {
            get
            {
                return buf_ptr - buf_start;
            }
            set
            {
                Flush();
                buf_ptr = buf_start + value;
            }
        }

        public int BitLength
        {
            get
            {
                return buf_ptr * 8 + 32 - bit_left;
            }
        }

        public BitWriter(byte[] buf, int pos, int len)
        {
            Buffer = buf;
            buf_start = pos;
            buf_ptr = pos;
            buf_end = pos + len;
            bit_left = 32;
            bit_buf = 0;
            eof = false;
        }

        public void Reset()
        {
            buf_ptr = buf_start;
            bit_left = 32;
            bit_buf = 0;
            eof = false;
        }

        public void Writebytes(int bytes, byte c)
        {
            for (; bytes > 0; bytes--)
            {
                Writebits(8, c);
            }
        }

        public unsafe void Writeints(int len, int pos, byte* buf)
        {
            int old_pos = BitLength;
            int start = old_pos / 8;
            int start1 = pos / 8;
            int end = (old_pos + len) / 8;
            int end1 = (pos + len) / 8;
            Flush();
            byte start_val = old_pos % 8 != 0 ? Buffer[start] : (byte)0;
            fixed (byte* buf1 = &Buffer[0])
                AudioSamples.MemCpy(buf1 + start, buf + start1, end - start);
            Buffer[start] |= start_val;
            buf_ptr = end;
            if ((old_pos + len) % 8 != 0)
                Writebits((old_pos + len) % 8, buf[end1] >> (8 - ((old_pos + len) % 8)));
        }

        public void Write(params char[] chars)
        {
            foreach (char c in chars)
                Writebits(8, (byte)c);
        }

        public void Write(string s)
        {
            for (int i = 0; i < s.Length; i++)
                Writebits(8, (byte)s[i]);
        }

        public void Writebits_signed(int bits, int val)
        {
            Writebits(bits, val & ((1 << bits) - 1));
        }

        public void Writebits_signed(uint bits, int val)
        {
            Writebits((int)bits, val & ((1 << (int)bits) - 1));
        }

        public void Writebits(int bits, int val)
        {
            Writebits(bits, (uint)val);
        }

        public void Writebits(DateTime val)
        {
            TimeSpan span = val.ToUniversalTime() - new DateTime(1904, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            Writebits(32, (uint)span.TotalSeconds);
        }

        public void Writebits64(int bits, ulong val)
        {
            if (bits > 32)
            {
                Writebits(bits - 32, (uint)(val >> 32));
                val &= 0xffffffffL;
                bits = 32;
            }
            Writebits(bits, (uint)val);
        }

        public void Writebits(int bits, uint val)
        {
            //assert(bits == 32 || val < (1U << bits));

            if (bits == 0 || eof) return;
            if ((buf_ptr + 3) >= buf_end)
            {
                eof = true;
                return;
            }
            if (bits < bit_left)
            {
                bit_buf = (bit_buf << bits) | val;
                bit_left -= bits;
            }
            else
            {
                uint bb = 0;
                if (bit_left == 32)
                {
                    //assert(bits == 32);
                    bb = val;
                }
                else
                {
                    bb = (bit_buf << bit_left) | (val >> (bits - bit_left));
                    bit_left += (32 - bits);
                }
                if (Buffer != null)
                {
                    Buffer[buf_ptr + 3] = (byte)(bb & 0xFF); bb >>= 8;
                    Buffer[buf_ptr + 2] = (byte)(bb & 0xFF); bb >>= 8;
                    Buffer[buf_ptr + 1] = (byte)(bb & 0xFF); bb >>= 8;
                    Buffer[buf_ptr + 0] = (byte)(bb & 0xFF);
                }
                buf_ptr += 4;
                bit_buf = val;
            }
        }

        /// <summary>
        /// Assumes there's enough space, buffer != null and bits is in range 1..31
        /// </summary>
        /// <param name="bits"></param>
        /// <param name="val"></param>
//        unsafe void writebits_fast(int bits, uint val, ref byte* buf)
//        {
//#if DEBUG
//            if ((buf_ptr + 3) >= buf_end)
//            {
//                eof = true;
//                return;
//            }
//#endif
//            if (bits < bit_left)
//            {
//                bit_buf = (bit_buf << bits) | val;
//                bit_left -= bits;
//            }
//            else
//            {
//                uint bb = (bit_buf << bit_left) | (val >> (bits - bit_left));
//                bit_left += (32 - bits);

//                *(buf++) = (byte)(bb >> 24);
//                *(buf++) = (byte)(bb >> 16);
//                *(buf++) = (byte)(bb >> 8);
//                *(buf++) = (byte)(bb);

//                bit_buf = val;
//            }
//        }

        public void Write_utf8(int val)
        {
            Write_utf8((uint)val);
        }

        public void Write_utf8(uint val)
        {
            if (val < 0x80)
            {
                Writebits(8, val);
                return;
            }
            int bytes = (BitReader.Log2i(val) + 4) / 5;
            int shift = (bytes - 1) * 6;
            Writebits(8, (256U - (256U >> bytes)) | (val >> shift));
            while (shift >= 6)
            {
                shift -= 6;
                Writebits(8, 0x80 | ((val >> shift) & 0x3F));
            }
        }

        public void Write_unary_signed(int val)
        {
            // convert signed to unsigned
            int v = -2 * val - 1;
            v ^= (v >> 31);

            // write quotient in unary
            int q = v + 1;
            while (q > 31)
            {
                Writebits(31, 0);
                q -= 31;
            }
            Writebits(q, 1);
        }

        public void Write_rice_signed(int k, int val)
        {
            // convert signed to unsigned
            int v = -2 * val - 1;
            v ^= (v >> 31);

            // write quotient in unary
            int q = (v >> k) + 1;
            while (q + k > 31)
            {
                int b = Math.Min(q + k - 31, 31);
                Writebits(b, 0);
                q -= b;
            }

            // write remainder in binary using 'k' bits
            Writebits(k + q, (v & ((1 << k) - 1)) | (1 << k));
        }

        public unsafe void Write_rice_block_signed(byte* fixedbuf, int k, int* residual, int count)
        {
            byte* buf = &fixedbuf[buf_ptr];
            //fixed (byte* fixbuf = &buffer[buf_ptr])
            {
                //byte* buf = fixbuf;
                for (int i = count; i > 0; i--)
                {
                    int v = *(residual++);
                    v = (v << 1) ^ (v >> 31);

                    // write quotient in unary
                    int q = (v >> k) + 1;
                    int bits = k + q;
                    while (bits > 31)
                    {
#if DEBUG
                        if (buf + 3 >= fixedbuf + buf_end)
                        {
                            eof = true;
                            return;
                        }
#endif
                        int b = Math.Min(bits - 31, 31);
                        if (b < bit_left)
                        {
                            bit_buf = (bit_buf << b);
                            bit_left -= b;
                        }
                        else
                        {
                            uint bb = bit_buf << bit_left;
                            bit_buf = 0;
                            bit_left += (32 - b);
                            *(buf++) = (byte)(bb >> 24);
                            *(buf++) = (byte)(bb >> 16);
                            *(buf++) = (byte)(bb >> 8);
                            *(buf++) = (byte)(bb);
                        }
                        bits -= b;
                    }

#if DEBUG
                    if (buf + 3 >= fixedbuf + buf_end)
                    {
                        eof = true;
                        return;
                    }
#endif

                    // write remainder in binary using 'k' bits
                    //writebits_fast(k + q, (uint)((v & ((1 << k) - 1)) | (1 << k)), ref buf);
                    uint val = (uint)((v & ((1 << k) - 1)) | (1 << k));
                    if (bits < bit_left)
                    {
                        bit_buf = (bit_buf << bits) | val;
                        bit_left -= bits;
                    }
                    else
                    {
                        uint bb = (bit_buf << bit_left) | (val >> (bits - bit_left));
                        bit_buf = val;
                        bit_left += (32 - bits);
                        *(buf++) = (byte)(bb >> 24);
                        *(buf++) = (byte)(bb >> 16);
                        *(buf++) = (byte)(bb >> 8);
                        *(buf++) = (byte)(bb);
                    }
                }
                buf_ptr = (int)(buf - fixedbuf);
            }
        }

        public void Flush()
        {
            bit_buf <<= bit_left;
            while (bit_left < 32 && !eof)
            {
                if (buf_ptr >= buf_end)
                {
                    eof = true;
                    break;
                }
                if (Buffer != null)
                    Buffer[buf_ptr] = (byte)(bit_buf >> 24);
                buf_ptr++;
                bit_buf <<= 8;
                bit_left += 8;
            }
            bit_left = 32;
            bit_buf = 0;
        }
    }
}
