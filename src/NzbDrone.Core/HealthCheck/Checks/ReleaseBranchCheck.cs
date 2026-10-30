using System;
using System.Collections.Generic;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ConfigSavedEvent))]
    public class ReleaseBranchCheck : HealthCheckBase
    {
        // Branch names are not valid identifiers once they contain a '/', so this cannot be an enum.
        private static readonly HashSet<string> ReleaseBranches = new (StringComparer.OrdinalIgnoreCase)
        {
            // ToDo Enable master as valid once released
            //"master",
            "develop",
            "nightly",
            "fork/main"
        };

        private readonly IConfigFileProvider _configFileService;

        public ReleaseBranchCheck(IConfigFileProvider configFileService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _configFileService = configFileService;
        }

        public override HealthCheck Check()
        {
            if (!ReleaseBranches.Contains(_configFileService.Branch))
            {
                return new HealthCheck(GetType(), HealthCheckResult.Warning, string.Format(_localizationService.GetLocalizedString("ReleaseBranchCheckOfficialBranchMessage"), _configFileService.Branch), "#branch-is-not-a-valid-release-branch");
            }

            return new HealthCheck(GetType());
        }
    }
}
