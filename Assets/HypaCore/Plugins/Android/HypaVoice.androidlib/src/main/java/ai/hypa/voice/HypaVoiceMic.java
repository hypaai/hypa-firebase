package ai.hypa.voice;

import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.media.audiofx.AcousticEchoCanceler;
import android.media.audiofx.NoiseSuppressor;
import android.util.Log;

/**
 * The microphone in voice-call mode (G10): AudioRecord on VOICE_COMMUNICATION, which runs the device's echo canceller, plus
 * AcousticEchoCanceler and NoiseSuppressor where the device has them. 16-bit mono, read on its own thread into a ring of at
 * most four seconds; C# (AndroidVoiceMicSource) takes what has arrived as floats once a frame with read().
 * The app already holds RECORD_AUDIO; start() returns false without it, and C# falls back to Unity's Microphone.
 */
public final class HypaVoiceMic {
    private static final String TAG = "HypaVoiceMic";
    private static final Object LOCK = new Object();
    private static AudioRecord record;
    private static AcousticEchoCanceler aec;
    private static NoiseSuppressor ns;
    private static Thread thread;
    private static volatile boolean running;
    private static short[] ring = new short[0];
    private static int head, count;

    private HypaVoiceMic() { }

    public static boolean start(int rate) {
        stop();
        try {
            int min = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (min <= 0) return false;
            AudioRecord r = new AudioRecord(MediaRecorder.AudioSource.VOICE_COMMUNICATION, rate, AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT, Math.max(min, rate / 5 * 2));
            if (r.getState() != AudioRecord.STATE_INITIALIZED) { r.release(); return false; }
            if (AcousticEchoCanceler.isAvailable()) {
                aec = AcousticEchoCanceler.create(r.getAudioSessionId());
                if (aec != null) aec.setEnabled(true);
            }
            if (NoiseSuppressor.isAvailable()) {
                ns = NoiseSuppressor.create(r.getAudioSessionId());
                if (ns != null) ns.setEnabled(true);
            }
            synchronized (LOCK) { ring = new short[rate * 4]; head = 0; count = 0; }
            record = r;
            running = true;
            r.startRecording();
            thread = new Thread(HypaVoiceMic::pump, "HypaVoiceMic");
            thread.start();
            return true;
        } catch (Exception e) {
            Log.w(TAG, "voice-call input unavailable: " + e.getClass().getSimpleName());
            stop();
            return false;
        }
    }

    /** True when the platform echo canceller is on. */
    public static boolean echoCancelled() {
        return aec != null && aec.getEnabled();
    }

    private static void pump() {
        short[] buf = new short[640];
        while (running) {
            AudioRecord r = record;
            if (r == null) break;
            int n = r.read(buf, 0, buf.length);
            if (n <= 0) continue;
            synchronized (LOCK) {
                for (int i = 0; i < n; i++) {
                    ring[(head + count) % ring.length] = buf[i];
                    if (count < ring.length) count++; else head = (head + 1) % ring.length; // full: the oldest goes
                }
            }
        }
    }

    /** What has been recorded since the last call, as floats in -1..1 (empty when nothing new). */
    public static float[] read() {
        synchronized (LOCK) {
            float[] out = new float[count];
            for (int i = 0; i < count; i++) out[i] = ring[(head + i) % ring.length] / 32768f;
            head = ring.length == 0 ? 0 : (head + count) % ring.length;
            count = 0;
            return out;
        }
    }

    public static void stop() {
        running = false;
        Thread t = thread;
        thread = null;
        AudioRecord r = record;
        record = null;
        if (r != null) {
            try { r.stop(); } catch (Exception ignored) { }
        }
        if (t != null) {
            try { t.join(300); } catch (InterruptedException ignored) { }
        }
        if (r != null) r.release();
        if (aec != null) { aec.release(); aec = null; }
        if (ns != null) { ns.release(); ns = null; }
    }
}
