#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;

namespace HypaCore.EditorTools
{
    /// <summary>
    /// iOS builds: sets up the Xcode project for Apple's on-device model (Assets/Plugins/iOS/HypaFoundationModels.swift,
    /// docs/ios-on-device.md) so it builds without manual steps beyond signing. FoundationModels is weak-linked, so the app
    /// still opens on iOS 15 to 18; the Swift version is pinned; the microphone prompt text is always in Info.plist.
    /// Compiled only while iOS is the active target (tools/build.sh ios starts Unity that way).
    /// </summary>
    sealed class HypaIosBuild : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;
            var output = report.summary.outputPath;

            var path = PBXProject.GetPBXProjectPath(output);
            var project = new PBXProject();
            project.ReadFromFile(path);
            var framework = project.GetUnityFrameworkTargetGuid();
            project.AddFrameworkToProject(framework, "FoundationModels.framework", true);
            foreach (var target in new[] { framework, project.GetUnityMainTargetGuid() })
                project.SetBuildProperty(target, "SWIFT_VERSION", "5.0");
            project.WriteToFile(path);

            var plistPath = Path.Combine(output, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            if (!plist.root.values.ContainsKey("NSMicrophoneUsageDescription"))
            {
                var text = PlayerSettings.iOS.microphoneUsageDescription;
                plist.root.SetString("NSMicrophoneUsageDescription", string.IsNullOrEmpty(text) ? "Hypa listens to what you say so it can translate it." : text);
                plist.WriteToFile(plistPath);
            }
        }
    }
}
#endif
