using System;

namespace CUETools.Codecs
{
    /// <summary>
    ///    This class provides an attribute for marking
    ///    classes that provide <see cref="IAudioDest" />.
    /// </summary>
    /// <remarks>
    ///    When plugins with classes that provide <see cref="IAudioDest" /> are
    ///    registered, their <see cref="AudioEncoderClass" /> attributes are read.
    /// </remarks>
    /// <example>
    ///    <code lang="C#">using CUETools.Codecs;
    ///
    ///[AudioEncoderClass("libFLAC", "flac", true, "0 1 2 3 4 5 6 7 8", "5", 1)]
    ///public class MyEncoder : IAudioDest {
    ///	...
    ///}</code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class AudioEncoderClass : Attribute
    {
        public string EncoderName { get; }

        public string Extension { get; }

        public string SupportedModes { get; }

        public string DefaultMode { get; }

        public bool Lossless { get; }

        public int Priority { get; }

        public Type Settings { get; }

        public AudioEncoderClass(string encoderName, string extension, bool lossless, string supportedModes, string defaultMode, int priority, Type settings)
        {
            EncoderName = encoderName;
            Extension = extension;
            SupportedModes = supportedModes;
            DefaultMode = defaultMode;
            Lossless = lossless;
            Priority = priority;
            Settings = settings;
        }
    }
}
