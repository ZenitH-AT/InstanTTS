using InstanTTS.Audio;
using InstanTTS.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace InstanTTS.Speech
{
    internal class SpeechSynthManager : IDisposable
    {
        // Hard constants for the system speech synthesizer based on the values it accepts.
        public const int MIN_RATE = -10;
        public const int MAX_RATE = 10;
        public const int MIN_VOLUME = 0;
        public const int MAX_VOLUME = 100;

        public const int DEFAULT_RATE = 0;
        public const int DEFAULT_VOLUME = 50;

        // Static instances of this class and installed system voices.
        public static SpeechSynthManager Instance { get; private set; }
        public static IReadOnlyCollection<InstalledVoice> Voices { get; private set; }

        /// <summary>
        /// Queue of TTS tasks. See <see cref="SpeechString"/> for the data contained in each entry.
        /// </summary>
        public ObservableQueue<SpeechString> TTSQueue { get; private set; }

        /// <summary>
        /// History of TTS tasks.
        /// </summary>
        public ObservableQueue<SpeechString> TTSHistory { get; private set; }
        

        // Queue sized is capped. This is only somewhat due to memory and mostly a weak attempt to
        // stop people from loading up a bevvy of TTS messages and leaving them to play.
        private const int MAX_QUEUE_SIZE = 5;

        /// <summary>
        /// Cancellation token used to cancel <see cref="_queueTask"/>.
        /// </summary>
        private readonly CancellationTokenSource _queueTaskCt;

        public SpeechSynthManager() 
        {
            TTSQueue = new ObservableQueue<SpeechString>();
            TTSHistory = new ObservableQueue<SpeechString>();
            _queueTaskCt = new CancellationTokenSource();
            var token = _queueTaskCt.Token;
            
            Task.Run(async () =>
            {
                while (true)
                {
                    // cancel if requested.
                    if (token.IsCancellationRequested)
                        return;

                    // if the queue has entries, play the next one.
                    if (TTSQueue.Count > 0)
                    {
                        // this will await until the TTS clip has finished playing.
                        SpeechString str = TTSQueue.Peek();
                        await Application.Current.Dispatcher.BeginInvoke(new Action(() => this.TTSQueue.Dequeue()));
                        await Speak(str);
                    }
                }
            }, token);

            // hack method to get installed voices because there's no direct method of getting these
            var synth = new SpeechSynthesizer();

            SpeechApiReflectionHelper.InjectOneCoreVoices(synth);

            Voices = synth.GetInstalledVoices();
            
            synth.Dispose();

            Instance = this;
        }

        /// <summary>
        /// Queues a TTS task if the queue isn't full.
        /// </summary>
        /// <param name="text">Text to speak.</param>
        /// <param name="device1">The <see cref="SoundDevice"/> to use when speaking.</param>
        /// <param name="voice">The voice to use.</param>
        /// <param name="rate">The rate to speak this text at. Clamped to [<see cref="MIN_RATE"/>, <see cref="MAX_RATE"/>].</param>
        /// <param name="volume">The volume to speak this text at. Clamped to [<see cref="MIN_VOLUME"/>, <see cref="MAX_VOLUME"/>].</param>
        public void QueueTTS(string text, SoundDevice device1, SoundDevice device2, InstalledVoice voice, int rate = DEFAULT_RATE, int volume = DEFAULT_VOLUME)
        {
            if (!IsQueueFull())
            {
                SpeechString data = new(text, voice, rate, volume, device1, device2);
                
                TTSQueue.Enqueue(data);
                TTSHistory.Enqueue(data);
            }
        }

        public bool IsQueueFull()
        {
            return TTSQueue.Count >= MAX_QUEUE_SIZE;
        }

        public int QueueSize()
        {
            return TTSQueue.Count;
        }

        /// <summary>
        /// Speaks the text in the provided <see cref="SpeechString"/> using the enclosed data.
        /// </summary>
        /// <returns>Nothing.</returns>
        private async Task Speak(SpeechString speech)
        {
            // create a new memory stream and speech synth, to be disposed of after this method executes.
            using MemoryStream stream = new();
            using SpeechSynthesizer synth = new();
            
            SpeechApiReflectionHelper.InjectOneCoreVoices(synth);

            // set synthesizer properties
            synth.SetOutputToWaveStream(stream);
            synth.Rate = speech.Rate;
            synth.Volume = speech.Volume;

            // TODO: refine the speech builder and actually use the style.
            PromptBuilder builder = new();
            PromptStyle style = new();

            builder.StartVoice(speech.Voice.VoiceInfo);
            builder.StartSentence();
            builder.AppendText(speech.Text);
            builder.EndSentence();
            builder.EndVoice();

            // "speaks" the text directly into the memory stream
            synth.Speak(builder);
            // then block while the speech is being played.
            await AudioManager.Instance.Play(stream, speech.PrimaryDevice.DeviceNumber, speech.SecondaryDevice.DeviceNumber);
        }

        public void Dispose()
        {
            // Make sure to clean up and cancel the queue task.
            _queueTaskCt.Cancel();
        }
    }
}
