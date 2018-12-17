﻿using System;
using System.Collections.Generic;
using System.IO;

namespace CUETools.Codecs
{
    [AudioEncoderClass("builtin wav", "wav", true, "", "", 10, typeof(object))]
    public class WAVWriter : IAudioDest
    {
        private Stream _IO;
        private BinaryWriter _bw;
        private long hdrLen = 0;
        private bool _headersWritten = false;
        private long _finalSampleCount = -1;
        private List<byte[]> _chunks = null;
        private List<uint> _chunkFCCs = null;

        public long Position { get; private set; }

        public long FinalSampleCount
        {
            set { _finalSampleCount = value; }
        }

        public long BlockSize
        {
            set { }
        }

        public int CompressionLevel
        {
            get { return 0; }
            set { }
        }

        public object Settings
        {
            get
            {
                return null;
            }
            set
            {
                if (value != null && value.GetType() != typeof(object))
                    throw new Exception("Unsupported options " + value);
            }
        }

        public long Padding
        {
            set { }
        }

        public AudioPCMConfig PCM { get; }

        public string Path { get; }

        public WAVWriter(string path, Stream IO, AudioPCMConfig pcm)
        {
            PCM = pcm;
            Path = path;
            _IO = IO ?? new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _bw = new BinaryWriter(_IO);
        }

        public WAVWriter(string path, AudioPCMConfig pcm)
            : this(path, null, pcm)
        {
        }

        public void WriteChunk(uint fcc, byte[] data)
        {
            if (Position > 0)
                throw new Exception("data already written, no chunks allowed");
            if (_chunks == null)
            {
                _chunks = new List<byte[]>();
                _chunkFCCs = new List<uint>();
            }
            _chunkFCCs.Add(fcc);
            _chunks.Add(data);
            hdrLen += 8 + data.Length + (data.Length & 1);
        }

        private void WriteHeaders()
        {
            const uint fccRIFF = 0x46464952;
            const uint fccWAVE = 0x45564157;
            const uint fccFormat = 0x20746D66;
            const uint fccData = 0x61746164;

            bool wavex = PCM.BitsPerSample != 16 && PCM.BitsPerSample != 24;

            hdrLen += 36 + (wavex ? 24 : 0) + 8;

            uint dataLen = (uint)(_finalSampleCount * PCM.BlockAlign);
            uint dataLenPadded = dataLen + (dataLen & 1);

            _bw.Write(fccRIFF);
            _bw.Write((uint)(dataLenPadded + hdrLen - 8));
            _bw.Write(fccWAVE);
            _bw.Write(fccFormat);
            if (wavex)
            {
                _bw.Write((uint)40);
                _bw.Write((ushort)0xfffe); // WAVEX follows
            }
            else
            {
                _bw.Write((uint)16);
                _bw.Write((ushort)1); // PCM
            }
            _bw.Write((ushort)PCM.ChannelCount);
            _bw.Write((uint)PCM.SampleRate);
            _bw.Write((uint)(PCM.SampleRate * PCM.BlockAlign));
            _bw.Write((ushort)PCM.BlockAlign);
            _bw.Write((ushort)((PCM.BitsPerSample + 7) / 8 * 8));
            if (wavex)
            {
                _bw.Write((ushort)22); // length of WAVEX structure
                _bw.Write((ushort)PCM.BitsPerSample);
                _bw.Write((uint)3); // speaker positions (3 == stereo)
                _bw.Write((ushort)1); // PCM
                _bw.Write((ushort)0);
                _bw.Write((ushort)0);
                _bw.Write((ushort)0x10);
                _bw.Write((byte)0x80);
                _bw.Write((byte)0x00);
                _bw.Write((byte)0x00);
                _bw.Write((byte)0xaa);
                _bw.Write((byte)0x00);
                _bw.Write((byte)0x38);
                _bw.Write((byte)0x9b);
                _bw.Write((byte)0x71);
            }
            if (_chunks != null)
                for (int i = 0; i < _chunks.Count; i++)
                {
                    _bw.Write(_chunkFCCs[i]);
                    _bw.Write((uint)_chunks[i].Length);
                    _bw.Write(_chunks[i]);
                    if ((_chunks[i].Length & 1) != 0)
                        _bw.Write((byte)0);
                }

            _bw.Write(fccData);
            _bw.Write(dataLen);

            _headersWritten = true;
        }

        public void Close()
        {
            if (_finalSampleCount <= 0)
            {
                const long maxFileSize = 0x7FFFFFFEL;
                long dataLen = Position * PCM.BlockAlign;
                if ((dataLen & 1) == 1)
                    _bw.Write((byte)0);
                if (dataLen + hdrLen > maxFileSize)
                    dataLen = ((maxFileSize - hdrLen) / PCM.BlockAlign) * PCM.BlockAlign;
                long dataLenPadded = dataLen + (dataLen & 1);

                _bw.Seek(4, SeekOrigin.Begin);
                _bw.Write((uint)(dataLenPadded + hdrLen - 8));

                _bw.Seek((int)hdrLen - 4, SeekOrigin.Begin);
                _bw.Write((uint)dataLen);
            }

            _bw.Close();

            _bw = null;
            _IO = null;

            if (_finalSampleCount > 0 && Position != _finalSampleCount)
                throw new Exception("Samples written differs from the expected sample count.");
        }

        public void Delete()
        {
            _bw.Close();
            _bw = null;
            _IO = null;
            File.Delete(Path);
        }

        public void Write(AudioBuffer buff)
        {
            if (buff.Length == 0)
                return;
            buff.Prepare(this);
            if (!_headersWritten)
                WriteHeaders();
            _IO.Write(buff.Bytes, 0, buff.ByteLength);
            Position += buff.Length;
        }
    }
}
