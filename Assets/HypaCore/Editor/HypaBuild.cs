using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor;
using UnityEditor.Android; // IPostGenerateGradleAndroidProject (core editor, no Android module needed)
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace HypaCore.EditorTools
{
    /// <summary>
    /// Builds Hypa Stories for Quest, Android phones, iOS and the web, from the Hypa > Build menu or the command line
    /// (tools/build.sh). Output goes to Builds/&lt;Target&gt;. Phone, iOS and web builds are "flat": they define
    /// <see cref="FlatDefine"/> so the Quest assemblies drop out, the Meta camera rig is stripped from the built scenes,
    /// XR does not start, and the mouse or touch pointer is the input. Quest and Android phone share Unity's Android
    /// settings, so the phone build swaps the Quest-only ones for the build and puts them back after.
    /// </summary>
    public static class HypaBuild
    {
        public const string FlatDefine = "HYPA_FLAT";
        const string PhoneId = "ai.hypa.stories.hybrid";
        const AndroidSdkVersions PhoneMinSdk = AndroidSdkVersions.AndroidApiLevel29; // ML Kit's minimum

        public enum Target { Quest, Android, IOS, Web }

        /// <summary>True while a flat build runs; read by the scene and manifest processors below.</summary>
        public static bool Flat { get; private set; }

        [MenuItem("Hypa/Build/Quest")] static void BuildQuest() => Build(Target.Quest);
        [MenuItem("Hypa/Build/Android Phone")] static void BuildAndroid() => Build(Target.Android);
        [MenuItem("Hypa/Build/iOS (Xcode project)")] static void BuildIos() => Build(Target.IOS);
        [MenuItem("Hypa/Build/Web")] static void BuildWeb() => Build(Target.Web);

        /// <summary>Unity -batchmode -executeMethod HypaCore.EditorTools.HypaBuild.FromCommandLine -hypaTarget quest|android|ios|web</summary>
        public static void FromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-hypaTarget");
            if (i < 0 || i + 1 >= args.Length || !Enum.TryParse(args[i + 1], true, out Target target))
            {
                Debug.LogError("Hypa: pass -hypaTarget quest|android|ios|web");
                EditorApplication.Exit(2);
                return;
            }
            if (target == Target.Quest || target == Target.Android) UseAndroidToolsFromEnvironment();
            EditorApplication.Exit(Build(target) ? 0 : 1);
        }

        /// <summary>
        /// Command-line builds (CI, or a machine with Android Studio's SDK instead of Unity's): ANDROID_HOME,
        /// ANDROID_NDK_ROOT and JAVA_HOME, when set, become Unity's External Tools paths. Set through reflection so this
        /// file still compiles without Android Build Support.
        /// </summary>
        static void UseAndroidToolsFromEnvironment()
        {
            var tools = Type.GetType("UnityEditor.Android.AndroidExternalToolsSettings, UnityEditor.Android.Extensions");
            foreach (var (property, variable) in new[] { ("sdkRootPath", "ANDROID_HOME"), ("ndkRootPath", "ANDROID_NDK_ROOT"), ("jdkRootPath", "JAVA_HOME") })
            {
                var value = Environment.GetEnvironmentVariable(variable);
                if (tools != null && !string.IsNullOrEmpty(value)) tools.GetProperty(property)?.SetValue(null, value);
            }
        }

        public static bool Build(Target target)
        {
            var buildTarget = target switch
            {
                Target.IOS => BuildTarget.iOS,
                Target.Web => BuildTarget.WebGL,
                _ => BuildTarget.Android,
            };
            var group = BuildPipeline.GetBuildTargetGroup(buildTarget);
            if (!BuildPipeline.IsBuildTargetSupported(group, buildTarget))
            {
                Debug.LogError($"Hypa: {buildTarget} Build Support is not installed (Unity Hub > Installs > Add modules).");
                return false;
            }
            if (EditorUserBuildSettings.activeBuildTarget != buildTarget && !EditorUserBuildSettings.SwitchActiveBuildTarget(group, buildTarget))
                return false;

            var output = Path.Combine("Builds", target.ToString());
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled && s.path != "Assets/HypaCore/Scenes/HypaCoreTest.unity"
                    && (target == Target.Quest || s.path != "Assets/HypaLens/Scenes/HypaLens.unity")).Select(s => s.path).ToArray(),
                target = buildTarget,
                targetGroup = group,
                locationPathName = buildTarget == BuildTarget.Android ? Path.Combine(output, "HypaStories.apk") : output,
                extraScriptingDefines = target == Target.Quest ? null : new[] { FlatDefine },
            };

            var manager = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group)?.Manager;
            var loaders = manager != null ? manager.activeLoaders.ToList() : new List<UnityEngine.XR.Management.XRLoader>();
            var androidId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            var androidMinSdk = PlayerSettings.Android.minSdkVersion;
            var androidGraphics = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            var phone = target == Target.Android;
            Flat = target != Target.Quest;
            try
            {
                if (Flat) foreach (var loader in loaders) manager.TryRemoveLoader(loader);
                if (phone)
                {
                    PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PhoneId);
                    PlayerSettings.Android.minSdkVersion = PhoneMinSdk;
                    // Quest is Vulkan only; phones without Vulkan (and the emulator) fall back to OpenGL ES 3
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
                }
                var summary = BuildPipeline.BuildPlayer(options).summary;
                Debug.Log($"Hypa: {target} build {summary.result}: {summary.outputPath} ({summary.totalErrors} errors, {summary.totalTime:mm\\:ss})");
                return summary.result == BuildResult.Succeeded;
            }
            finally
            {
                if (Flat) foreach (var loader in loaders) manager.TryAddLoader(loader);
                if (phone)
                {
                    PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, androidId);
                    PlayerSettings.Android.minSdkVersion = androidMinSdk;
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, androidGraphics);
                }
                Flat = false;
                AssetDatabase.SaveAssets(); // write the restored settings now: batch-mode Unity can abort on exit before saving
            }
        }
    }

    /// <summary>
    /// Flat builds: removes Meta XR components (camera rig, passthrough, hands) and Hypa's Quest components from the
    /// built copy of each scene. The plain camera stays; the app already seats it at eye height when nothing tracks it.
    /// </summary>
    sealed class StripQuestFromFlatScenes : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || !HypaBuild.Flat) return; // null in Play Mode
            var quest = scene.GetRootGameObjects()
                .SelectMany(go => go.GetComponentsInChildren<Component>(true))
                .Where(c => c != null && IsQuest(c.GetType().Assembly.GetName().Name))
                .ToList();
            // A component another one requires goes after it; each pass removes whatever nothing left still needs.
            while (quest.Count > 0)
            {
                var free = quest.Where(c => !RequiredByOthers(c)).ToList();
                if (free.Count == 0) { Debug.LogWarning($"Hypa: could not strip {quest.Count} Quest components from {scene.name}"); break; }
                foreach (var c in free) UnityEngine.Object.DestroyImmediate(c);
                quest.RemoveAll(c => c == null);
            }
        }

        internal static bool IsQuest(string assembly) =>
            assembly.StartsWith("Oculus.", StringComparison.OrdinalIgnoreCase)
            || assembly.StartsWith("Meta.XR", StringComparison.OrdinalIgnoreCase)
            || assembly.Contains(".Quest");

        static bool RequiredByOthers(Component c) =>
            c.GetComponents<Component>().Any(o => o != null && o != c && o.GetType()
                .GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>()
                .Any(r => new[] { r.m_Type0, r.m_Type1, r.m_Type2 }.Any(t => t != null && t.IsInstanceOfType(c))));
    }

    /// <summary>
    /// Flat builds: leaves the Meta XR and Hypa Quest assemblies out of the player. MR Utility Kit starts itself on load
    /// and calls a native library that only exists on Quest and Android; in a browser that call crashes the page.
    /// </summary>
    sealed class DropQuestAssembliesFromFlatBuilds : IFilterBuildAssemblies
    {
        public int callbackOrder => 0;

        public string[] OnFilterAssemblies(BuildOptions options, string[] assemblies) => HypaBuild.Flat
            ? assemblies.Where(a => !StripQuestFromFlatScenes.IsQuest(Path.GetFileNameWithoutExtension(a))).ToArray()
            : assemblies;
    }

    /// <summary>
    /// Android phone build: removes what the Quest manifest (Assets/Plugins/Android) and Meta's own build step add
    /// (VR launcher category, head tracking, Horizon OS entries), so the APK installs and opens as a normal phone app.
    /// Runs after Meta's manifest patch (callbackOrder 99999).
    /// </summary>
    sealed class FlatAndroidManifest : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        public int callbackOrder => int.MaxValue;

        public void OnPostGenerateGradleAndroidProject(string unityLibrary)
        {
            if (!HypaBuild.Flat) return;
            foreach (var path in new[] { Path.Combine(unityLibrary, "src/main/AndroidManifest.xml"), Path.Combine(unityLibrary, "../launcher/src/main/AndroidManifest.xml") })
            {
                if (!File.Exists(path)) continue;
                var doc = new XmlDocument();
                doc.Load(path);
                foreach (var node in doc.SelectNodes("//*").Cast<XmlElement>().Where(IsQuest).ToList()) node.ParentNode.RemoveChild(node);
                foreach (XmlElement activity in doc.GetElementsByTagName("activity")) activity.RemoveAttribute("excludeFromRecents", AndroidNs);
                doc.Save(path);
            }
        }

        static bool IsQuest(XmlElement e)
        {
            if (e.NamespaceURI.Contains("horizonos")) return true;
            var name = e.GetAttribute("name", AndroidNs);
            return name.StartsWith("com.oculus.") || name.StartsWith("oculus.") || name.StartsWith("horizonos.")
                || name.StartsWith("android.hardware.vr.");
        }
    }
}
