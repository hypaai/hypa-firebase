using HypaCore.Hybrid;
using UnityEngine;

namespace HypaCore
{
    /// <summary>Saved settings shared by every mode: target language and the last mode used.</summary>
    public static class HypaSettings
    {
        const string TargetKey = "hypa.targetLanguage", ModeKey = "hypa.lastMode", AiKey = "hypa.aiMode", StatsKey = "hypa.shareRouteStats";
        /// <summary>The language Lens translates into until the user picks another.</summary>
        public const string DefaultTarget = "English";
        // Once per install: English becomes the selected target (earlier builds defaulted to Annang). A later pick sticks.
        const string EnglishDefaultKey = "hypa.targetDefaultEnglish";

        /// <summary>A Hypa language label, e.g. "Yoruba". Default English.</summary>
        public static string TargetLanguage
        {
            get
            {
                if (PlayerPrefs.GetInt(EnglishDefaultKey, 0) == 0)
                {
                    PlayerPrefs.SetString(TargetKey, DefaultTarget);
                    PlayerPrefs.SetInt(EnglishDefaultKey, 1);
                    PlayerPrefs.Save();
                }
                return PlayerPrefs.GetString(TargetKey, DefaultTarget);
            }
            set { PlayerPrefs.SetString(TargetKey, value ?? DefaultTarget); PlayerPrefs.Save(); }
        }

        public static AiMode AiMode
        {
            get => (AiMode)Mathf.Clamp(PlayerPrefs.GetInt(AiKey, (int)AiMode.Hybrid), 0, 2);
            set { PlayerPrefs.SetInt(AiKey, (int)value); PlayerPrefs.Save(); }
        }

        public static bool ShareRouteStats
        {
            get => PlayerPrefs.GetInt(StatsKey, 1) == 1;
            set { PlayerPrefs.SetInt(StatsKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>Scene name of the last mode opened from the hand menu (see <see cref="HypaScenes"/>).</summary>
        public static string LastMode
        {
            get => PlayerPrefs.GetString(ModeKey, HypaScenes.Lens);
            set { PlayerPrefs.SetString(ModeKey, value); PlayerPrefs.Save(); }
        }
    }
}
