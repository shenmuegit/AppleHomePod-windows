namespace AirFlash.Core;

public static class SetupConfirmationPcm
{
    public static float[] Create(int sampleRate, int channels)
    {
        if (sampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        var frames = (sampleRate * 3 + 1) / 2;
        var pcm = new float[frames * channels];
        var secondStart = sampleRate * 4 / 5;
        WriteNote(0, sampleRate * 7 / 10, 523.25);
        WriteNote(secondStart, frames - secondStart, 659.25);
        return pcm;

        void WriteNote(int start, int count, double frequency)
        {
            for (var frame = 0; frame < count; frame++)
            {
                var seconds = (double)frame / sampleRate;
                var attack = Math.Min(1, seconds / .03);
                var release = Math.Min(1, (double)(count - 1 - frame) / sampleRate / .12);
                var envelope = (.5 - .5 * Math.Cos(Math.PI * attack)) * (.5 - .5 * Math.Cos(Math.PI * release));
                var sample = (float)(.14 * envelope * Math.Exp(-seconds) * Math.Sin(2 * Math.PI * frequency * seconds));
                for (var channel = 0; channel < channels; channel++) pcm[(start + frame) * channels + channel] = sample;
            }
        }
    }
}
