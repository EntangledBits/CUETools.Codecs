using System;
using System.ComponentModel;
using System.Text;
using System.IO;
using System.Security.Cryptography;

namespace CUETools.Codecs.FLAKE
{
    public class FlakeWriterSettings
	{
		public FlakeWriterSettings() { DoVerify = false; DoMD5 = true; }
		[DefaultValue(false)]
		[DisplayName("Verify")]
		[SRDescription(typeof(Properties.Resources), "DoVerifyDescription")]
		public bool DoVerify { get; set; }

		[DefaultValue(true)]
		[DisplayName("MD5")]
		[SRDescription(typeof(Properties.Resources), "DoMD5Description")]
		public bool DoMD5 { get; set; }
	}

	[AudioEncoderClass("libFlake", "flac", true, "0 1 2 3 4 5 6 7 8 9 10 11", "7", 4, typeof(FlakeWriterSettings))]
	//[AudioEncoderClass("libFlake nonsub", "flac", true, "9 10 11", "9", 3, typeof(FlakeWriterSettings))]
	public class FlakeWriter : IAudioDest, IDisposable
	{
		Stream _IO = null;
        private readonly int channels;
        private int ch_code;
        private int sr_code0;

        // sample size in bits
        // set by use"audio/x-flac; rate=16000" prior to calling flake_encode_init
        // only 16-bit is currently supported
        int bps_code;

		// total stream samples
		// set by user prior to calling flake_encode_init
		// if 0, stream length is unknown
		int sample_count = -1;

		FlakeEncodeParams eparams;

		// maximum frame size in bytes
		// set by flake_encode_init
		// this can be used to allocate memory for output
		int max_frame_size;

		byte[] frame_buffer = null;

		int frame_count = 0;

		long first_frame_offset = 0;

#if INTEROP
		TimeSpan _userProcessorTime;
#endif

		// header bytes
		// allocated by flake_encode_init and freed by flake_encode_close
		byte[] header;
        readonly int[] samplesBuffer;
		int[] verifyBuffer;
        readonly int[] residualBuffer;
        readonly float[] windowBuffer;
        readonly double[] windowScale;
		int samplesInBuffer = 0;

		int _compressionLevel = 7;
		int _blocksize = 0;
        int _windowsize = 0, _windowcount = 0;

		Crc8 crc8;
		Crc16 crc16;
		MD5 md5;

		FlacFrame frame;
		FlakeReader verify;

		SeekPoint[] seek_table;
		int seek_table_offset = -1;

		bool inited = false;

        public FlakeWriter(string path, Stream IO, AudioPCMConfig pcm)
		{
			PCM = pcm;

			channels = pcm.ChannelCount;

			Path = path;
			_IO = IO;

			samplesBuffer = new int[Flake.MAX_BLOCKSIZE * (channels == 2 ? 4 : channels)];
			residualBuffer = new int[Flake.MAX_BLOCKSIZE * (channels == 2 ? 10 : channels + 1)];
			windowBuffer = new float[Flake.MAX_BLOCKSIZE * 2 * Lpc.MAX_LPC_WINDOWS];
			windowScale = new double[Lpc.MAX_LPC_WINDOWS];

			eparams.Flake_set_defaults(_compressionLevel);
			eparams.padding_size = 8192;

			crc8 = new Crc8();
			crc16 = new Crc16();
			frame = new FlacFrame(channels * 2);
		}

		public FlakeWriter(string path, AudioPCMConfig pcm)
			: this(path, null, pcm)
		{
		}

        public int TotalSize { get; private set; } = 0;

        public int CompressionLevel
		{
			get
			{
				return _compressionLevel;
			}
			set
			{
				if (value < 0 || value > 11)
					throw new Exception("unsupported compression level");
				_compressionLevel = value;
				eparams.Flake_set_defaults(_compressionLevel);
			}
		}

		FlakeWriterSettings _settings = new FlakeWriterSettings();

		public object Settings
		{
			get
			{
				return _settings;
			}
			set
			{
				if (value as FlakeWriterSettings == null)
					throw new Exception("Unsupported options " + value);
				_settings = value as FlakeWriterSettings;
			}
		}

		public long Padding
		{
			get
			{
				return eparams.padding_size;
			}
			set
			{
				eparams.padding_size = (int)value;
			}
		}

		void DoClose()
		{
			if (inited)
			{
				while (samplesInBuffer > 0)
				{
					eparams.block_size = samplesInBuffer;
					Output_frame();
				}

				if (_IO.CanSeek)
				{
					if (sample_count <= 0 && Position != 0)
					{
						BitWriter bitwriter = new BitWriter(header, 0, 4);
						bitwriter.Writebits(32, (int)Position);
						bitwriter.Flush();
						_IO.Position = 22;
						_IO.Write(header, 0, 4);
					}

					if (md5 != null)
					{
						md5.TransformFinalBlock(frame_buffer, 0, 0);
						_IO.Position = 26;
						_IO.Write(md5.Hash, 0, md5.Hash.Length);
					}

					if (seek_table != null)
					{
						_IO.Position = seek_table_offset;
						int len = Write_seekpoints(header, 0, 0);
						_IO.Write(header, 4, len - 4);
					}
				}
				_IO.Close();
				inited = false;
			}
		}

		public void Close()
		{
			DoClose();
			if (sample_count > 0 && Position != sample_count)
				throw new Exception(Properties.Resources.ExceptionSampleCount);
		}

		public void Delete()
		{
			if (inited)
			{
				_IO.Close();
				inited = false;
			}

			if (Path != "")
				File.Delete(Path);
		}

        public long Position { get; private set; }

        public long FinalSampleCount
		{
			set { sample_count = (int)value; }
		}

		public long BlockSize
		{
			set { _blocksize = (int)value; }
			get { return _blocksize == 0 ? eparams.block_size : _blocksize; }
		}

		public OrderMethod OrderMethod
		{
			get { return eparams.order_method; }
			set { eparams.order_method = value; }
		}

		public PredictionType PredictionType
		{
			get { return eparams.prediction_type; }
			set { eparams.prediction_type = value; }
		}

		public StereoMethod StereoMethod
		{
			get { return eparams.stereo_method; }
			set { eparams.stereo_method = value; }
		}

		public WindowMethod WindowMethod
		{
			get { return eparams.window_method; }
			set { eparams.window_method = value; }
		}

		public int MinPrecisionSearch
		{
			get { return eparams.lpc_min_precision_search; }
			set
			{
				if (value < 0 || value > eparams.lpc_max_precision_search)
					throw new Exception("unsupported MinPrecisionSearch value");
				eparams.lpc_min_precision_search = value;
			}
		}

		public int MaxPrecisionSearch
		{
			get { return eparams.lpc_max_precision_search; }
			set
			{
				if (value < eparams.lpc_min_precision_search || value >= Lpc.MAX_LPC_PRECISIONS)
					throw new Exception("unsupported MaxPrecisionSearch value");
				eparams.lpc_max_precision_search = value;
			}
		}

		public WindowFunction WindowFunction
		{
			get { return eparams.window_function; }
			set { eparams.window_function = value; }
		}

		public bool DoSeekTable
		{
			get { return eparams.do_seektable; }
			set { eparams.do_seektable = value; }
		}

		public int VBRMode
		{
			get { return eparams.variable_block_size; }
			set { eparams.variable_block_size = value; }
		}

		public int MinPredictionOrder
		{
			get
			{
				return PredictionType == PredictionType.Fixed ?
					MinFixedOrder : MinLPCOrder;
			}
			set
			{
				if (PredictionType == PredictionType.Fixed)
					MinFixedOrder = value;
				else
					MinLPCOrder = value;
			}
		}

		public int MaxPredictionOrder
		{
			get
			{
				return PredictionType == PredictionType.Fixed ?
					MaxFixedOrder : MaxLPCOrder;
			}
			set
			{
				if (PredictionType == PredictionType.Fixed)
					MaxFixedOrder = value;
				else
					MaxLPCOrder = value;
			}
		}

		public int MinLPCOrder
		{
			get
			{
				return eparams.min_prediction_order;
			}
			set
			{
				if (value < 1 || value > eparams.max_prediction_order)
					throw new Exception("invalid MinLPCOrder " + value.ToString());
				eparams.min_prediction_order = value;
			}
		}

		public int MaxLPCOrder
		{
			get
			{
				return eparams.max_prediction_order;
			}
			set
			{
				if (value > Lpc.MAX_LPC_ORDER || value < eparams.min_prediction_order)
					throw new Exception("invalid MaxLPCOrder " + value.ToString());
				eparams.max_prediction_order = value;
			}
		}

		public int EstimationDepth
		{
			get
			{
				return eparams.estimation_depth;
			}
			set
			{
				if (value > 32 || value < 1)
					throw new Exception("invalid estimation_depth " + value.ToString());
				eparams.estimation_depth = value;
			}
		}

		public int MinFixedOrder
		{
			get
			{
				return eparams.min_fixed_order;
			}
			set
			{
				if (value < 0 || value > eparams.max_fixed_order)
					throw new Exception("invalid MinFixedOrder " + value.ToString());
				eparams.min_fixed_order = value;
			}
		}

		public int MaxFixedOrder
		{
			get
			{
				return eparams.max_fixed_order;
			}
			set
			{
				if (value > 4 || value < eparams.min_fixed_order)
					throw new Exception("invalid MaxFixedOrder " + value.ToString());
				eparams.max_fixed_order = value;
			}
		}

		public int MinPartitionOrder
		{
			get { return eparams.min_partition_order; }
			set
			{
				if (value < 0 || value > eparams.max_partition_order)
					throw new Exception("invalid MinPartitionOrder " + value.ToString());
				eparams.min_partition_order = value;
			}
		}

		public int MaxPartitionOrder
		{
			get { return eparams.max_partition_order; }
			set
			{
				if (value > 8 || value < eparams.min_partition_order)
					throw new Exception("invalid MaxPartitionOrder " + value.ToString());
				eparams.max_partition_order = value;
			}
		}

		public TimeSpan UserProcessorTime
		{
			get
			{
				return new TimeSpan(0);
			}
		}

        public AudioPCMConfig PCM { get; }

        unsafe int Get_wasted_bits(int* signal, int samples)
		{
			int i, shift;
			int x = 0;

			for (i = 0; i < samples && 0 == (x & 1); i++)
				x |= signal[i];

			if (x == 0)
			{
				shift = 0;
			}
			else
			{
				for (shift = 0; 0 == (x & 1); shift++)
					x >>= 1;
			}

			if (shift > 0)
			{
				for (i = 0; i < samples; i++)
					signal[i] >>= shift;
			}

			return shift;
		}

		/// <summary>
		/// Copy channel-interleaved input samples into separate subframes
		/// </summary>
		/// <param name="samples"></param>
		/// <param name="pos"></param>
		/// <param name="block"></param>
 		unsafe void Copy_samples(int[,] samples, int pos, int block)
		{
			fixed (int* fsamples = samplesBuffer, src = &samples[pos, 0])
			{
				if (channels == 2)
				{
					if (eparams.stereo_method == StereoMethod.Independent)
						AudioSamples.Deinterlace(fsamples + samplesInBuffer, fsamples + Flake.MAX_BLOCKSIZE + samplesInBuffer, src, block);
					else
					{
						int* left = fsamples + samplesInBuffer;
						int* right = left + Flake.MAX_BLOCKSIZE;
						int* leftM = right + Flake.MAX_BLOCKSIZE;
						int* rightM = leftM + Flake.MAX_BLOCKSIZE;
						for (int i = 0; i < block; i++)
						{
							int l = src[2 * i];
							int r = src[2 * i + 1];
							left[i] = l;
							right[i] = r;
							leftM[i] = (l + r) >> 1;
							rightM[i] = l - r;
						}
					}
				}
				else
					for (int ch = 0; ch < channels; ch++)
					{
						int* psamples = fsamples + ch * Flake.MAX_BLOCKSIZE + samplesInBuffer;
						for (int i = 0; i < block; i++)
							psamples[i] = src[i * channels + ch];
					}
			}
			samplesInBuffer += block;
		}

		//unsafe static void channel_decorrelation(int* leftS, int* rightS, int *leftM, int *rightM, int blocksize)
		//{
		//    for (int i = 0; i < blocksize; i++)
		//    {
		//        leftM[i] = (leftS[i] + rightS[i]) >> 1;
		//        rightM[i] = leftS[i] - rightS[i];
		//    }
		//}

		unsafe void Encode_residual_verbatim(int* res, int* smp, uint n)
		{
			AudioSamples.MemCpy(res, smp, (int) n);
		}

		unsafe void Encode_residual_fixed(int* res, int* smp, int n, int order)
		{
			int i;
			int s0, s1, s2;
			switch (order)
			{
				case 0:
					AudioSamples.MemCpy(res, smp, n);
					return;
				case 1:
					*(res++) = s1 = *(smp++);
					for (i = n - 1; i > 0; i--)
					{
						s0 = *(smp++);
						*(res++) = s0 - s1;
						s1 = s0;
					}
					return;
				case 2:
					*(res++) = s2 = *(smp++);
					*(res++) = s1 = *(smp++);
					for (i = n - 2; i > 0; i--)
					{
						s0 = *(smp++);
						*(res++) = s0 - 2 * s1 + s2;
						s2 = s1;
						s1 = s0;
					}
					return;
				case 3:
					res[0] = smp[0];
					res[1] = smp[1];
					res[2] = smp[2];
					for (i = 3; i < n; i++)
					{
						res[i] = smp[i] - 3 * smp[i - 1] + 3 * smp[i - 2] - smp[i - 3];
					}
					return;
				case 4:
					res[0] = smp[0];
					res[1] = smp[1];
					res[2] = smp[2];
					res[3] = smp[3];
					for (i = 4; i < n; i++)
					{
						res[i] = smp[i] - 4 * smp[i - 1] + 6 * smp[i - 2] - 4 * smp[i - 3] + smp[i - 4];
					}
					return;
				default:
					return;
			}
		}

		static unsafe uint Calc_optimal_rice_params(int porder, int* parm, ulong* sums, uint n, uint pred_order, ref int method)
		{
			uint part = (1U << porder);
			uint cnt = (n >> porder) - pred_order;
			int maxK = method > 0 ? 30 : Flake.MAX_RICE_PARAM;
			int k = cnt > 0 ? Math.Min(maxK, BitReader.Log2i(sums[0] / cnt)) : 0;
			int realMaxK0 = k;
			ulong all_bits = cnt * ((uint)k + 1U) + (sums[0] >> k);
			parm[0] = k;
			cnt = (n >> porder);
			for (uint i = 1; i < part; i++)
			{
				k = Math.Min(maxK, BitReader.Log2i(sums[i] / cnt));
				realMaxK0 = Math.Max(realMaxK0, k);
				all_bits += cnt * ((uint)k + 1U) + (sums[i] >> k);
				parm[i] = k;
			}
			method = realMaxK0 > Flake.MAX_RICE_PARAM ? 1 : 0;
			return (uint)all_bits + ((4U + (uint)method) * part);
		}

		static unsafe void Calc_lower_sums(int pmin, int pmax, ulong* sums)
		{
			for (int i = pmax - 1; i >= pmin; i--)
			{
				for (int j = 0; j < (1 << i); j++)
				{
					sums[i * Flake.MAX_PARTITIONS + j] =
						sums[(i + 1) * Flake.MAX_PARTITIONS + 2 * j] +
						sums[(i + 1) * Flake.MAX_PARTITIONS + 2 * j + 1];
				}
			}
		}

		static unsafe void Calc_sums(int pmin, int pmax, uint* data, uint n, uint pred_order, ulong* sums)
		{
			int parts = (1 << pmax);
			uint* res = data + pred_order;
			uint cnt = (n >> pmax) - pred_order;
			ulong sum = 0;
			for (uint j = cnt; j > 0; j--)
				sum += *(res++);
			sums[0] = sum;
			cnt = (n >> pmax);
			for (int i = 1; i < parts; i++)
			{
				sum = 0;
				for (uint j = cnt; j > 0; j--)
					sum += *(res++);
				sums[i] = sum;
			}
		}

		/// <summary>
		/// Special case when (n >> pmax) == 18
		/// </summary>
		/// <param name="pmin"></param>
		/// <param name="pmax"></param>
		/// <param name="data"></param>
		/// <param name="n"></param>
		/// <param name="pred_order"></param>
		/// <param name="sums"></param>
		static unsafe void Calc_sums18(int pmin, int pmax, uint* data, uint n, uint pred_order, ulong* sums)
		{
			int parts = (1 << pmax);
			uint* res = data + pred_order;
			uint cnt = 18 - pred_order;
			ulong sum = 0;
			for (uint j = cnt; j > 0; j--)
				sum += *(res++);
			sums[0] = sum;
			for (int i = 1; i < parts; i++)
			{
				sums[i] = 0UL +
					*(res++) + *(res++) + *(res++) + *(res++) +
					*(res++) + *(res++) + *(res++) + *(res++) +
					*(res++) + *(res++) + *(res++) + *(res++) +
					*(res++) + *(res++) + *(res++) + *(res++) +
					*(res++) + *(res++);
			}
		}

		/// <summary>
		/// Special case when (n >> pmax) == 18
		/// </summary>
		/// <param name="pmin"></param>
		/// <param name="pmax"></param>
		/// <param name="data"></param>
		/// <param name="n"></param>
		/// <param name="pred_order"></param>
		/// <param name="sums"></param>
		static unsafe void Calc_sums16(int pmin, int pmax, uint* data, uint n, uint pred_order, ulong* sums)
		{
			int parts = (1 << pmax);
			uint* res = data + pred_order;
			uint cnt = 16 - pred_order;
			ulong sum = 0;
			for (uint j = cnt; j > 0; j--)
				sum += *(res++);
			sums[0] = sum;
			for (int i = 1; i < parts; i++)
			{
				sums[i] = 0UL +
					*(res++) + *(res++) + *(res++) + *(res++) +
					*(res++) + *(res++) + *(res++) + *(res++) +
					*(res++) + *(res++) + *(res++) + *(res++) +
					*(res++) + *(res++) + *(res++) + *(res++);
			}
		}

		static unsafe uint Calc_rice_params(RiceContext rc, int pmin, int pmax, int* data, uint n, uint pred_order, int bps)
		{
			uint* udata = stackalloc uint[(int)n];
			ulong* sums = stackalloc ulong[(pmax + 1) * Flake.MAX_PARTITIONS];
			int* parm = stackalloc int[(pmax + 1) * Flake.MAX_PARTITIONS];
			//uint* bits = stackalloc uint[Flake.MAX_PARTITION_ORDER];

			//assert(pmin >= 0 && pmin <= Flake.MAX_PARTITION_ORDER);
			//assert(pmax >= 0 && pmax <= Flake.MAX_PARTITION_ORDER);
			//assert(pmin <= pmax);

			for (uint i = 0; i < n; i++)
				udata[i] = (uint) ((data[i] << 1) ^ (data[i] >> 31));

			// sums for highest level
			if ((n >> pmax) == 18)
				Calc_sums18(pmin, pmax, udata, n, pred_order, sums + pmax * Flake.MAX_PARTITIONS);
			else if ((n >> pmax) == 16)
				Calc_sums16(pmin, pmax, udata, n, pred_order, sums + pmax * Flake.MAX_PARTITIONS);
			else
				Calc_sums(pmin, pmax, udata, n, pred_order, sums + pmax * Flake.MAX_PARTITIONS);
			// sums for lower levels
			Calc_lower_sums(pmin, pmax, sums);

			uint opt_bits = AudioSamples.UINT32_MAX;
			int opt_porder = pmin;
			int opt_method = 0;
			for (int i = pmin; i <= pmax; i++)
			{
				int method = bps > 16 ? 1 : 0;
				uint bits = Calc_optimal_rice_params(i, parm + i * Flake.MAX_PARTITIONS, sums + i * Flake.MAX_PARTITIONS, n, pred_order, ref method);
				if (bits <= opt_bits)
				{
					opt_bits = bits;
					opt_porder = i;
					opt_method = method;
				}
			}

			rc.porder = opt_porder;
			rc.coding_method = opt_method;
			fixed (int* rparms = rc.rparams)
				AudioSamples.MemCpy(rparms, parm + opt_porder * Flake.MAX_PARTITIONS, (1 << opt_porder));

			return opt_bits;
		}

		static int Get_max_p_order(int max_porder, int n, int order)
		{
			int porder = Math.Min(max_porder, BitReader.Log2i(n ^ (n - 1)));
			if (order > 0)
				porder = Math.Min(porder, BitReader.Log2i(n / order));
			return porder;
		}

		unsafe void Encode_residual_lpc_sub(FlacFrame frame, float* lpcs, int iWindow, int order, int ch)
		{
			// select LPC precision based on block size
			uint lpc_precision;
			if (frame.blocksize <= 192) lpc_precision = 7U;
			else if (frame.blocksize <= 384) lpc_precision = 8U;
			else if (frame.blocksize <= 576) lpc_precision = 9U;
			else if (frame.blocksize <= 1152) lpc_precision = 10U;
			else if (frame.blocksize <= 2304) lpc_precision = 11U;
			else if (frame.blocksize <= 4608) lpc_precision = 12U;
			else if (frame.blocksize <= 8192) lpc_precision = 13U;
			else if (frame.blocksize <= 16384) lpc_precision = 14U;
			else lpc_precision = 15;

			for (int i_precision = eparams.lpc_min_precision_search; i_precision <= eparams.lpc_max_precision_search && lpc_precision + i_precision < 16; i_precision++)
				// check if we already calculated with this order, window and precision
				if ((frame.subframes[ch].lpc_ctx[iWindow].done_lpcs[i_precision] & (1U << (order - 1))) == 0)
				{
					frame.subframes[ch].lpc_ctx[iWindow].done_lpcs[i_precision] |= (1U << (order - 1));

					uint cbits = lpc_precision + (uint)i_precision;

					frame.current.type = SubframeType.LPC;
					frame.current.order = order;
					frame.current.window = iWindow;

					fixed (int* coefs = frame.current.coefs)
					{
						Lpc.Quantize_lpc_coefs(lpcs + (frame.current.order - 1) * Lpc.MAX_LPC_ORDER,
							frame.current.order, cbits, coefs, out frame.current.shift, 15, 0);

						if (frame.current.shift < 0 || frame.current.shift > 15)
							throw new Exception("negative shift");

						ulong csum = 0;
						for (int i = frame.current.order; i > 0; i--)
							csum += (ulong)Math.Abs(coefs[i - 1]);

						if ((csum << frame.subframes[ch].obits) >= 1UL << 32)
							Lpc.Encode_residual_long(frame.current.residual, frame.subframes[ch].samples, frame.blocksize, frame.current.order, coefs, frame.current.shift);
						else
							Lpc.Encode_residual(frame.current.residual, frame.subframes[ch].samples, frame.blocksize, frame.current.order, coefs, frame.current.shift);

					}
					int pmax = Get_max_p_order(eparams.max_partition_order, frame.blocksize, frame.current.order);
					int pmin = Math.Min(eparams.min_partition_order, pmax);
					uint best_size = Calc_rice_params(frame.current.rc, pmin, pmax, frame.current.residual, (uint)frame.blocksize, (uint)frame.current.order, PCM.BitsPerSample);
					// not working
					//for (int o = 1; o <= frame.current.order; o++)
					//{
					//    if (frame.current.coefs[o - 1] > -(1 << frame.current.shift))
					//    {
					//        for (int i = o; i < frame.blocksize; i++)
					//            frame.current.residual[i] += frame.subframes[ch].samples[i - o] >> frame.current.shift;
					//        frame.current.coefs[o - 1]--;
					//        uint new_size = calc_rice_params(ref frame.current.rc, pmin, pmax, frame.current.residual, (uint)frame.blocksize, (uint)frame.current.order);
					//        if (new_size > best_size)
					//        {
					//            for (int i = o; i < frame.blocksize; i++)
					//                frame.current.residual[i] -= frame.subframes[ch].samples[i - o] >> frame.current.shift;
					//            frame.current.coefs[o - 1]++;
					//        }
					//    }
					//}
					frame.current.size = (uint)(frame.current.order * frame.subframes[ch].obits + 4 + 5 + frame.current.order * (int)cbits + 6 + (int)best_size);
					frame.ChooseBestSubframe(ch);
				}
		}

		unsafe void Encode_residual_fixed_sub(FlacFrame frame, int order, int ch)
		{
			if ((frame.subframes[ch].done_fixed & (1U << order)) != 0)
				return; // already calculated;

			frame.current.order = order;
			frame.current.type = SubframeType.Fixed;

			Encode_residual_fixed(frame.current.residual, frame.subframes[ch].samples, frame.blocksize, frame.current.order);

			int pmax = Get_max_p_order(eparams.max_partition_order, frame.blocksize, frame.current.order);
			int pmin = Math.Min(eparams.min_partition_order, pmax);
			frame.current.size = (uint)(frame.current.order * frame.subframes[ch].obits) + 6
				+ Calc_rice_params(frame.current.rc, pmin, pmax, frame.current.residual, (uint)frame.blocksize, (uint)frame.current.order, PCM.BitsPerSample);

			frame.subframes[ch].done_fixed |= (1U << order);

			frame.ChooseBestSubframe(ch);
		}

		unsafe void Encode_residual(FlacFrame frame, int ch, PredictionType predict, OrderMethod omethod, int pass, int best_window)
		{
			int* smp = frame.subframes[ch].samples;
			int i, n = frame.blocksize;
			// save best.window, because we can overwrite it later with fixed frame

			// CONSTANT
			for (i = 1; i < n; i++)
			{
				if (smp[i] != smp[0]) break;
			}
			if (i == n)
			{
				frame.subframes[ch].best.type = SubframeType.Constant;
				frame.subframes[ch].best.residual[0] = smp[0];
				frame.subframes[ch].best.size = (uint)frame.subframes[ch].obits;
				return;
			}

			// VERBATIM
			frame.current.type = SubframeType.Verbatim;
			frame.current.size = (uint)(frame.subframes[ch].obits * frame.blocksize);
			frame.ChooseBestSubframe(ch);

			if (n < 5 || predict == PredictionType.None)
				return;

			// FIXED
			if (predict == PredictionType.Fixed ||
				(predict == PredictionType.Search && pass != 1) ||
				//predict == PredictionType.Search ||
				//(pass == 2 && frame.subframes[ch].best.type == SubframeType.Fixed) ||
				n <= eparams.max_prediction_order)
			{
				int max_fixed_order = Math.Min(eparams.max_fixed_order, 4);
				int min_fixed_order = Math.Min(eparams.min_fixed_order, max_fixed_order);

				for (i = min_fixed_order; i <= max_fixed_order; i++)
					Encode_residual_fixed_sub(frame, i, ch);
			}

			// LPC
			if (n > eparams.max_prediction_order &&
			   (predict == PredictionType.Levinson ||
				predict == PredictionType.Search)
				//predict == PredictionType.Search ||
				//(pass == 2 && frame.subframes[ch].best.type == SubframeType.LPC))
				)
			{
				float* lpcs = stackalloc float[Lpc.MAX_LPC_ORDER * Lpc.MAX_LPC_ORDER];
				int min_order = eparams.min_prediction_order;
				int max_order = eparams.max_prediction_order;

				for (int iWindow = 0; iWindow < _windowcount; iWindow++)
				{
					if (best_window != -1 && iWindow != best_window)
						continue;

					LpcContext lpc_ctx = frame.subframes[ch].lpc_ctx[iWindow];

					lpc_ctx.GetReflection(max_order, smp, n, frame.window_buffer + iWindow * Flake.MAX_BLOCKSIZE * 2);
					lpc_ctx.ComputeLPC(lpcs);

					//int frameSize = n;
					//float* F = stackalloc float[frameSize];
					//float* B = stackalloc float[frameSize];
					//float* PE = stackalloc float[max_order + 1];
					//float* arp = stackalloc float[max_order];
					//float* rc = stackalloc float[max_order];

					//for (int j = 0; j < frameSize; j++)
					//    F[j] = B[j] = smp[j];

					//for (int K = 1; K <= max_order; K++)
					//{
					//    // BURG:
					//    float denominator = 0.0f;
					//    //float denominator = F[K - 1] * F[K - 1] + B[frameSize - K] * B[frameSize - K];
					//    for (int j = 0; j < frameSize - K; j++)
					//        denominator += F[j + K] * F[j + K] + B[j] * B[j];
					//    denominator /= 2;

					//    // Estimate error
					//    PE[K - 1] = denominator / (frameSize - K);

					//    float reflectionCoeff = 0.0f;
					//    for (int j = 0; j < frameSize - K; j++)
					//        reflectionCoeff += F[j + K] * B[j];
					//    reflectionCoeff /= denominator;
					//    rc[K - 1] = arp[K - 1] = reflectionCoeff;

					//    // Levinson-Durbin
					//    for (int j = 0; j < (K - 1) >> 1; j++)
					//    {
					//        float arptmp = arp[j];
					//        arp[j] -= reflectionCoeff * arp[K - 2 - j];
					//        arp[K - 2 - j] -= reflectionCoeff * arptmp;
					//    }
					//    if (((K - 1) & 1) != 0)
					//        arp[(K - 1) >> 1] -= reflectionCoeff * arp[(K - 1) >> 1];

					//    for (int j = 0; j < frameSize - K; j++)
					//    {
					//        float f = F[j + K];
					//        float b = B[j];
					//        F[j + K] = f - reflectionCoeff * b;
					//        B[j] = b - reflectionCoeff * f;
					//    }

					//    for (int j = 0; j < K; j++)
					//        lpcs[(K - 1) * lpc.MAX_LPC_ORDER + j] = (float)arp[j];
					//}

					switch (omethod)
					{
						case OrderMethod.Akaike:
							//lpc_ctx.SortOrdersAkaike(frame.blocksize, eparams.estimation_depth, max_order, 7.1, 0.0);
							lpc_ctx.SortOrdersAkaike(frame.blocksize, eparams.estimation_depth, max_order, 4.5, 0.0);
							break;
						default:
							throw new Exception("unknown order method");
					}

					for (i = 0; i < eparams.estimation_depth && i < max_order; i++)
						Encode_residual_lpc_sub(frame, lpcs, iWindow, lpc_ctx.best_orders[i], ch);
				}
			}
		}

		unsafe void Output_frame_header(FlacFrame frame, BitWriter bitwriter)
		{
			bitwriter.Writebits(15, 0x7FFC);
			bitwriter.Writebits(1, eparams.variable_block_size > 0 ? 1 : 0);
			bitwriter.Writebits(4, frame.bs_code0);
			bitwriter.Writebits(4, sr_code0);
			if (frame.ch_mode == ChannelMode.NotStereo)
				bitwriter.Writebits(4, ch_code);
			else
				bitwriter.Writebits(4, (int) frame.ch_mode);
			bitwriter.Writebits(3, bps_code);
			bitwriter.Writebits(1, 0);
			bitwriter.Write_utf8(frame_count);

			// custom block size
			if (frame.bs_code1 >= 0)
			{
				if (frame.bs_code1 < 256)
					bitwriter.Writebits(8, frame.bs_code1);
				else
					bitwriter.Writebits(16, frame.bs_code1);
			}

			// CRC-8 of frame header
			bitwriter.Flush();
			byte crc = crc8.ComputeChecksum(frame_buffer, 0, bitwriter.Length);
			bitwriter.Writebits(8, crc);
		}

		unsafe void Output_residual(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			// rice-encoded block
			bitwriter.Writebits(2, sub.best.rc.coding_method);

			// partition order
			int porder = sub.best.rc.porder;
			int psize = frame.blocksize >> porder;
			//assert(porder >= 0);
			bitwriter.Writebits(4, porder);
			int res_cnt = psize - sub.best.order;

			int rice_len = 4 + sub.best.rc.coding_method;
			// residual
			int j = sub.best.order;
			fixed (byte* fixbuf = &frame_buffer[0])
			for (int p = 0; p < (1 << porder); p++)
			{
				int k = sub.best.rc.rparams[p];
				bitwriter.Writebits(rice_len, k);
				if (p == 1) res_cnt = psize;
				int cnt = Math.Min(res_cnt, frame.blocksize - j);
				bitwriter.Write_rice_block_signed(fixbuf, k, sub.best.residual + j, cnt);
				j += cnt;
			}
		}

		unsafe void Output_subframe_constant(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			bitwriter.Writebits_signed(sub.obits, sub.best.residual[0]);
		}

		unsafe void Output_subframe_verbatim(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			int n = frame.blocksize;
			for (int i = 0; i < n; i++)
				bitwriter.Writebits_signed(sub.obits, sub.samples[i]);
			// Don't use residual here, because we don't copy samples to residual for verbatim frames.
		}

		unsafe void Output_subframe_fixed(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			// warm-up samples
			for (int i = 0; i < sub.best.order; i++)
				bitwriter.Writebits_signed(sub.obits, sub.best.residual[i]);

			// residual
			Output_residual(frame, bitwriter, sub);
		}

		unsafe void Output_subframe_lpc(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			// warm-up samples
			for (int i = 0; i < sub.best.order; i++)
				bitwriter.Writebits_signed(sub.obits, sub.best.residual[i]);

			// LPC coefficients
			int cbits = 1;
			for (int i = 0; i < sub.best.order; i++)
				while (cbits < 16 && sub.best.coefs[i] != (sub.best.coefs[i] << (32 - cbits)) >> (32 - cbits))
					cbits++;
			bitwriter.Writebits(4, cbits - 1);
			bitwriter.Writebits_signed(5, sub.best.shift);
			for (int i = 0; i < sub.best.order; i++)
				bitwriter.Writebits_signed(cbits, sub.best.coefs[i]);

			// residual
			Output_residual(frame, bitwriter, sub);
		}

		unsafe void Output_subframes(FlacFrame frame, BitWriter bitwriter)
		{
			for (int ch = 0; ch < channels; ch++)
			{
				FlacSubframeInfo sub = frame.subframes[ch];
				// subframe header
				int type_code = (int) sub.best.type;
				if (sub.best.type == SubframeType.Fixed)
					type_code |= sub.best.order;
				if (sub.best.type == SubframeType.LPC)
					type_code |= sub.best.order - 1;
				bitwriter.Writebits(1, 0);
				bitwriter.Writebits(6, type_code);
				bitwriter.Writebits(1, sub.wbits != 0 ? 1 : 0);
				if (sub.wbits > 0)
					bitwriter.Writebits((int)sub.wbits, 1);

				// subframe
				switch (sub.best.type)
				{
					case SubframeType.Constant:
						Output_subframe_constant(frame, bitwriter, sub);
						break;
					case SubframeType.Verbatim:
						Output_subframe_verbatim(frame, bitwriter, sub);
						break;
					case SubframeType.Fixed:
						Output_subframe_fixed(frame, bitwriter, sub);
						break;
					case SubframeType.LPC:
						Output_subframe_lpc(frame, bitwriter, sub);
						break;
				}
			}
		}

		void Output_frame_footer(BitWriter bitwriter)
		{
			bitwriter.Flush();
			ushort crc = crc16.ComputeChecksum(frame_buffer, 0, bitwriter.Length);
			bitwriter.Writebits(16, crc);
			bitwriter.Flush();
		}

		unsafe void Encode_residual_pass1(FlacFrame frame, int ch, int best_window)
		{
			int max_prediction_order = eparams.max_prediction_order;
			int max_fixed_order = eparams.max_fixed_order;
			int min_fixed_order = eparams.min_fixed_order;
			int lpc_min_precision_search = eparams.lpc_min_precision_search;
			int lpc_max_precision_search = eparams.lpc_max_precision_search;
			int max_partition_order = eparams.max_partition_order;
			int estimation_depth = eparams.estimation_depth;
			eparams.min_fixed_order = 2;
			eparams.max_fixed_order = 2;
			eparams.lpc_min_precision_search = eparams.lpc_max_precision_search;
			eparams.max_prediction_order = 8;
			eparams.estimation_depth = 1;
			Encode_residual(frame, ch, eparams.prediction_type, OrderMethod.Akaike, 1, best_window);
			eparams.min_fixed_order = min_fixed_order;
			eparams.max_fixed_order = max_fixed_order;
			eparams.max_prediction_order = max_prediction_order;
			eparams.lpc_min_precision_search = lpc_min_precision_search;
			eparams.lpc_max_precision_search = lpc_max_precision_search;
			eparams.max_partition_order = max_partition_order;
			eparams.estimation_depth = estimation_depth;
		}

		unsafe void Encode_residual_pass2(FlacFrame frame, int ch)
		{
			Encode_residual(frame, ch, eparams.prediction_type, eparams.order_method, 2, Estimate_best_window(frame, ch));
		}

		unsafe int Estimate_best_window(FlacFrame frame, int ch)
		{
			if (_windowcount == 1)
				return 0;
			switch (eparams.window_method)
			{
				case WindowMethod.Estimate:
					{
						int best_window = -1;
						double best_error = 0;
						int order = 2;
						for (int i = 0; i < _windowcount; i++)
						{
							frame.subframes[ch].lpc_ctx[i].GetReflection(order, frame.subframes[ch].samples, frame.blocksize, frame.window_buffer + i * Flake.MAX_BLOCKSIZE * 2);
							double err = frame.subframes[ch].lpc_ctx[i].prediction_error[order - 1] / frame.subframes[ch].lpc_ctx[i].autocorr_values[0];
							//double err = frame.subframes[ch].lpc_ctx[i].autocorr_values[0] / frame.subframes[ch].lpc_ctx[i].autocorr_values[2];
							if (best_window == -1 || best_error > err)
							{
								best_window = i;
								best_error = err;
							}
						}
						return best_window;
					}
				case WindowMethod.Evaluate:
					Encode_residual_pass1(frame, ch, -1);
					return frame.subframes[ch].best.type == SubframeType.LPC ? frame.subframes[ch].best.window : -1;
				case WindowMethod.Search:
					return -1;
			}
			return -1;
		}

		unsafe void Estimate_frame(FlacFrame frame, bool do_midside)
		{
			int subframes = do_midside ? channels * 2 : channels;

			switch (eparams.stereo_method)
			{
				case StereoMethod.Estimate:
					for (int ch = 0; ch < subframes; ch++)
					{
						LpcContext lpc_ctx = frame.subframes[ch].lpc_ctx[0];
						lpc_ctx.GetReflection(4, frame.subframes[ch].samples, frame.blocksize, frame.window_buffer);
						lpc_ctx.SortOrdersAkaike(frame.blocksize, 1, 4, 4.5, 0.0);
						frame.subframes[ch].best.size = (uint)Math.Max(0, lpc_ctx.Akaike(frame.blocksize, lpc_ctx.best_orders[0], 4.5, 0.0) + 7.1 * frame.subframes[ch].obits * eparams.max_prediction_order);
					}
					break;
				case StereoMethod.Evaluate:
					for (int ch = 0; ch < subframes; ch++)
						Encode_residual_pass1(frame, ch, 0);
					break;
				case StereoMethod.Search:
					for (int ch = 0; ch < subframes; ch++)
					    Encode_residual_pass2(frame, ch);
					break;
			}
		}

		unsafe uint Measure_frame_size(FlacFrame frame, bool do_midside)
		{
			// crude estimation of header/footer size
			uint total = (uint)(32 + ((BitReader.Log2i(frame_count) + 4) / 5) * 8 + (eparams.variable_block_size != 0 ? 16 : 0) + 16);

			if (do_midside)
			{
				uint bitsBest = AudioSamples.UINT32_MAX;
				ChannelMode modeBest = ChannelMode.LeftRight;

				if (bitsBest > frame.subframes[2].best.size + frame.subframes[3].best.size)
				{
					bitsBest = frame.subframes[2].best.size + frame.subframes[3].best.size;
					modeBest = ChannelMode.MidSide;
				}
				if (bitsBest > frame.subframes[3].best.size + frame.subframes[1].best.size)
				{
					bitsBest = frame.subframes[3].best.size + frame.subframes[1].best.size;
					modeBest = ChannelMode.RightSide;
				}
				if (bitsBest > frame.subframes[3].best.size + frame.subframes[0].best.size)
				{
					bitsBest = frame.subframes[3].best.size + frame.subframes[0].best.size;
					modeBest = ChannelMode.LeftSide;
				}
				if (bitsBest > frame.subframes[0].best.size + frame.subframes[1].best.size)
				{
					bitsBest = frame.subframes[0].best.size + frame.subframes[1].best.size;
					modeBest = ChannelMode.LeftRight;
				}
				frame.ch_mode = modeBest;
				return total + bitsBest;
			}

			for (int ch = 0; ch < channels; ch++)
				total += frame.subframes[ch].best.size;
			return total;
		}

		unsafe void Encode_estimated_frame(FlacFrame frame)
		{
			switch (eparams.stereo_method)
			{
				case StereoMethod.Estimate:
					for (int ch = 0; ch < channels; ch++)
					{
						frame.subframes[ch].best.size = AudioSamples.UINT32_MAX;
						Encode_residual_pass2(frame, ch);
					}
					break;
				case StereoMethod.Evaluate:
					for (int ch = 0; ch < channels; ch++)
						Encode_residual_pass2(frame, ch);
					break;
				case StereoMethod.Search:
					break;
			}
		}

		unsafe delegate void window_function(float* window, int size);

		unsafe void Calculate_window(float* window, window_function func, WindowFunction flag)
		{
			if ((eparams.window_function & flag) == 0 || _windowcount == Lpc.MAX_LPC_WINDOWS)
				return;
			int sz = _windowsize;
			float* pos1 = window + _windowcount * Flake.MAX_BLOCKSIZE * 2;
			float* pos = pos1;
			do
			{
				func(pos, sz);
				if ((sz & 1) != 0)
					break;
				pos += sz;
				sz >>= 1;
			} while (sz >= 32);
			double scale = 0.0;
			for (int i = 0; i < _windowsize; i++)
				scale += pos1[i] * pos1[i];
			windowScale[_windowcount] = scale;
			_windowcount++;
		}

		unsafe int Encode_frame(out int size)
		{
			fixed (int* s = samplesBuffer, r = residualBuffer)
			fixed (float* window = windowBuffer)
			{
				frame.InitSize(eparams.block_size, eparams.variable_block_size != 0);

				if (frame.blocksize != _windowsize && frame.blocksize > 4)
				{
					_windowsize = frame.blocksize;
					_windowcount = 0;
					Calculate_window(window, Lpc.Window_welch, WindowFunction.Welch);
					Calculate_window(window, Lpc.Window_tukey, WindowFunction.Tukey);
					Calculate_window(window, Lpc.Window_flattop, WindowFunction.Flattop);
					Calculate_window(window, Lpc.Window_hann, WindowFunction.Hann);
					Calculate_window(window, Lpc.Window_bartlett, WindowFunction.Bartlett);
					if (_windowcount == 0)
						throw new Exception("invalid windowfunction");
				}

				if (channels != 2 || frame.blocksize <= 32 || eparams.stereo_method == StereoMethod.Independent)
				{
					frame.window_buffer = window;
					frame.current.residual = r + channels * Flake.MAX_BLOCKSIZE;
					frame.ch_mode = channels != 2 ? ChannelMode.NotStereo : ChannelMode.LeftRight;
					for (int ch = 0; ch < channels; ch++)
						frame.subframes[ch].Init(s + ch * Flake.MAX_BLOCKSIZE, r + ch * Flake.MAX_BLOCKSIZE,
							PCM.BitsPerSample, Get_wasted_bits(s + ch * Flake.MAX_BLOCKSIZE, frame.blocksize));

					for (int ch = 0; ch < channels; ch++)
						Encode_residual_pass2(frame, ch);
				}
				else
				{
					//channel_decorrelation(s, s + Flake.MAX_BLOCKSIZE, s + 2 * Flake.MAX_BLOCKSIZE, s + 3 * Flake.MAX_BLOCKSIZE, frame.blocksize);
					frame.window_buffer = window;
					frame.current.residual = r + 4 * Flake.MAX_BLOCKSIZE;
					for (int ch = 0; ch < 4; ch++)
						frame.subframes[ch].Init(s + ch * Flake.MAX_BLOCKSIZE, r + ch * Flake.MAX_BLOCKSIZE,
							PCM.BitsPerSample + (ch == 3 ? 1 : 0), Get_wasted_bits(s + ch * Flake.MAX_BLOCKSIZE, frame.blocksize));

					//for (int ch = 0; ch < 4; ch++)
					//    for (int iWindow = 0; iWindow < _windowcount; iWindow++)
					//        frame.subframes[ch].lpc_ctx[iWindow].GetReflection(32, frame.subframes[ch].samples, frame.blocksize, frame.window_buffer + iWindow * Flake.MAX_BLOCKSIZE * 2);

					Estimate_frame(frame, true);
					uint fs = Measure_frame_size(frame, true);

					if (0 != eparams.variable_block_size)
					{
						FlacFrame frame2 = new FlacFrame(channels * 2);
						FlacFrame frame3 = new FlacFrame(channels * 2);
						int tumbler = 1;
						while ((frame.blocksize & 1) == 0 && frame.blocksize >= 1024)
						{
							frame2.InitSize(frame.blocksize / 2, true);
							frame2.window_buffer = frame.window_buffer + frame.blocksize;
							frame2.current.residual = r + tumbler * 5 * Flake.MAX_BLOCKSIZE;
							for (int ch = 0; ch < 4; ch++)
								frame2.subframes[ch].Init(frame.subframes[ch].samples, frame2.current.residual + (ch + 1) * frame2.blocksize,
									frame.subframes[ch].obits + frame.subframes[ch].wbits, frame.subframes[ch].wbits);
							Estimate_frame(frame2, true);
							uint fs2 = Measure_frame_size(frame2, true);
							uint fs3 = fs2;
							if (eparams.variable_block_size == 2 || eparams.variable_block_size == 4)
							{
								frame3.InitSize(frame2.blocksize, true);
								frame3.window_buffer = frame2.window_buffer;
								frame3.current.residual = frame2.current.residual + 5 * frame2.blocksize;
								for (int ch = 0; ch < 4; ch++)
									frame3.subframes[ch].Init(frame2.subframes[ch].samples + frame2.blocksize, frame3.current.residual + (ch + 1) * frame3.blocksize,
										frame.subframes[ch].obits + frame.subframes[ch].wbits, frame.subframes[ch].wbits);
								Estimate_frame(frame3, true);
								fs3 = Measure_frame_size(frame3, true);
							}
							if (fs2 + fs3 > fs)
								break;
							FlacFrame tmp = frame;
							frame = frame2;
							frame2 = tmp;
							fs = fs2;
							if (eparams.variable_block_size <= 2)
								break;
							tumbler = 1 - tumbler;
						}
					}

					frame.ChooseSubframes();
					Encode_estimated_frame(frame);
				}

				BitWriter bitwriter = new BitWriter(frame_buffer, 0, max_frame_size);

				Output_frame_header(frame, bitwriter);
				Output_subframes(frame, bitwriter);
				Output_frame_footer(bitwriter);

				if (bitwriter.Length >= max_frame_size)
					throw new Exception("buffer overflow");

				if (frame_buffer != null)
				{
					if (eparams.variable_block_size > 0)
						frame_count += frame.blocksize;
					else
						frame_count++;
				}
				size = frame.blocksize;
				return bitwriter.Length;
			}
		}

		unsafe int Output_frame()
		{
			if (verify != null)
			{
				fixed (int* s = verifyBuffer, r = samplesBuffer)
					for (int ch = 0; ch < channels; ch++)
						AudioSamples.MemCpy(s + ch * Flake.MAX_BLOCKSIZE, r + ch * Flake.MAX_BLOCKSIZE, eparams.block_size);
			}

			int fs;
            //if (0 != eparams.variable_block_size && 0 == (eparams.block_size & 7) && eparams.block_size >= 128)
            //    fs = encode_frame_vbs();
            //else
            fs = Encode_frame(out int bs);

			if (seek_table != null && _IO.CanSeek)
			{
				for (int sp = 0; sp < seek_table.Length; sp++)
				{
					if (seek_table[sp].framesize != 0)
						continue;
					if (seek_table[sp].number > Position + bs)
						break;
					if (seek_table[sp].number >= Position)
					{
						seek_table[sp].number = Position;
						seek_table[sp].offset = _IO.Position - first_frame_offset;
						seek_table[sp].framesize = bs;
					}
				}
			}

			Position += bs;
			_IO.Write(frame_buffer, 0, fs);
			TotalSize += fs;

			if (verify != null)
			{
				int decoded = verify.DecodeFrame(frame_buffer, 0, fs);
				if (decoded != fs || verify.Remaining != bs)
					throw new Exception(Properties.Resources.ExceptionValidationFailed);
				fixed (int* s = verifyBuffer, r = verify.Samples)
				{
					for (int ch = 0; ch < channels; ch++)
						if (AudioSamples.MemCmp(s + ch * Flake.MAX_BLOCKSIZE, r + ch * Flake.MAX_BLOCKSIZE, bs))
							throw new Exception(Properties.Resources.ExceptionValidationFailed);
				}
			}

			if (bs < eparams.block_size)
			{
				for (int ch = 0; ch < (channels == 2 ? 4 : channels); ch++)
					Buffer.BlockCopy(samplesBuffer, (bs + ch * Flake.MAX_BLOCKSIZE) * sizeof(int), samplesBuffer, ch * Flake.MAX_BLOCKSIZE * sizeof(int), (eparams.block_size - bs) * sizeof(int));
				//fixed (int* s = samplesBuffer)
				//    for (int ch = 0; ch < channels; ch++)
				//        AudioSamples.MemCpy(s + ch * Flake.MAX_BLOCKSIZE, s + bs + ch * Flake.MAX_BLOCKSIZE, eparams.block_size - bs);
			}

			samplesInBuffer -= bs;

			return bs;
		}

		public void Write(AudioBuffer buff)
		{
			if (!inited)
			{
				if (_IO == null)
					_IO = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
				int header_size = Flake_encode_init();
				_IO.Write(header, 0, header_size);
				if (_IO.CanSeek)
					first_frame_offset = _IO.Position;
				inited = true;
			}

			buff.Prepare(this);

			int pos = 0;
			while (pos < buff.Length)
			{
				int block = Math.Min(buff.Length - pos, eparams.block_size - samplesInBuffer);

				Copy_samples(buff.Samples, pos, block);

				pos += block;

				while (samplesInBuffer >= eparams.block_size)
					Output_frame();
			}

			if (md5 != null)
				md5.TransformBlock(buff.Bytes, 0, buff.ByteLength, null, 0);
		}

        public string Path { get; }

        string vendor_string = "Flake#0.1";

		int Select_blocksize(int samplerate, int time_ms)
		{
			int blocksize = Flake.flac_blocksizes[1];
			int target = (samplerate * time_ms) / 1000;
			if (eparams.variable_block_size > 0)
			{
				blocksize = 1024;
				while (target >= blocksize)
					blocksize <<= 1;
				return blocksize >> 1;
			}

			for (int i = 0; i < Flake.flac_blocksizes.Length; i++)
				if (target >= Flake.flac_blocksizes[i] && Flake.flac_blocksizes[i] > blocksize)
				{
					blocksize = Flake.flac_blocksizes[i];
				}
			return blocksize;
		}

		void Write_streaminfo(byte[] header, int pos, int last)
		{
			Array.Clear(header, pos, 38);
			BitWriter bitwriter = new BitWriter(header, pos, 38);

			// metadata header
			bitwriter.Writebits(1, last);
			bitwriter.Writebits(7, (int)MetadataType.StreamInfo);
			bitwriter.Writebits(24, 34);

			if (eparams.variable_block_size > 0)
				bitwriter.Writebits(16, 0);
			else
				bitwriter.Writebits(16, eparams.block_size);

			bitwriter.Writebits(16, eparams.block_size);
			bitwriter.Writebits(24, 0);
			bitwriter.Writebits(24, max_frame_size);
			bitwriter.Writebits(20, PCM.SampleRate);
			bitwriter.Writebits(3, channels - 1);
			bitwriter.Writebits(5, PCM.BitsPerSample - 1);

			// total samples
			if (sample_count > 0)
			{
				bitwriter.Writebits(4, 0);
				bitwriter.Writebits(32, sample_count);
			}
			else
			{
				bitwriter.Writebits(4, 0);
				bitwriter.Writebits(32, 0);
			}
			bitwriter.Flush();
		}

		/**
		 * Write vorbis comment metadata block to byte array.
		 * Just writes the vendor string for now.
	     */
		int Write_vorbis_comment(byte[] comment, int pos, int last)
		{
			BitWriter bitwriter = new BitWriter(comment, pos, 4);
			Encoding enc = new ASCIIEncoding();
			int vendor_len = enc.GetBytes(vendor_string, 0, vendor_string.Length, comment, pos + 8);

			// metadata header
			bitwriter.Writebits(1, last);
			bitwriter.Writebits(7, (int)MetadataType.VorbisComment);
			bitwriter.Writebits(24, vendor_len + 8);

			comment[pos + 4] = (byte)(vendor_len & 0xFF);
			comment[pos + 5] = (byte)((vendor_len >> 8) & 0xFF);
			comment[pos + 6] = (byte)((vendor_len >> 16) & 0xFF);
			comment[pos + 7] = (byte)((vendor_len >> 24) & 0xFF);
			comment[pos + 8 + vendor_len] = 0;
			comment[pos + 9 + vendor_len] = 0;
			comment[pos + 10 + vendor_len] = 0;
			comment[pos + 11 + vendor_len] = 0;
			bitwriter.Flush();
			return vendor_len + 12;
		}

		int Write_seekpoints(byte[] header, int pos, int last)
		{
			seek_table_offset = pos + 4;

			BitWriter bitwriter = new BitWriter(header, pos, 4 + 18 * seek_table.Length);

			// metadata header
			bitwriter.Writebits(1, last);
			bitwriter.Writebits(7, (int)MetadataType.Seektable);
			bitwriter.Writebits(24, 18 * seek_table.Length);
			for (int i = 0; i < seek_table.Length; i++)
			{
				bitwriter.Writebits64(Flake.FLAC__STREAM_METADATA_SEEKPOINT_SAMPLE_NUMBER_LEN, (ulong)seek_table[i].number);
				bitwriter.Writebits64(Flake.FLAC__STREAM_METADATA_SEEKPOINT_STREAM_OFFSET_LEN, (ulong)seek_table[i].offset);
				bitwriter.Writebits(Flake.FLAC__STREAM_METADATA_SEEKPOINT_FRAME_SAMPLES_LEN, seek_table[i].framesize);
			}
			bitwriter.Flush();
			return 4 + 18 * seek_table.Length;
		}

		/**
		 * Write padding metadata block to byte array.
		 */
		int Write_padding(byte[] padding, int pos, int last, int padlen)
		{
			BitWriter bitwriter = new BitWriter(padding, pos, 4);

			// metadata header
			bitwriter.Writebits(1, last);
			bitwriter.Writebits(7, (int)MetadataType.Padding);
			bitwriter.Writebits(24, padlen);

			return padlen + 4;
		}

		int Write_headers()
		{
			int header_size = 0;
			int last = 0;

			// stream marker
			header[0] = 0x66;
			header[1] = 0x4C;
			header[2] = 0x61;
			header[3] = 0x43;
			header_size += 4;

			// streaminfo
			Write_streaminfo(header, header_size, last);
			header_size += 38;

			// seek table
			if (_IO.CanSeek && seek_table != null)
				header_size += Write_seekpoints(header, header_size, last);

			// vorbis comment
			if (eparams.padding_size == 0) last = 1;
			header_size += Write_vorbis_comment(header, header_size, last);

			// padding
			if (eparams.padding_size > 0)
			{
				last = 1;
				header_size += Write_padding(header, header_size, last, eparams.padding_size);
			}

			return header_size;
		}

		int Flake_encode_init()
		{
			int i, header_len;

			//if(flake_validate_params(s) < 0)

			ch_code = channels - 1;

			// find samplerate in table
			for (i = 4; i < 12; i++)
			{
				if (PCM.SampleRate == Flake.flac_samplerates[i])
				{
					sr_code0 = i;
					break;
				}
			}

			// if not in table, samplerate is non-standard
			if (i == 12)
				throw new Exception("non-standard samplerate");

			for (i = 1; i < 8; i++)
			{
				if (PCM.BitsPerSample == Flake.flac_bitdepths[i])
				{
					bps_code = i;
					break;
				}
			}
			if (i == 8)
				throw new Exception("non-standard bps");

			if (_blocksize == 0)
			{
				if (eparams.block_size == 0)
					eparams.block_size = Select_blocksize(PCM.SampleRate, eparams.block_time_ms);
				_blocksize = eparams.block_size;
			}
			else
				eparams.block_size = _blocksize;

			// set maximum encoded frame size (if larger, re-encodes in verbatim mode)
			if (channels == 2)
				max_frame_size = 16 + ((eparams.block_size * (PCM.BitsPerSample + PCM.BitsPerSample + 1) + 7) >> 3);
			else
				max_frame_size = 16 + ((eparams.block_size * channels * PCM.BitsPerSample + 7) >> 3);

			if (_IO.CanSeek && eparams.do_seektable && sample_count > 0)
			{
				int seek_points_distance = PCM.SampleRate * 10;
				int num_seek_points = 1 + sample_count / seek_points_distance; // 1 seek point per 10 seconds
				if (sample_count % seek_points_distance == 0)
					num_seek_points--;
				seek_table = new SeekPoint[num_seek_points];
				for (int sp = 0; sp < num_seek_points; sp++)
				{
					seek_table[sp].framesize = 0;
					seek_table[sp].offset = 0;
					seek_table[sp].number = sp * seek_points_distance;
				}
			}

			// output header bytes
			header = new byte[eparams.padding_size + 1024 + (seek_table == null ? 0 : seek_table.Length * 18)];
			header_len = Write_headers();

            // initialize CRC & MD5
#pragma warning disable SCS0006
            if (_IO.CanSeek && _settings.DoMD5)
				md5 = new MD5CryptoServiceProvider();
#pragma warning restore SCS0006

            if (_settings.DoVerify)
			{
				verify = new FlakeReader(PCM);
				verifyBuffer = new int[Flake.MAX_BLOCKSIZE * channels];
			}

			frame_buffer = new byte[max_frame_size];

			return header_len;
		}

        public void Dispose()
        {
            Close();
        }
    }

	struct FlakeEncodeParams
	{
		// compression quality
		// set by user prior to calling flake_encode_init
		// standard values are 0 to 8
		// 0 is lower compression, faster encoding
		// 8 is higher compression, slower encoding
		// extended values 9 to 12 are slower and/or use
		// higher prediction orders
		public int compression;

		// prediction order selection method
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 5
		// 0 = use maximum order only
		// 1 = use estimation
		// 2 = 2-level
		// 3 = 4-level
		// 4 = 8-level
		// 5 = full search
		// 6 = log search
		public OrderMethod order_method;


		// stereo decorrelation method
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 2
		// 0 = independent L+R channels
		// 1 = mid-side encoding
		public StereoMethod stereo_method;

		public WindowMethod window_method;

		// block size in samples
		// set by the user prior to calling flake_encode_init
		// if set to 0, a block size is chosen based on block_time_ms
		// can also be changed by user before encoding a frame
		public int block_size;

		// block time in milliseconds
		// set by the user prior to calling flake_encode_init
		// used to calculate block_size based on sample rate
		// can also be changed by user before encoding a frame
		public int block_time_ms;

		// padding size in bytes
		// set by the user prior to calling flake_encode_init
		// if set to less than 0, defaults to 4096
		public int padding_size;

		// minimum LPC order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32
		public int min_prediction_order;

		// maximum LPC order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32
		public int max_prediction_order;

		// Number of LPC orders to try (for estimate mode)
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32
		public int estimation_depth;

		// minimum fixed prediction order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 4
		public int min_fixed_order;

		// maximum fixed prediction order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 4
		public int max_fixed_order;

		// type of linear prediction
		// set by user prior to calling flake_encode_init
		public PredictionType prediction_type;

		// minimum partition order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 8
		public int min_partition_order;

		// maximum partition order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 8
		public int max_partition_order;

		// whether to use variable block sizes
		// set by user prior to calling flake_encode_init
		// 0 = fixed block size
		// 1 = variable block size
		public int variable_block_size;

		// whether to try various lpc_precisions
		// 0 - use only one precision
		// 1 - try two precisions
		public int lpc_max_precision_search;

		public int lpc_min_precision_search;

		public WindowFunction window_function;

		public bool do_seektable;

		public int Flake_set_defaults(int lvl)
		{
			compression = lvl;

			if ((lvl < 0 || lvl > 12) && (lvl != 99))
			{
				return -1;
			}

			// default to level 5 params
			window_function = WindowFunction.Flattop | WindowFunction.Tukey;
			order_method = OrderMethod.Akaike;
			stereo_method = StereoMethod.Evaluate;
			window_method = WindowMethod.Evaluate;
			block_size = 0;
			block_time_ms = 105;
			prediction_type = PredictionType.Search;
			min_prediction_order = 1;
			max_prediction_order = 12;
			estimation_depth = 1;
			min_fixed_order = 2;
			max_fixed_order = 2;
			min_partition_order = 0;
			max_partition_order = 8;
			variable_block_size = 0;
			lpc_min_precision_search = 1;
			lpc_max_precision_search = 1;
			do_seektable = true;

			// differences from level 7
			switch (lvl)
			{
				case 0:
					block_time_ms = 53;
					prediction_type = PredictionType.Fixed;
					stereo_method = StereoMethod.Independent;
					max_partition_order = 6;
					break;
				case 1:
					prediction_type = PredictionType.Levinson;
					stereo_method = StereoMethod.Independent;
					window_function = WindowFunction.Bartlett;
					max_prediction_order = 8;
					max_partition_order = 6;
					break;
				case 2:
					stereo_method = StereoMethod.Independent;
					window_function = WindowFunction.Bartlett;
					max_partition_order = 6;
					break;
				case 3:
					stereo_method = StereoMethod.Estimate;
					window_function = WindowFunction.Bartlett;
					max_prediction_order = 8;
					break;
				case 4:
					stereo_method = StereoMethod.Estimate;
					window_function = WindowFunction.Bartlett;
					break;
				case 5:
					stereo_method = StereoMethod.Estimate;
					window_method = WindowMethod.Estimate;
					break;
				case 6:
					stereo_method = StereoMethod.Estimate;
					break;
				case 7:
					break;
				case 8:
					estimation_depth = 2;
					min_fixed_order = 0;
					lpc_min_precision_search = 0;
					break;
				case 9:
					window_function = WindowFunction.Bartlett;
					max_prediction_order = 32;
					break;
				case 10:
					min_fixed_order = 0;
					max_fixed_order = 4;
					max_prediction_order = 32;
					//lpc_max_precision_search = 2;
					break;
				case 11:
					min_fixed_order = 0;
					max_fixed_order = 4;
					max_prediction_order = 32;
					estimation_depth = 5;
					//lpc_max_precision_search = 2;
					variable_block_size = 4;
					break;
			}

			return 0;
		}
	}
}
