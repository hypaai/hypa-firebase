using System;
using System.Threading;
using System.Threading.Tasks;

namespace HypaCore.Hybrid
{
    public enum OnDeviceSetupState
    {
        /// <summary>No on-device model on this device: Quest, the editor, or a phone that cannot run one.</summary>
        Unavailable,
        /// <summary>The device can run it once the model has been downloaded.</summary>
        Downloadable,
        Downloading,
        Ready,
        /// <summary>The last download failed; <see cref="OnDeviceSetup.Problem"/> says why. It can be tried again.</summary>
        Failed,
    }

    /// <summary>
    /// What the settings screen and the route chip need to know about the on-device model, with no UI of its own: the state, a label,
    /// the download progress, and a way to start the download. A screen binds to <see cref="Changed"/> and reads the properties.
    /// Raised on the thread that changed it, so a Unity screen should copy the values on its own update.
    /// </summary>
    public sealed class OnDeviceSetup
    {
        readonly IOnDeviceModel _model;
        readonly object _gate = new object();
        OnDeviceSetupState _state = OnDeviceSetupState.Unavailable;
        float _progress;
        string _problem;
        bool _busy;

        public OnDeviceSetup(IOnDeviceModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public event Action Changed;

        public OnDeviceSetupState State { get { lock (_gate) return _state; } }

        /// <summary>0..1 while downloading.</summary>
        public float Progress { get { lock (_gate) return _progress; } }

        /// <summary>Plain words for the player when the last download failed, otherwise null.</summary>
        public string Problem { get { lock (_gate) return _problem; } }

        /// <summary>True when the settings screen should offer the download (and a retry after a failure).</summary>
        public bool CanDownload
        {
            get
            {
                lock (_gate) return !_busy && _model is IDownloadableOnDeviceModel && (_state == OnDeviceSetupState.Downloadable || _state == OnDeviceSetupState.Failed);
            }
        }

        /// <summary>The settings entry. The model's size is not known before the download starts, so it is not shown.</summary>
        public string Label
        {
            get
            {
                lock (_gate)
                {
                    switch (_state)
                    {
                        case OnDeviceSetupState.Downloadable: return "Get the on-device storyteller";
                        case OnDeviceSetupState.Downloading: return "Getting the on-device storyteller… " + (int)(_progress * 100f) + "%";
                        case OnDeviceSetupState.Ready: return "On-device storyteller ready";
                        case OnDeviceSetupState.Failed: return (_problem ?? "The download did not finish.") + " Tap to try again.";
                        default: return "This phone has no on-device model";
                    }
                }
            }
        }

        /// <summary>For the route chip when the on-device model was not used: why, in a few words. Empty when it is ready.</summary>
        public string RouteReason
        {
            get
            {
                lock (_gate)
                {
                    switch (_state)
                    {
                        case OnDeviceSetupState.Ready: return "";
                        case OnDeviceSetupState.Downloadable: return "the on-device model is not downloaded yet";
                        case OnDeviceSetupState.Downloading: return "the on-device model is still downloading";
                        case OnDeviceSetupState.Failed: return "the on-device model could not be downloaded";
                        default: return "this phone has no on-device model";
                    }
                }
            }
        }

        /// <summary>Reads the model's status again. Call it at start-up, when the app returns, and after a download.</summary>
        public async Task RefreshAsync()
        {
            var status = await _model.StatusAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_busy) return; // a download in progress owns the state
                _state = FromStatus(status, _state);
                if (_state != OnDeviceSetupState.Failed) _problem = null;
            }
            Changed?.Invoke();
        }

        /// <summary>Starts the download. Does nothing if it is not offered or is already running. Never throws: a failure is the state.</summary>
        public async Task DownloadAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_busy || !(_model is IDownloadableOnDeviceModel) || (_state != OnDeviceSetupState.Downloadable && _state != OnDeviceSetupState.Failed)) return;
                _busy = true;
                _state = OnDeviceSetupState.Downloading;
                _progress = 0f;
                _problem = null;
            }
            Changed?.Invoke();

            try
            {
                await ((IDownloadableOnDeviceModel)_model).DownloadAsync(new ProgressReport(this), ct).ConfigureAwait(false);
                var status = await _model.StatusAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _busy = false;
                    _state = status == "available" ? OnDeviceSetupState.Ready : FromStatus(status, OnDeviceSetupState.Downloadable);
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate) { _busy = false; _state = OnDeviceSetupState.Downloadable; }
            }
            catch (OnDeviceModelException e)
            {
                lock (_gate) { _busy = false; _state = OnDeviceSetupState.Failed; _problem = Describe(e.Reason); }
            }
            catch (Exception)
            {
                lock (_gate) { _busy = false; _state = OnDeviceSetupState.Failed; _problem = Describe("error"); }
            }
            Changed?.Invoke();
        }

        static OnDeviceSetupState FromStatus(string status, OnDeviceSetupState current)
        {
            switch (status)
            {
                case "available": return OnDeviceSetupState.Ready;
                case "downloadable": return current == OnDeviceSetupState.Failed ? current : OnDeviceSetupState.Downloadable;
                case "downloading": return OnDeviceSetupState.Downloading;
                default: return OnDeviceSetupState.Unavailable;
            }
        }

        static string Describe(string reason)
        {
            switch (reason)
            {
                case "no-space": return "There is not enough space on this phone.";
                case "needs-update": return "This phone needs a system update first.";
                case "busy":
                case "quota":
                case "background": return "The phone is busy right now.";
                case "unavailable": return "This phone cannot use the on-device model.";
                default: return "The download did not finish.";
            }
        }

        sealed class ProgressReport : IProgress<float>
        {
            readonly OnDeviceSetup _owner;
            public ProgressReport(OnDeviceSetup owner) { _owner = owner; }

            public void Report(float value)
            {
                lock (_owner._gate) _owner._progress = value < 0f ? 0f : value > 1f ? 1f : value;
                _owner.Changed?.Invoke();
            }
        }
    }
}
