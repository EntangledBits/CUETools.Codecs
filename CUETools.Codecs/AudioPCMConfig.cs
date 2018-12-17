namespace CUETools.Codecs
{
    public class AudioPCMConfig
    {
        public static readonly AudioPCMConfig RedBook = new AudioPCMConfig(16, 2, 44100);

        public int BitsPerSample { get; }
        public int ChannelCount { get; }
        public int SampleRate { get; }
        public int BlockAlign { get { return ChannelCount * ((BitsPerSample + 7) / 8); } }
        public bool IsRedBook { get { return BitsPerSample == 16 && ChannelCount == 2 && SampleRate == 44100; } }

        public AudioPCMConfig(int bitsPerSample, int channelCount, int sampleRate)
        {
            BitsPerSample = bitsPerSample;
            ChannelCount = channelCount;
            SampleRate = sampleRate;
        }
    }
}
