using System;
using System.IO;

namespace CUETools.Codecs.FLAKE
{
    [AudioDecoderClass("libFlake", "flac")]
	public class FlakeReader: IAudioSource, IDisposable
	{
        readonly int[] residualBuffer;

		byte[] _framesBuffer;
		int _framesBufferLength = 0, _framesBufferOffset = 0;
		long first_frame_offset;

		SeekPoint[] seek_table;

		Crc8 crc8;
		Crc16 crc16;
		FlacFrame frame;
		BitReader framereader;
        uint min_block_size = 0;
		uint max_block_size = 0;
		uint min_frame_size = 0;
		uint max_frame_size = 0;

		int _samplesInBuffer, _samplesBufferOffset;
		long _sampleOffset = 0;
        Stream _IO;

        public bool DoCRC { get; set; } = true;

        public int[] Samples { get; }

        public FlakeReader(string path, Stream IO)
		{
			Path = path;
			_IO = IO ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0x10000);

			crc8 = new Crc8();
			crc16 = new Crc16();

			_framesBuffer = new byte[0x20000];
			Decode_metadata();

			frame = new FlacFrame(PCM.ChannelCount);
			framereader = new BitReader();

			//max_frame_size = 16 + ((Flake.MAX_BLOCKSIZE * PCM.BitsPerSample * PCM.ChannelCount + 1) + 7) >> 3);
			if (((int)max_frame_size * PCM.BitsPerSample * PCM.ChannelCount * 2 >> 3) > _framesBuffer.Length)
			{
				byte[] temp = _framesBuffer;
				_framesBuffer = new byte[((int)max_frame_size * PCM.BitsPerSample * PCM.ChannelCount * 2 >> 3)];
				if (_framesBufferLength > 0)
					Array.Copy(temp, _framesBufferOffset, _framesBuffer, 0, _framesBufferLength);
				_framesBufferOffset = 0;
			}
			_samplesInBuffer = 0;

			//if ((PCM.BitsPerSample != 16 && PCM.BitsPerSample != 24) || PCM.ChannelCount != 2 || (PCM.SampleRate != 44100 && PCM.SampleRate != 48000))
			//	throw new Exception("invalid flac file");

			Samples = new int[Flake.MAX_BLOCKSIZE * PCM.ChannelCount];
			residualBuffer = new int[Flake.MAX_BLOCKSIZE * PCM.ChannelCount];
		}

		public FlakeReader(AudioPCMConfig _pcm)
		{
			PCM = _pcm;
			crc8 = new Crc8();
			crc16 = new Crc16();

			Samples = new int[Flake.MAX_BLOCKSIZE * PCM.ChannelCount];
			residualBuffer = new int[Flake.MAX_BLOCKSIZE * PCM.ChannelCount];
			frame = new FlacFrame(PCM.ChannelCount);
			framereader = new BitReader();
		}

		public void Close()
		{
			_IO.Close();
		}

        public long Length { get; private set; } = 0;

        public long Remaining
		{
			get
			{
				return Length - Position;
			}
		}

		public long Position
		{
			get
			{
				return _sampleOffset - _samplesInBuffer;
			}
			set
			{
				if (value > Length)
					throw new Exception("seeking past end of stream");
				if (value < Position || value > _sampleOffset)
				{
					if (seek_table != null && _IO.CanSeek)
					{
						int best_st = -1;
						for (int st = 0; st < seek_table.Length; st++)
						{
							if (seek_table[st].number <= value &&
								(best_st == -1 || seek_table[st].number > seek_table[best_st].number))
								best_st = st;
						}
						if (best_st != -1)
						{
							_framesBufferLength = 0;
							_samplesInBuffer = 0;
                            _samplesBufferOffset = 0;
							_IO.Position = (long)seek_table[best_st].offset + first_frame_offset;
							_sampleOffset = seek_table[best_st].number;
						}
					}
					if (value < Position)
						throw new Exception("cannot seek backwards without seek table");
				}
				while (value > _sampleOffset)
				{
					_samplesInBuffer = 0;
					_samplesBufferOffset = 0;

					Fill_frames_buffer();
					if (_framesBufferLength == 0)
						throw new Exception("seek failed");

					int bytesDecoded = DecodeFrame(_framesBuffer, _framesBufferOffset, _framesBufferLength);
					_framesBufferLength -= bytesDecoded;
					_framesBufferOffset += bytesDecoded;

					_sampleOffset += _samplesInBuffer;
				};
				int diff = _samplesInBuffer - (int)(_sampleOffset - value);
				_samplesInBuffer -= diff;
				_samplesBufferOffset += diff;
			}
		}

        public AudioPCMConfig PCM { get; private set; }

        public string Path { get; }

        unsafe void Interlace(AudioBuffer buff, int offset, int count)
		{
			if (PCM.ChannelCount == 2)
			{
				fixed (int* src = &Samples[_samplesBufferOffset])
					buff.Interlace(offset, src, src + Flake.MAX_BLOCKSIZE, count);
			}
			else
			{
				for (int ch = 0; ch < PCM.ChannelCount; ch++)
					fixed (int* res = &buff.Samples[offset, ch], src = &Samples[_samplesBufferOffset + ch * Flake.MAX_BLOCKSIZE])
					{
						int* psrc = src;
						for (int i = 0; i < count; i++)
							res[i + i] = *(psrc++);
					}
			}
		}

		public int Read(AudioBuffer buff, int maxLength)
		{
			buff.Prepare(this, maxLength);

			int offset = 0;
			int sampleCount = buff.Length;

			while (_samplesInBuffer < sampleCount)
			{
				if (_samplesInBuffer > 0)
				{
					Interlace(buff, offset, _samplesInBuffer);
					sampleCount -= _samplesInBuffer;
					offset += _samplesInBuffer;
					_samplesInBuffer = 0;
					_samplesBufferOffset = 0;
				}

				Fill_frames_buffer();

				if (_framesBufferLength == 0)
				{
					buff.Length = offset;
					return offset;
				}

				int bytesDecoded = DecodeFrame(_framesBuffer, _framesBufferOffset, _framesBufferLength);
				_framesBufferLength -= bytesDecoded;
				_framesBufferOffset += bytesDecoded;

				_samplesInBuffer -= _samplesBufferOffset; // can be set by Seek, otherwise zero
				_sampleOffset += _samplesInBuffer;
			}

			Interlace(buff, offset, sampleCount);
			_samplesInBuffer -= sampleCount;
			_samplesBufferOffset += sampleCount;
			if (_samplesInBuffer == 0)
				_samplesBufferOffset = 0;
			return offset + sampleCount;
		}

		unsafe void Fill_frames_buffer()
		{
			if (_framesBufferLength == 0)
				_framesBufferOffset = 0;
			else if (_framesBufferLength < _framesBuffer.Length / 2 && _framesBufferOffset >= _framesBuffer.Length / 2)
			{
				fixed (byte* buff = _framesBuffer)
					AudioSamples.MemCpy(buff, buff + _framesBufferOffset, _framesBufferLength);
				_framesBufferOffset = 0;
			}
			while (_framesBufferLength < _framesBuffer.Length / 2)
			{
				int read = _IO.Read(_framesBuffer, _framesBufferOffset + _framesBufferLength, _framesBuffer.Length - _framesBufferOffset - _framesBufferLength);
				_framesBufferLength += read;
				if (read == 0)
					break;
			}
		}

		unsafe void Decode_frame_header(BitReader bitreader, FlacFrame frame)
		{
			int header_start = bitreader.Position;

			if (bitreader.Readbits(15) != 0x7FFC)
				throw new Exception("invalid frame");
			uint vbs = bitreader.Readbit();
			frame.bs_code0 = (int) bitreader.Readbits(4);
			uint sr_code0 = bitreader.Readbits(4);
			frame.ch_mode = (ChannelMode)bitreader.Readbits(4);
			uint bps_code = bitreader.Readbits(3);
			if (Flake.flac_bitdepths[bps_code] != PCM.BitsPerSample)
				throw new Exception("unsupported bps coding");
			uint t1 = bitreader.Readbit(); // == 0?????
			if (t1 != 0)
				throw new Exception("unsupported frame coding");
			frame.frame_number = (int)bitreader.Read_utf8();

			// custom block size
			if (frame.bs_code0 == 6)
			{
				frame.bs_code1 = (int)bitreader.Readbits(8);
				frame.blocksize = frame.bs_code1 + 1;
			}
			else if (frame.bs_code0 == 7)
			{
				frame.bs_code1 = (int)bitreader.Readbits(16);
				frame.blocksize = frame.bs_code1 + 1;
			}
			else
				frame.blocksize = Flake.flac_blocksizes[frame.bs_code0];

			// custom sample rate
			if (sr_code0 < 4 || sr_code0 > 11)
			{
				// sr_code0 == 12 -> sr == bitreader.readbits(8) * 1000;
				// sr_code0 == 13 -> sr == bitreader.readbits(16);
				// sr_code0 == 14 -> sr == bitreader.readbits(16) * 10;
				throw new Exception("invalid sample rate mode");
			}

			int frame_channels = (int)frame.ch_mode + 1;
			if (frame_channels > 11)
				throw new Exception("invalid channel mode");
			if (frame_channels == 2 || frame_channels > 8) // Mid/Left/Right Side Stereo
				frame_channels = 2;
			else
				frame.ch_mode = ChannelMode.NotStereo;
			if (frame_channels != PCM.ChannelCount)
				throw new Exception("invalid channel mode");

			// CRC-8 of frame header
			byte crc = DoCRC ? crc8.ComputeChecksum(bitreader.Buffer, header_start, bitreader.Position - header_start) : (byte)0;
			frame.crc8 = (byte)bitreader.Readbits(8);
			if (DoCRC && frame.crc8 != crc)
				throw new Exception("header crc mismatch");
		}

		unsafe void Decode_subframe_constant(BitReader bitreader, FlacFrame frame, int ch)
		{
			int obits = frame.subframes[ch].obits;
			frame.subframes[ch].best.residual[0] = bitreader.Readbits_signed(obits);
		}

		unsafe void Decode_subframe_verbatim(BitReader bitreader, FlacFrame frame, int ch)
		{
			int obits = frame.subframes[ch].obits;
			for (int i = 0; i < frame.blocksize; i++)
				frame.subframes[ch].best.residual[i] = bitreader.Readbits_signed(obits);
		}

		unsafe void Decode_residual(BitReader bitreader, FlacFrame frame, int ch)
		{
			// rice-encoded block
			// coding method
			frame.subframes[ch].best.rc.coding_method = (int)bitreader.Readbits(2); // ????? == 0
			if (frame.subframes[ch].best.rc.coding_method != 0 && frame.subframes[ch].best.rc.coding_method != 1)
				throw new Exception("unsupported residual coding");
			// partition order
			frame.subframes[ch].best.rc.porder = (int)bitreader.Readbits(4);
			if (frame.subframes[ch].best.rc.porder > 8)
				throw new Exception("invalid partition order");
			int psize = frame.blocksize >> frame.subframes[ch].best.rc.porder;
			int res_cnt = psize - frame.subframes[ch].best.order;

			int rice_len = 4 + frame.subframes[ch].best.rc.coding_method;
			// residual
			int j = frame.subframes[ch].best.order;
			int* r = frame.subframes[ch].best.residual + j;
			for (int p = 0; p < (1 << frame.subframes[ch].best.rc.porder); p++)
			{
				if (p == 1) res_cnt = psize;
				int n = Math.Min(res_cnt, frame.blocksize - j);

				int k = frame.subframes[ch].best.rc.rparams[p] = (int)bitreader.Readbits(rice_len);
				if (k == (1 << rice_len) - 1)
				{
					k = frame.subframes[ch].best.rc.esc_bps[p] = (int)bitreader.Readbits(5);
					for (int i = n; i > 0; i--)
						*(r++) = bitreader.Readbits_signed((int)k);
				}
				else
				{
					bitreader.Read_rice_block(n, (int)k, r);
					r += n;
				}
				j += n;
			}
		}

		unsafe void Decode_subframe_fixed(BitReader bitreader, FlacFrame frame, int ch)
		{
			// warm-up samples
			int obits = frame.subframes[ch].obits;
			for (int i = 0; i < frame.subframes[ch].best.order; i++)
				frame.subframes[ch].best.residual[i] = bitreader.Readbits_signed(obits);

			// residual
			Decode_residual(bitreader, frame, ch);
		}

		unsafe void Decode_subframe_lpc(BitReader bitreader, FlacFrame frame, int ch)
		{
			// warm-up samples
			int obits = frame.subframes[ch].obits;
			for (int i = 0; i < frame.subframes[ch].best.order; i++)
				frame.subframes[ch].best.residual[i] = bitreader.Readbits_signed(obits);

			// LPC coefficients
			frame.subframes[ch].best.cbits = (int)bitreader.Readbits(4) + 1; // lpc_precision
			frame.subframes[ch].best.shift = bitreader.Readbits_signed(5);
			if (frame.subframes[ch].best.shift < 0)
				throw new Exception("negative shift");
			for (int i = 0; i < frame.subframes[ch].best.order; i++)
				frame.subframes[ch].best.coefs[i] = bitreader.Readbits_signed(frame.subframes[ch].best.cbits);

			// residual
			Decode_residual(bitreader, frame, ch);
		}

		unsafe void Decode_subframes(BitReader bitreader, FlacFrame frame)
		{
			fixed (int *r = residualBuffer, s = Samples)
				for (int ch = 0; ch < PCM.ChannelCount; ch++)
			{
				// subframe header
				uint t1 = bitreader.Readbit(); // ?????? == 0
				if (t1 != 0)
					throw new Exception("unsupported subframe coding (ch == " + ch.ToString() + ")");
				int type_code = (int)bitreader.Readbits(6);
				frame.subframes[ch].wbits = (int)bitreader.Readbit();
				if (frame.subframes[ch].wbits != 0)
					frame.subframes[ch].wbits += (int)bitreader.Read_unary();

				frame.subframes[ch].obits = PCM.BitsPerSample - frame.subframes[ch].wbits;
				switch (frame.ch_mode)
				{
					case ChannelMode.MidSide: frame.subframes[ch].obits += ch; break;
					case ChannelMode.LeftSide: frame.subframes[ch].obits += ch; break;
					case ChannelMode.RightSide: frame.subframes[ch].obits += 1 - ch; break;
				}

				frame.subframes[ch].best.type = (SubframeType)type_code;
				frame.subframes[ch].best.order = 0;

				if ((type_code & (uint)SubframeType.LPC) != 0)
				{
					frame.subframes[ch].best.order = (type_code - (int)SubframeType.LPC) + 1;
					frame.subframes[ch].best.type = SubframeType.LPC;
				}
				else if ((type_code & (uint)SubframeType.Fixed) != 0)
				{
					frame.subframes[ch].best.order = (type_code - (int)SubframeType.Fixed);
					frame.subframes[ch].best.type = SubframeType.Fixed;
				}

				frame.subframes[ch].best.residual = r + ch * Flake.MAX_BLOCKSIZE;
				frame.subframes[ch].samples = s + ch * Flake.MAX_BLOCKSIZE;

				// subframe
				switch (frame.subframes[ch].best.type)
				{
					case SubframeType.Constant:
						Decode_subframe_constant(bitreader, frame, ch);
						break;
					case SubframeType.Verbatim:
						Decode_subframe_verbatim(bitreader, frame, ch);
						break;
					case SubframeType.Fixed:
						Decode_subframe_fixed(bitreader, frame, ch);
						break;
					case SubframeType.LPC:
						Decode_subframe_lpc(bitreader, frame, ch);
						break;
					default:
						throw new Exception("invalid subframe type");
				}
			}
		}

		unsafe void Restore_samples_fixed(FlacFrame frame, int ch)
		{
			FlacSubframeInfo sub = frame.subframes[ch];

			AudioSamples.MemCpy(sub.samples, sub.best.residual, sub.best.order);
			int* data = sub.samples + sub.best.order;
			int* residual = sub.best.residual + sub.best.order;
			int data_len = frame.blocksize - sub.best.order;
			int s0, s1, s2;
			switch (sub.best.order)
			{
				case 0:
					AudioSamples.MemCpy(data, residual, data_len);
					break;
				case 1:
					s1 = data[-1];
					for (int i = data_len; i > 0; i--)
					{
						s1 += *(residual++);
						*(data++) = s1;
					}
					//data[i] = residual[i] + data[i - 1];
					break;
				case 2:
					s2 = data[-2];
					s1 = data[-1];
					for (int i = data_len; i > 0; i--)
					{
						s0 = *(residual++) + (s1 << 1) - s2;
						*(data++) = s0;
						s2 = s1;
						s1 = s0;
					}
					//data[i] = residual[i] + data[i - 1] * 2  - data[i - 2];
					break;
				case 3:
					for (int i = 0; i < data_len; i++)
						data[i] = residual[i] + (((data[i - 1] - data[i - 2]) << 1) + (data[i - 1] - data[i - 2])) + data[i - 3];
					break;
				case 4:
					for (int i = 0; i < data_len; i++)
						data[i] = residual[i] + ((data[i - 1] + data[i - 3]) << 2) - ((data[i - 2] << 2) + (data[i - 2] << 1)) - data[i - 4];
					break;
			}
		}

		unsafe void Restore_samples_lpc(FlacFrame frame, int ch)
		{
			FlacSubframeInfo sub = frame.subframes[ch];
			ulong csum = 0;
			fixed (int* coefs = sub.best.coefs)
			{
				for (int i = sub.best.order; i > 0; i--)
					csum += (ulong)Math.Abs(coefs[i - 1]);
				if ((csum << sub.obits) >= 1UL << 32)
					Lpc.Decode_residual_long(sub.best.residual, sub.samples, frame.blocksize, sub.best.order, coefs, sub.best.shift);
				else
					Lpc.Decode_residual(sub.best.residual, sub.samples, frame.blocksize, sub.best.order, coefs, sub.best.shift);
			}
		}

		unsafe void Restore_samples(FlacFrame frame)
		{
			for (int ch = 0; ch < PCM.ChannelCount; ch++)
			{
				switch (frame.subframes[ch].best.type)
				{
					case SubframeType.Constant:
						AudioSamples.MemSet(frame.subframes[ch].samples, frame.subframes[ch].best.residual[0], frame.blocksize);
						break;
					case SubframeType.Verbatim:
						AudioSamples.MemCpy(frame.subframes[ch].samples, frame.subframes[ch].best.residual, frame.blocksize);
						break;
					case SubframeType.Fixed:
						Restore_samples_fixed(frame, ch);
						break;
					case SubframeType.LPC:
						Restore_samples_lpc(frame, ch);
						break;
				}
				if (frame.subframes[ch].wbits != 0)
				{
					int* s = frame.subframes[ch].samples;
					int x = (int) frame.subframes[ch].wbits;
					for (int i = frame.blocksize; i > 0; i--)
						*(s++) <<= x;
				}
			}
			if (frame.ch_mode != ChannelMode.NotStereo)
			{
				int* l = frame.subframes[0].samples;
				int* r = frame.subframes[1].samples;
				switch (frame.ch_mode)
				{
					case ChannelMode.LeftRight:
						break;
					case ChannelMode.MidSide:
						for (int i = frame.blocksize; i > 0; i--)
						{
							int mid = *l;
							int side = *r;
							mid <<= 1;
							mid |= (side & 1); /* i.e. if 'side' is odd... */
							*(l++) = (mid + side) >> 1;
							*(r++) = (mid - side) >> 1;
						}
						break;
					case ChannelMode.LeftSide:
						for (int i = frame.blocksize; i > 0; i--)
						{
							int _l = *(l++), _r = *r;
							*(r++) = _l - _r;
						}
						break;
					case ChannelMode.RightSide:
						for (int i = frame.blocksize; i > 0; i--)
							*(l++) += *(r++);
						break;
				}
			}
		}

		public unsafe int DecodeFrame(byte[] buffer, int pos, int len)
		{
			fixed (byte* buf = buffer)
			{
				framereader.Reset(buf, pos, len);
				Decode_frame_header(framereader, frame);
				Decode_subframes(framereader, frame);
				framereader.Flush();
				ushort crc_1 = DoCRC ? crc16.ComputeChecksum(framereader.Buffer + pos, framereader.Position - pos) : (ushort)0;
				ushort crc_2 = (ushort)framereader.Readbits(16);
				if (DoCRC && crc_1 != crc_2)
					throw new Exception("frame crc mismatch");
				Restore_samples(frame);
				_samplesInBuffer = frame.blocksize;
				return framereader.Position - pos;
			}
		}


		bool Skip_bytes(int bytes)
		{
			for (int j = 0; j < bytes; j++)
				if (0 == _IO.Read(_framesBuffer, 0, 1))
					return false;
			return true;
		}

		unsafe void Decode_metadata()
		{
			byte x;
			int i, id;
			byte[] FLAC__STREAM_SYNC_STRING = new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C' };
			byte[] ID3V2_TAG_ = new byte[] { (byte)'I', (byte)'D', (byte)'3' };

			for (i = id = 0; i < 4; )
			{
				if (_IO.Read(_framesBuffer, 0, 1) == 0)
					throw new Exception("FLAC stream not found");
				x = _framesBuffer[0];
				if (x == FLAC__STREAM_SYNC_STRING[i])
				{
                    i++;
					id = 0;
					continue;
				}
				if (id < 3 && x == ID3V2_TAG_[id])
				{
					id++;
					i = 0;
					if (id == 3)
					{
						if (!Skip_bytes(3))
							throw new Exception("FLAC stream not found");
						int skip = 0;
						for (int j = 0; j < 4; j++)
						{
							if (0 == _IO.Read(_framesBuffer, 0, 1))
								throw new Exception("FLAC stream not found");
							skip <<= 7;
							skip |= ((int)_framesBuffer[0] & 0x7f);
						}
						if (!Skip_bytes(skip))
							throw new Exception("FLAC stream not found");
					}
					continue;
				}
				id = 0;
				if (x == 0xff) /* MAGIC NUMBER for the first 8 frame sync bits */
				{
					do
					{
						if (_IO.Read(_framesBuffer, 0, 1) == 0)
							throw new Exception("FLAC stream not found");
						x = _framesBuffer[0];
					} while (x == 0xff);
					if (x >> 2 == 0x3e) /* MAGIC NUMBER for the last 6 sync bits */
					{
						//_IO.Position -= 2;
						// state = frame
						throw new Exception("headerless file unsupported");
					}
				}
				throw new Exception("FLAC stream not found");
			}

			do
			{
				Fill_frames_buffer();
				fixed (byte* buf = _framesBuffer)
				{
					BitReader bitreader = new BitReader(buf, _framesBufferOffset, _framesBufferLength - _framesBufferOffset);
					bool is_last = bitreader.Readbit() != 0;
					MetadataType type = (MetadataType)bitreader.Readbits(7);
					int len = (int)bitreader.Readbits(24);

					if (type == MetadataType.StreamInfo)
					{
						const int FLAC__STREAM_METADATA_STREAMINFO_MIN_BLOCK_SIZE_LEN = 16; /* bits */
						const int FLAC__STREAM_METADATA_STREAMINFO_MAX_BLOCK_SIZE_LEN = 16; /* bits */
						const int FLAC__STREAM_METADATA_STREAMINFO_MIN_FRAME_SIZE_LEN = 24; /* bits */
						const int FLAC__STREAM_METADATA_STREAMINFO_MAX_FRAME_SIZE_LEN = 24; /* bits */
						const int FLAC__STREAM_METADATA_STREAMINFO_SAMPLE_RATE_LEN = 20; /* bits */
						const int FLAC__STREAM_METADATA_STREAMINFO_CHANNELS_LEN = 3; /* bits */
						const int FLAC__STREAM_METADATA_STREAMINFO_BITS_PER_SAMPLE_LEN = 5; /* bits */
						const int FLAC__STREAM_METADATA_STREAMINFO_TOTAL_SAMPLES_LEN = 36; /* bits */
						const int FLAC__STREAM_METADATA_STREAMINFO_MD5SUM_LEN = 128; /* bits */

						min_block_size = bitreader.Readbits(FLAC__STREAM_METADATA_STREAMINFO_MIN_BLOCK_SIZE_LEN);
						max_block_size = bitreader.Readbits(FLAC__STREAM_METADATA_STREAMINFO_MAX_BLOCK_SIZE_LEN);
						min_frame_size = bitreader.Readbits(FLAC__STREAM_METADATA_STREAMINFO_MIN_FRAME_SIZE_LEN);
						max_frame_size = bitreader.Readbits(FLAC__STREAM_METADATA_STREAMINFO_MAX_FRAME_SIZE_LEN);
						int sample_rate = (int)bitreader.Readbits(FLAC__STREAM_METADATA_STREAMINFO_SAMPLE_RATE_LEN);
						int channels = 1 + (int)bitreader.Readbits(FLAC__STREAM_METADATA_STREAMINFO_CHANNELS_LEN);
						int bits_per_sample = 1 + (int)bitreader.Readbits(FLAC__STREAM_METADATA_STREAMINFO_BITS_PER_SAMPLE_LEN);
						PCM = new AudioPCMConfig(bits_per_sample, channels, sample_rate);
						Length = (long)bitreader.Readbits64(FLAC__STREAM_METADATA_STREAMINFO_TOTAL_SAMPLES_LEN);
						bitreader.Skipbits(FLAC__STREAM_METADATA_STREAMINFO_MD5SUM_LEN);
					}
					else if (type == MetadataType.Seektable)
					{
						int num_entries = len / 18;
						seek_table = new SeekPoint[num_entries];
						for (int e = 0; e < num_entries; e++)
						{
							seek_table[e].number = (long)bitreader.Readbits64(Flake.FLAC__STREAM_METADATA_SEEKPOINT_SAMPLE_NUMBER_LEN);
							seek_table[e].offset = (long)bitreader.Readbits64(Flake.FLAC__STREAM_METADATA_SEEKPOINT_STREAM_OFFSET_LEN);
							seek_table[e].framesize = (int)bitreader.Readbits24(Flake.FLAC__STREAM_METADATA_SEEKPOINT_FRAME_SAMPLES_LEN);
						}
					}
					if (_framesBufferLength < 4 + len)
					{
						_IO.Position += 4 + len - _framesBufferLength;
						_framesBufferLength = 0;
					}
					else
					{
						_framesBufferLength -= 4 + len;
						_framesBufferOffset += 4 + len;
					}
					if (is_last)
						break;
				}
			} while (true);
			first_frame_offset = _IO.Position - _framesBufferLength;
		}

        public void Dispose()
        {
            Close();
        }
    }
}
