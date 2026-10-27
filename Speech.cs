using System;
using System.Linq;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Tolmach;

// Reads the translation aloud with the voices built into Windows.
internal static class Speech
{
    static MediaPlayer? _player;
    static SpeechSynthesizer? _synthesizer;

    public static async void Speak(string text, string language)
    {
        try
        {
            _synthesizer ??= new SpeechSynthesizer();
            _player ??= new MediaPlayer();
            _player.Pause();

            string code = language.Split('-')[0];
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith(code, StringComparison.OrdinalIgnoreCase));
            if (voice == null)
            {
                Log.Write($"No voice for {language}");
                return;
            }
            _synthesizer.Voice = voice;
            var stream = await _synthesizer.SynthesizeTextToStreamAsync(text);
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
        }
        catch (Exception ex)
        {
            Log.Write("Speech failed: " + ex.Message);
        }
    }

    public static bool HasVoice(string language)
    {
        string code = language.Split('-')[0];
        return SpeechSynthesizer.AllVoices.Any(v => v.Language.StartsWith(code, StringComparison.OrdinalIgnoreCase));
    }
}
