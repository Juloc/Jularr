using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Jularr.Frontend.Framework
{
    // Page policies and the setup routes of the Admin editors, shared by every host.
    public static class PageConventions
    {
        public static PageConventionCollection AddJularrPages(this PageConventionCollection conventions)
        {
            conventions.AuthorizeFolder("/");
            conventions.AuthorizeFolder("/Admin", ServiceApi.AdminPolicyName);
            foreach (var view in new[] { "Unmatched", "Files", "Naming", "Rename", "Versions" })
            {
                conventions.AddPageRoute("/Admin/Library/Library", "/Admin/Library/" + view);
            }

            conventions.AddPageRoute("/Admin/Acquisition/Acquisition", "/Admin/Acquisition/Downloads");
            conventions.AddPageRoute("/Admin/Acquisition/Acquisition", "/Admin/Acquisition/Subtitles");
            conventions.AddPageRoute("/Admin/Acquisition/Acquisition", "/Setup/Indexers");
            conventions.AddPageRoute("/Admin/Acquisition/Acquisition", "/Setup/Downloads");
            conventions.AddPageRoute("/Admin/DownloadStorage/DownloadStorage", "/Setup/DownloadStorage");
            conventions.AddPageRoute("/Admin/Providers/Providers", "/Setup/Providers");
            conventions.AddPageRoute("/Admin/Profiles/Profiles", "/Setup/Profiles");
            conventions.AddPageRoute("/Admin/Profile/Profile", "/Setup/Profiles/{id}");
            conventions.AddPageRoute("/Admin/Takeover/Takeover", "/Setup/Takeover");
            conventions.AddPageRoute("/Admin/Backup/Backup", "/Setup/Backup");
            conventions.AuthorizePage("/Admin/Requests/Requests", ServiceApi.RequestsReadPolicyName);
            conventions.AuthorizePage("/Admin/Wanted/Wanted", ServiceApi.WantedReadPolicyName);
            conventions.AuthorizePage("/Admin/Acquisition/Acquisition", ServiceApi.AcquisitionManagePolicyName);
            conventions.AuthorizePage("/Admin/DownloadStorage/DownloadStorage", ServiceApi.AcquisitionManagePolicyName);
            conventions.AuthorizePage("/Admin/Profiles/Profiles", ServiceApi.AcquisitionManagePolicyName);
            conventions.AuthorizePage("/Admin/Sources/Sources", ServiceApi.AcquisitionManagePolicyName);
            conventions.AuthorizePage("/Admin/Works/Works", ServiceApi.MediaReadPolicyName);
            conventions.AuthorizePage("/Admin/Work/Work", ServiceApi.MediaReadPolicyName);
            conventions.AuthorizePage("/Admin/WorkMarkers/WorkMarkers", ServiceApi.MediaReadPolicyName);
            conventions.AuthorizePage("/Admin/Matches/Matches", ServiceApi.MediaReadPolicyName);
            conventions.AuthorizePage("/Admin/Library/Library", ServiceApi.MediaReadPolicyName);
            conventions.AuthorizePage("/Admin/Rules/Rules", ServiceApi.RulesReadPolicyName);
            conventions.AuthorizePage("/Admin/Users/Users", ServiceApi.RulesReadPolicyName);
            conventions.AuthorizePage("/Admin/Localization/Localization", ServiceApi.LocalizationReadPolicyName);
            conventions.AuthorizePage("/Admin/Languages/Languages", ServiceApi.LocalizationReadPolicyName);
            conventions.AuthorizePage("/Admin/Accounts/Accounts", ServiceApi.AccountsReadPolicyName);
            conventions.AuthorizePage("/Admin/Providers/Providers", ServiceApi.ProvidersReadPolicyName);
            conventions.AuthorizePage("/Admin/Instance/Instance", ServiceApi.InstanceSettingsReadPolicyName);
            conventions.AuthorizePage("/Admin/Speech/Speech", ServiceApi.InstanceSettingsReadPolicyName);
            conventions.AuthorizePage("/Admin/Catalog/Catalog", ServiceApi.InstanceSettingsReadPolicyName);
            conventions.AuthorizePage("/Admin/Backup/Backup", ServiceApi.BackupReadPolicyName);
            conventions.AuthorizePage("/Admin/System/System", ServiceApi.InstanceSettingsReadPolicyName);
            conventions.AuthorizePage("/Admin/Jobs/Jobs", ServiceApi.InstanceSettingsReadPolicyName);
            conventions.AuthorizePage("/Admin/Transcoding/Transcoding", ServiceApi.InstanceSettingsReadPolicyName);
            conventions.AllowAnonymousToPage("/Account/Login/Login");
            conventions.AllowAnonymousToPage("/Setup/Start/Start");
            conventions.AllowAnonymousToPage("/Account/DeviceLink/DeviceLink");
            conventions.AllowAnonymousToPage("/Account/Register/Register");
            conventions.AllowAnonymousToPage("/Development/ShellPreview/ShellPreview");
            return conventions;
        }
    }
}
