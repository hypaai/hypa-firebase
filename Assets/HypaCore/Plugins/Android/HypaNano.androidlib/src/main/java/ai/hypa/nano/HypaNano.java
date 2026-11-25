package ai.hypa.nano;

import com.google.common.util.concurrent.ListenableFuture;
import com.google.mlkit.genai.common.DownloadCallback;
import com.google.mlkit.genai.common.FeatureStatus;
import com.google.mlkit.genai.common.GenAiException;
import com.google.mlkit.genai.common.StreamingCallback;
import com.google.mlkit.genai.prompt.GenerateContentRequest;
import com.google.mlkit.genai.prompt.Generation;
import com.google.mlkit.genai.prompt.TextPart;
import com.google.mlkit.genai.prompt.java.GenerativeModelFutures;

import java.util.concurrent.CancellationException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.TimeUnit;

/**
 * Hypa bridge to Gemini Nano through Google ML Kit's GenAI Prompt API (AICore). Called from C# (HypaCore.Hybrid.AndroidNanoBridge).
 * Never call it on the UI thread: status() waits for the device. Answers come on ML Kit's threads, so the listeners must be thread-safe.
 *
 * Nothing here throws to the caller. A phone with no AICore, an unlocked bootloader, and Quest (Android without AICore) all
 * come back as status "unavailable", and a failed call reports a short reason ("busy", "background", "no-space", ...) to the listener.
 * The prompt and the reply are held in memory only for the call.
 */
public final class HypaNano {
    /** Receives a streamed reply. Exactly one of onDone and onError is called, last. */
    public interface TextListener {
        void onText(String chunk);
        void onDone();
        void onError(String reason);
    }

    /** Receives the model download. Exactly one of onDone and onError is called, last. */
    public interface DownloadListener {
        void onStarted(long totalBytes);
        void onProgress(long downloadedBytes);
        void onDone();
        void onError(String reason);
    }

    private static final long STATUS_TIMEOUT_SECONDS = 8;
    private static final Executor DIRECT = new Executor() {
        @Override public void execute(Runnable command) { command.run(); }
    };

    private static GenerativeModelFutures client;
    private static ListenableFuture<?> inFlight;

    private HypaNano() {}

    /** True when the ML Kit classes are in the app. False in a build that left them out. */
    public static boolean isAvailable() {
        try {
            Class.forName("com.google.mlkit.genai.prompt.Generation");
            return true;
        } catch (Throwable t) {
            return false;
        }
    }

    private static synchronized GenerativeModelFutures model() {
        if (client == null) client = GenerativeModelFutures.from(Generation.INSTANCE.getClient());
        return client;
    }

    /** "available", "downloadable", "downloading" or "unavailable". Blocks for up to a few seconds. */
    public static String status() {
        try {
            Integer status = model().checkStatus().get(STATUS_TIMEOUT_SECONDS, TimeUnit.SECONDS);
            if (status == null) return "unavailable";
            switch (status) {
                case FeatureStatus.AVAILABLE: return "available";
                case FeatureStatus.DOWNLOADABLE: return "downloadable";
                case FeatureStatus.DOWNLOADING: return "downloading";
                default: return "unavailable";
            }
        } catch (Throwable t) {
            return "unavailable";
        }
    }

    /** Starts the model download. Progress and the end come to the listener. */
    public static void download(final DownloadListener listener) {
        try {
            final ListenableFuture<Void> done = model().download(new DownloadCallback() {
                @Override public void onDownloadStarted(long totalBytes) { listener.onStarted(totalBytes); }
                @Override public void onDownloadProgress(long downloadedBytes) { listener.onProgress(downloadedBytes); }
                @Override public void onDownloadCompleted() { listener.onDone(); }
                @Override public void onDownloadFailed(GenAiException e) { listener.onError(reason(e)); }
            });
            done.addListener(new Runnable() {
                @Override public void run() {
                    try {
                        done.get();
                    } catch (Throwable t) {
                        listener.onError(reason(t));
                    }
                }
            }, DIRECT);
        } catch (Throwable t) {
            listener.onError(reason(t));
        }
    }

    /**
     * Streams a reply. A call that starts while another is running ends the earlier one first.
     * @param maxTokens 0 for the model's own limit
     * @param temperature below 0 for the model's own setting
     */
    public static void generate(String prompt, int maxTokens, float temperature, final TextListener listener) {
        try {
            cancel();
            GenerateContentRequest.Builder request = new GenerateContentRequest.Builder(new TextPart(prompt));
            if (maxTokens > 0) request.setMaxOutputTokens(maxTokens);
            if (temperature >= 0f) request.setTemperature(temperature);

            final ListenableFuture<?> future = model().generateContent(request.build(), new StreamingCallback() {
                @Override public void onNewText(String text) { listener.onText(text); }
            });
            synchronized (HypaNano.class) { inFlight = future; }
            future.addListener(new Runnable() {
                @Override public void run() {
                    synchronized (HypaNano.class) { if (inFlight == future) inFlight = null; }
                    try {
                        future.get();
                        listener.onDone();
                    } catch (Throwable t) {
                        listener.onError(reason(t));
                    }
                }
            }, DIRECT);
        } catch (Throwable t) {
            listener.onError(reason(t));
        }
    }

    /** Stops the reply in progress, if any. Its listener gets onError("cancelled"). */
    public static void cancel() {
        ListenableFuture<?> running;
        synchronized (HypaNano.class) {
            running = inFlight;
            inFlight = null;
        }
        if (running != null) running.cancel(true);
    }

    /** A short reason for the caller, never the message: it could carry the prompt. */
    static String reason(Throwable t) {
        Throwable cause = t;
        if (t instanceof ExecutionException && t.getCause() != null) cause = t.getCause();
        if (cause instanceof CancellationException) return "cancelled";
        if (cause instanceof GenAiException) {
            switch (((GenAiException) cause).getErrorCode()) {
                case GenAiException.ErrorCode.CANCELLED: return "cancelled";
                case GenAiException.ErrorCode.BUSY: return "busy";
                case GenAiException.ErrorCode.BACKGROUND_USE_BLOCKED: return "background";
                case GenAiException.ErrorCode.PER_APP_BATTERY_USE_QUOTA_EXCEEDED: return "quota";
                case GenAiException.ErrorCode.NOT_ENOUGH_DISK_SPACE: return "no-space";
                case GenAiException.ErrorCode.NEEDS_SYSTEM_UPDATE: return "needs-update";
                case GenAiException.ErrorCode.AICORE_INCOMPATIBLE: return "needs-update";
                case GenAiException.ErrorCode.NOT_AVAILABLE: return "unavailable";
                case GenAiException.ErrorCode.NOT_SUPPORTED: return "unavailable";
                case GenAiException.ErrorCode.REQUEST_TOO_LARGE: return "too-large";
                case GenAiException.ErrorCode.REQUEST_TOO_SMALL: return "too-small";
                default: return "error";
            }
        }
        return "error";
    }
}
