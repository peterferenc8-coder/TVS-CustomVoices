using System;
using System.IO;
using UnityEngine;

namespace TVSCustomVoices
{
    // Minimal RIFF/WAVE -> AudioClip loader. Supports PCM 8/16/24/32-bit and
    // IEEE float 32-bit, mono or multi-channel. Runs synchronously on the main
    // thread (AudioClip.Create must be called from the main thread).
    internal static class WavLoader
    {
        public static AudioClip Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Parse(bytes, Path.GetFileNameWithoutExtension(path));
        }

        private static AudioClip Parse(byte[] b, string name)
        {
            if (b.Length < 12 ||
                b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' ||
                b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
                throw new Exception("not a RIFF/WAVE file");

            int audioFormat = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
            int dataOffset = -1, dataLength = 0;

            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = new string(new[] { (char)b[p], (char)b[p + 1], (char)b[p + 2], (char)b[p + 3] });
                int size = BitConverter.ToInt32(b, p + 4);
                int body = p + 8;

                if (id == "fmt ")
                {
                    audioFormat = BitConverter.ToInt16(b, body);
                    channels = BitConverter.ToInt16(b, body + 2);
                    sampleRate = BitConverter.ToInt32(b, body + 4);
                    bitsPerSample = BitConverter.ToInt16(b, body + 14);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = Math.Min(size, b.Length - body);
                }

                p = body + size + (size & 1); // chunks are word-aligned
            }

            if (dataOffset < 0) throw new Exception("no data chunk");
            if (channels <= 0 || sampleRate <= 0) throw new Exception("bad fmt chunk");

            const int FORMAT_PCM = 1, FORMAT_FLOAT = 3;
            float[] samples;

            if (audioFormat == FORMAT_FLOAT && bitsPerSample == 32)
            {
                int n = dataLength / 4;
                samples = new float[n];
                for (int i = 0; i < n; i++)
                    samples[i] = BitConverter.ToSingle(b, dataOffset + i * 4);
            }
            else if (audioFormat == FORMAT_PCM)
            {
                int bytesPerSample = bitsPerSample / 8;
                if (bytesPerSample < 1) throw new Exception("bad bitsPerSample");
                int n = dataLength / bytesPerSample;
                samples = new float[n];
                switch (bitsPerSample)
                {
                    case 8: // unsigned
                        for (int i = 0; i < n; i++)
                            samples[i] = (b[dataOffset + i] - 128) / 128f;
                        break;
                    case 16:
                        for (int i = 0; i < n; i++)
                            samples[i] = BitConverter.ToInt16(b, dataOffset + i * 2) / 32768f;
                        break;
                    case 24:
                        for (int i = 0; i < n; i++)
                        {
                            int o = dataOffset + i * 3;
                            int v = (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16));
                            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000); // sign-extend
                            samples[i] = v / 8388608f;
                        }
                        break;
                    case 32:
                        for (int i = 0; i < n; i++)
                            samples[i] = BitConverter.ToInt32(b, dataOffset + i * 4) / 2147483648f;
                        break;
                    default:
                        throw new Exception($"unsupported PCM bit depth: {bitsPerSample}");
                }
            }
            else
            {
                throw new Exception($"unsupported WAV format (audioFormat={audioFormat}, bits={bitsPerSample}). Use PCM or 32-bit float.");
            }

            int frames = samples.Length / channels;
            if (frames <= 0) throw new Exception("empty audio data");

            var clip = AudioClip.Create(name, frames, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
