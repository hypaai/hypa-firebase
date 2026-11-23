using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace HypaStories
{
    /// <summary>Local identity and appearance only. No credentials or online identity claims.</summary>
    [Serializable]
    public sealed class StoryProfile
    {
        public const int CurrentVersion = 2;

        public int version = CurrentVersion;
        public string localId;
        public string username = "";
        public string displayName = "";

        // Choices index into ProfileCatalog. Sliders run 0..1 with 0.5 the average.
        public int skin = 4, eyeColor, brows = 1, hairStyle, hairColor;
        public int outfit, outfitColor, shoes;
        public float frame = 0.5f, weight = 0.5f, muscle = 0.5f, height = 0.5f;
        public float headShape = 0.5f, jaw = 0.5f, chin = 0.5f, cheeks = 0.5f, noseWidth = 0.5f, noseLength = 0.5f, lips = 0.5f, eyeSize = 0.5f;

        // Version 1 fields, read once to migrate an older save.
        [SerializeField] int faceShape = -1, build = -1;

        public StoryProfile Copy() => JsonUtility.FromJson<StoryProfile>(JsonUtility.ToJson(this));

        public static string UsernameError(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length < 3 || value.Length > 20) return "Use 3–20 letters, numbers or underscores.";
            return Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_]*$") ? null : "Start with a letter. Use letters, numbers or underscores.";
        }

        /// <summary>Version 1 had 8 skin tones, 5 hair styles and three-step face and build presets.</summary>
        public void Migrate()
        {
            if (version >= CurrentVersion) return;
            skin = Mathf.RoundToInt(Mathf.Clamp(skin, 0, 7) * (ProfileCatalog.SkinTones.Length - 1) / 7f);
            hairStyle = ProfileCatalog.HairFromVersion1(hairStyle);
            if (faceShape >= 0) headShape = new[] { 0.2f, 0.5f, 0.8f }[Mathf.Clamp(faceShape, 0, 2)];
            if (build >= 0) weight = new[] { 0.25f, 0.5f, 0.75f }[Mathf.Clamp(build, 0, 2)];
            outfit = 0; outfitColor = 0;
            faceShape = build = -1;
            version = CurrentVersion;
        }

        public void Normalize()
        {
            username = (username ?? "").Trim().ToLowerInvariant();
            displayName = (displayName ?? "").Trim();
            if (displayName.Length > 24) displayName = displayName.Substring(0, 24);
            if (displayName.Length == 0) displayName = username;
            skin = Pick(skin, ProfileCatalog.SkinTones.Length); eyeColor = Pick(eyeColor, ProfileCatalog.EyeColors.Length);
            brows = Pick(brows, ProfileCatalog.Brows.Length); hairStyle = Pick(hairStyle, ProfileCatalog.HairStyles.Length);
            hairColor = Pick(hairColor, ProfileCatalog.HairColors.Length); outfit = Pick(outfit, ProfileCatalog.Outfits.Length);
            outfitColor = Pick(outfitColor, ProfileCatalog.OutfitColors.Length); shoes = Pick(shoes, ProfileCatalog.Shoes.Length);
            frame = Unit(frame); weight = Unit(weight); muscle = Unit(muscle); height = Unit(height);
            headShape = Unit(headShape); jaw = Unit(jaw); chin = Unit(chin); cheeks = Unit(cheeks);
            noseWidth = Unit(noseWidth); noseLength = Unit(noseLength); lips = Unit(lips); eyeSize = Unit(eyeSize);
        }

        static int Pick(int value, int count) => Mathf.Clamp(value, 0, Mathf.Max(0, count - 1));
        static float Unit(float value) => float.IsNaN(value) ? 0.5f : Mathf.Clamp01(value);
    }

    public static class StoryProfileStore
    {
        const string Key = "hypa.stories.local-profile.v1";
        public static StoryProfile Load()
        {
            if (!PlayerPrefs.HasKey(Key)) return null;
            try
            {
                var profile = JsonUtility.FromJson<StoryProfile>(PlayerPrefs.GetString(Key));
                if (profile == null || profile.version < 1 || profile.version > StoryProfile.CurrentVersion
                    || !Guid.TryParse(profile.localId, out _) || StoryProfile.UsernameError(profile.username) != null) return null;
                profile.Migrate();
                profile.Normalize();
                return profile;
            }
            catch (ArgumentException) { return null; }
        }

        public static void Save(StoryProfile profile)
        {
            var error = StoryProfile.UsernameError(profile.username);
            if (error != null) throw new ArgumentException(error);
            var saved = profile.Copy();
            saved.Migrate();
            saved.Normalize();
            if (!Guid.TryParse(saved.localId, out _)) saved.localId = Guid.NewGuid().ToString("D");
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(saved));
            PlayerPrefs.Save();
            profile.localId = saved.localId;
        }
    }
}
