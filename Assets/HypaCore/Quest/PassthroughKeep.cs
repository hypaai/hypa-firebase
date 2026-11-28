using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace HypaCore.Quest
{
    /// <summary>
    /// The see-through view lives on its own object. The shell, Lens, and Home need it under their menus. A story starts
    /// in the forest, which draws its own sky, so the see-through view stays off until the camp enters the room or a door
    /// looks back into it. Home is an empty scene created without a load event, so it is restored when the story unloads.
    /// </summary>
    public static class PassthroughKeep
    {
        /// <summary>
        /// True when the see-through view can run under the app, without turning it on. "In my room" takes the sky, ground and
        /// trees away, so it needs this underneath. While see-through is off the SDK forgets a failed start, so this mostly
        /// means "supported": a start that fails is only known after turning it on.
        /// </summary>
        public static bool CanRun =>
            Object.FindAnyObjectByType<OVRManager>(FindObjectsInactive.Include) != null
            && OVRManager.IsInsightPassthroughSupported() && !OVRManager.HasInsightPassthroughInitFailed();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            SceneManager.sceneLoaded += (scene, _) => OnScene(scene.name);
            SceneManager.sceneUnloaded += OnUnloaded;
            OnScene(SceneManager.GetActiveScene().name);
        }

        /// <summary>A story world draws its own sky. Home is an empty scene, so it does not.</summary>
        static bool DrawsItsOwnSky(string scene) =>
            scene == HypaScenes.Story || scene == HypaScenes.StoryHut || scene == HypaScenes.StoryGathering;

        static void OnScene(string scene)
        {
            var managers = Object.FindObjectsByType<OVRManager>(FindObjectsInactive.Include);
            if (managers.Length == 0) return;
            foreach (var manager in managers) CapResolution(manager);
            Pace();

            if (scene == HypaScenes.Story)
                SetActive(false);
            else if (!DrawsItsOwnSky(scene))
                SetActive(true); // shell, Lens, and Home. A place loaded on the story keeps whatever the story chose
        }

        /// <summary>
        /// Home is created with <c>CreateScene</c>, which does not raise a load event. The story has just been unloaded,
        /// and that is what left the see-through view off.
        /// </summary>
        static void OnUnloaded(Scene _)
        {
            if (DrawsItsOwnSky(SceneManager.GetActiveScene().name)) return;
            if (Object.FindObjectsByType<OVRManager>(FindObjectsInactive.Include).Length == 0) return;
            SetActive(true);
        }

        /// <summary>Turns the see-through view on or off. Returns false when there is no headset manager to drive.</summary>
        public static bool SetActive(bool on)
        {
            var managers = Object.FindObjectsByType<OVRManager>(FindObjectsInactive.Include);
            if (managers.Length == 0) return false;
            if (!on)
            {
                foreach (var old in Object.FindObjectsByType<OVRPassthroughLayer>(FindObjectsInactive.Include))
                    Object.DestroyImmediate(old.gameObject);
                foreach (var manager in managers) manager.isInsightPassthroughEnabled = false;
                SetCameraAlpha(1f);
                return true;
            }

            foreach (var manager in managers) manager.isInsightPassthroughEnabled = true;
            if (Object.FindAnyObjectByType<OVRPassthroughLayer>() == null)
            {
                var host = new GameObject("Hypa Passthrough");
                Object.DontDestroyOnLoad(host);
                host.AddComponent<OVRPassthroughLayer>();
            }
            SetCameraAlpha(0f);
            return true;
        }

        static void SetCameraAlpha(float alpha)
        {
            var rig = Object.FindAnyObjectByType<OVRCameraRig>();
            var cameras = rig != null
                ? rig.GetComponentsInChildren<Camera>(true)
                : new[] { Camera.main };
            foreach (var cam in cameras)
            {
                if (cam == null) continue;
                cam.clearFlags = CameraClearFlags.SolidColor;
                var color = cam.backgroundColor;
                color.a = alpha;
                cam.backgroundColor = color;
            }
        }

        /// <summary>
        /// The story rig's prefab allows Quest 3 to render at 1.6× and Quest 2 at 1.3×. Keep the eye texture at native
        /// size. The headset can still scale the view down when a scene is heavy.
        /// </summary>
        static void CapResolution(OVRManager manager)
        {
            manager.quest2MaxDynamicResolutionScale = 1f;
            manager.quest3MaxDynamicResolutionScale = 1f;
            manager.maxDynamicResolutionScale = 1f;
            if (XRSettings.enabled && XRSettings.eyeTextureResolutionScale > 1f)
                XRSettings.eyeTextureResolutionScale = 1f;
        }

        /// <summary>
        /// Ask the headset for 72 Hz, high foveation, and sustained CPU and GPU. That is the store's floor for an
        /// interactive Quest app. The headset paces frames itself, so vSync and targetFrameRate are not what holds it.
        /// </summary>
        static void Pace()
        {
            OVRManager.suggestedCpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
            OVRManager.suggestedGpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
            OVRManager.foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.High;
            OVRManager.useDynamicFoveatedRendering = true;
            var display = OVRManager.display;
            if (display != null) display.displayFrequency = 72f;
        }
    }
}
