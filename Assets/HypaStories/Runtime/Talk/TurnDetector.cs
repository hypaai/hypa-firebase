namespace HypaStories
{
    /// <summary>
    /// Voice activity for an open conversation (G10): when the player starts and stops a turn, and when they talk over her.
    /// Energy against a noise floor: at least <see cref="SilenceDetector"/>'s speech threshold, raised by the room's own level (the
    /// fire and wind, a fan), which is learned for <see cref="LearnMs"/> before any turn can start and kept up to date between turns.
    /// A turn starts after <see cref="OnsetMs"/> of speech and ends after <see cref="EndSilenceMs"/> of quiet (700 ms, the ticket's
    /// figure); within a turn, quiet is anything under <see cref="VoiceShare"/> of the player's own voice, and a click shorter than
    /// <see cref="BlipMs"/> doesn't count as speech. <see cref="Finish"/> ends it at once (the panel's Done). While she speaks, the player must be <see cref="EchoFactor"/> times louder for <see cref="BargeInMs"/> to
    /// interrupt her, so her own voice from the speaker does not (Quest: the speakers sit by the microphone, so raise
    /// <see cref="EchoFactor"/> there). Her voice counts as hers for <see cref="TailMs"/> after it stops (the room's echo, the gap
    /// between two of her sentences), so its tail never starts a turn. Where the microphone has no echo canceller (the editor, a
    /// desktop), <see cref="CanBargeIn"/> is off: nothing heard while she speaks counts, and she is never answered with her own words.
    /// Pure, unit tested.
    /// </summary>
    public sealed class TurnDetector
    {
        public enum Signal { None, Started, Ended, Dropped, BargeIn }

        public float Threshold = new SilenceDetector().Threshold;
        public float OnsetMs = 120f, MinSpeechMs = 250f, EndSilenceMs = 700f, BargeInMs = 300f, MaxTurnMs = 15000f;
        public float EchoFactor = 2.5f, FloorFactor = 3f;
        /// <summary>A gap this long in loud speech restarts the barge-in count (a word's pause does not).</summary>
        public float BargeGapMs = 150f;
        /// <summary>How long after her voice stops it still counts as hers.</summary>
        public float TailMs = 600f;
        /// <summary>False where the microphone has no echo canceller: she can't be talked over, and nothing is heard while she speaks.</summary>
        public bool CanBargeIn = true;
        /// <summary>How long the room is listened to before a turn can start, and how fast the floor follows it up and down.</summary>
        public float LearnMs = 800f, FloorRiseMs = 1500f, FloorFallMs = 150f;
        /// <summary>In a turn, a sound under this share of the player's voice (its recent peak) is quiet: the room, not them.</summary>
        public float VoiceShare = 0.25f;
        /// <summary>Speech this short doesn't end the quiet (a crackle of the fire, a click).</summary>
        public float BlipMs = 60f;

        public bool InTurn { get; private set; }
        /// <summary>The learned background level (RMS), 0 until something has been heard.</summary>
        public float Floor { get; private set; }
        /// <summary>She is speaking, or stopped less than <see cref="TailMs"/> ago (as of the last update).</summary>
        public bool HerTurn { get; private set; }

        float _onset, _speech, _quiet, _turn, _loud, _gap, _run, _voice, _learned, _sinceHer = float.MaxValue;

        /// <summary>The level speech must reach now.</summary>
        public float Level(bool sheSpeaks)
        {
            var t = Threshold > Floor * FloorFactor ? Threshold : Floor * FloorFactor;
            return sheSpeaks ? t * EchoFactor : t;
        }

        /// <summary>Feed the loudness (RMS) of the last <paramref name="dtMs"/>, and whether she is speaking.</summary>
        public Signal Update(float rms, float dtMs, bool sheSpeaks)
        {
            _sinceHer = sheSpeaks ? 0f : _sinceHer + dtMs;
            var her = HerTurn = sheSpeaks || _sinceHer < TailMs;
            // the player's own turn is measured against her actual voice: her tail must not cut it short
            var loud = rms > Level(InTurn ? sheSpeaks : her);
            if (!sheSpeaks && (rms < Floor || !InTurn))
            {
                // the room's level: down quickly (any pause, even mid-turn), up slowly between turns, and ten times slower over
                // anything loud once the room is known (the start of a word must not teach it that words are the room)
                var ms = rms < Floor ? FloorFallMs : loud && _learned >= LearnMs ? FloorRiseMs * 10f : FloorRiseMs;
                Floor += (rms - Floor) * System.Math.Min(1f, dtMs / ms);
                if (!InTurn) _learned += dtMs;
            }

            if (her && !InTurn)
            {
                if (!CanBargeIn) { _onset = _loud = _gap = 0f; return Signal.None; }
                if (loud) { _loud += dtMs; _gap = 0f; }
                else if ((_gap += dtMs) > BargeGapMs) _loud = 0f;
                if (_loud < BargeInMs) return Signal.None;
                Begin(_loud, rms);
                return Signal.BargeIn;
            }
            _loud = 0f; _gap = 0f;

            if (!InTurn)
            {
                _onset = loud && _learned >= LearnMs ? _onset + dtMs : 0f;
                if (_onset < OnsetMs) return Signal.None;
                Begin(_onset, rms);
                return Signal.Started;
            }

            _turn += dtMs;
            _voice *= (float)System.Math.Pow(0.5, dtMs / 1500.0); // the voice's recent peak, halving every 1.5 s
            var speech = loud && rms > _voice * VoiceShare;
            if (speech)
            {
                if (rms > _voice) _voice = rms;
                _speech += dtMs;
                _run += dtMs;
                if (_run >= BlipMs) _quiet = 0f;
                else _quiet += dtMs;
            }
            else { _run = 0f; _quiet += dtMs; }
            if (_quiet > EndSilenceMs || _turn > MaxTurnMs)
            {
                var enough = _speech >= MinSpeechMs;
                Reset();
                return enough ? Signal.Ended : Signal.Dropped;
            }
            return Signal.None;
        }

        void Begin(float heardMs, float rms)
        {
            InTurn = true;
            _speech = heardMs; _turn = heardMs; _run = heardMs; _voice = rms; _quiet = 0f; _onset = 0f; _loud = 0f; _gap = 0f;
        }

        /// <summary>The player says they are done (the panel's Done): <see cref="Signal.Ended"/> now, or <see cref="Signal.Dropped"/> if too little was said.</summary>
        public Signal Finish()
        {
            if (!InTurn) return Signal.None;
            var enough = _speech >= MinSpeechMs;
            Reset();
            return enough ? Signal.Ended : Signal.Dropped;
        }

        /// <summary>Forget the turn in progress (the conversation is held for an item's story).</summary>
        public void Reset()
        {
            InTurn = false;
            _onset = _speech = _quiet = _turn = _loud = _gap = _run = _voice = 0f;
        }
    }
}
