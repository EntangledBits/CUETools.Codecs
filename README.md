.net Standard port of CUETools.Codecs.FLAKE Original code from Gregory S. Chudov and Justin Ruggles

This is for encoding into FLAC.

This has not been tested much yet, I just converted it to .net standard and left much of the original code intact



```
private void ConvertToFlac(Stream sourceStream, Stream destinationStream)
        {
            var audioSource = new WAVReader(null, sourceStream);
            try
            {
                var buff = new AudioBuffer(audioSource, 0x10000);
                var flakeWriter = new FlakeWriter(null, destinationStream, audioSource.PCM);
                flakeWriter.CompressionLevel = 8;
                while (audioSource.Read(buff, -1) != 0)
                {
                    flakeWriter.Write(buff);
                }
                flakeWriter.Close();
            }
            finally
            {
                audioSource.Close();
            }
        }
```
