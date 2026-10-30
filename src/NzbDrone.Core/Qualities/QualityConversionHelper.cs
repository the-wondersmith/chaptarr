using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Qualities
{
    public static class QualityConversionHelper
    {
        public static Quality GetPlannedConversionTarget(Author author, QualityModel sourceQuality)
        {
            var profile = author?.GetQualityProfileForQuality(sourceQuality?.Quality);
            return GetPlannedConversionTarget(profile, sourceQuality);
        }

        public static QualityModel GetEffectiveQualityAfterPlannedConversion(Author author, QualityModel sourceQuality)
        {
            var profile = author?.GetQualityProfileForQuality(sourceQuality?.Quality);
            return GetEffectiveQualityAfterPlannedConversion(profile, sourceQuality);
        }

        public static QualityModel GetEffectiveQualityAfterPlannedConversion(QualityProfile profile, QualityModel sourceQuality)
        {
            var targetQuality = GetPlannedConversionTarget(profile, sourceQuality);
            if (targetQuality == null)
            {
                return sourceQuality ?? new QualityModel(Quality.Unknown);
            }

            return new QualityModel(targetQuality, sourceQuality?.Revision ?? new Revision());
        }

        public static Quality GetPlannedConversionTarget(QualityProfile profile, QualityModel sourceQuality)
        {
            if (profile == null)
            {
                return null;
            }

            var targetQualityId = GetConversionTargetQualityId(profile);
            if (!targetQualityId.HasValue)
            {
                return null;
            }

            Quality targetQuality;
            try
            {
                targetQuality = Quality.FindById(targetQualityId.Value);
            }
            catch
            {
                return null;
            }

            var source = sourceQuality?.Quality ?? Quality.Unknown;
            if (source.Id == targetQuality.Id)
            {
                return null;
            }

            if (profile.ProfileType == ProfileType.Audiobook &&
                targetQuality == Quality.M4B &&
                (source == Quality.UnknownAudio || QualityMediaTypeHelper.IsAudiobookQuality(source)))
            {
                return targetQuality;
            }

            return null;
        }

        /// <summary>
        /// Whether the profile is configured to convert audiobooks to M4B, regardless of the source
        /// quality. <see cref="GetPlannedConversionTarget(QualityProfile, QualityModel)"/> reports no
        /// target when the source already matches, which hides profiles that still want an M4B-only
        /// operation such as merging a chapterized release.
        /// </summary>
        public static bool TargetsM4bConversion(QualityProfile profile)
        {
            return profile != null &&
                   profile.ProfileType == ProfileType.Audiobook &&
                   GetConversionTargetQualityId(profile) == Quality.M4B.Id;
        }

        private static int? GetConversionTargetQualityId(QualityProfile profile)
        {
            if (profile.ConvertToQualityId.HasValue && profile.ConvertToQualityId.Value > 0)
            {
                return IsQualityAllowed(profile, profile.ConvertToQualityId.Value) ? profile.ConvertToQualityId.Value : null;
            }

            return null;
        }

        private static bool IsQualityAllowed(QualityProfile profile, int qualityId)
        {
            return profile?.Items?.Any(i => i.Allowed && i.GetQualities().Any(q => q.Id == qualityId)) == true;
        }
    }
}
