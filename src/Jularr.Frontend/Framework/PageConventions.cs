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
            conventions.AddPageRoute("/Admin/Instance/Instance", "/Setup/Instance");
            conventions.AddPageRoute("/Admin/Acquisition/Acquisition", "/Setup/Acquisition");
            conventions.AddPageRoute("/Admin/Providers/Providers", "/Setup/Providers");
            conventions.AddPageRoute("/Admin/Profiles/Profiles", "/Setup/Profiles");
            conventions.AddPageRoute("/Admin/Takeover/Takeover", "/Setup/Takeover");
            conventions.AddPageRoute("/Admin/Backup/Backup", "/Setup/Backup");
            conventions.AuthorizePage("/Admin/Requests/Requests", ServiceApi.RequestsReadPolicyName);
            conventions.AuthorizePage("/Admin/Wanted/Wanted", ServiceApi.WantedReadPolicyName);
            conventions.AuthorizePage("/Admin/Acquisition/Acquisition", ServiceApi.AcquisitionManagePolicyName);
            conventions.AuthorizePage("/Admin/Profiles/Profiles", ServiceApi.AcquisitionManagePolicyName);
            conventions.AuthorizePage("/Admin/Sources/Sources", ServiceApi.AcquisitionManagePolicyName);
            conventions.AuthorizePage("/Admin/Works/Works", ServiceApi.MediaReadPolicyName);
            conventions.AuthorizePage("/Admin/Work/Work", ServiceApi.MediaReadPolicyName);
            conventions.AuthorizePage("/Admin/Matches/Matches", ServiceApi.MediaReadPolicyName);
            conventions.AuthorizePage("/Admin/Library/Library", ServiceApi.MediaReadPolicyName);
            conventions.AuthorizePage("/Admin/Rules/Rules", ServiceApi.RulesReadPolicyName);
            conventions.AuthorizePage("/Admin/Users/Users", ServiceApi.RulesReadPolicyName);
            conventions.AuthorizePage("/Admin/Localization/Localization", ServiceApi.LocalizationReadPolicyName);
            conventions.AuthorizePage("/Admin/Languages/Languages", ServiceApi.LocalizationReadPolicyName);
            conventions.AuthorizePage("/Admin/Accounts/Accounts", ServiceApi.AccountsReadPolicyName);
            conventions.AuthorizePage("/Admin/Providers/Providers", ServiceApi.ProvidersReadPolicyName);
            conventions.AuthorizePage("/Admin/Instance/Instance", ServiceApi.InstanceSettingsReadPolicyName);
            conventions.AuthorizePage("/Admin/Backup/Backup", ServiceApi.BackupReadPolicyName);
            conventions.AuthorizePage("/Admin/System/System", ServiceApi.InstanceSettingsReadPolicyName);
            conventions.AllowAnonymousToPage("/Account/Login/Login");
            conventions.AllowAnonymousToPage("/Setup/Start/Start");
            conventions.AllowAnonymousToPage("/Account/DeviceLink/DeviceLink");
            conventions.AllowAnonymousToPage("/Account/Register/Register");
            conventions.AllowAnonymousToPage("/Development/ShellPreview/ShellPreview");
            return conventions;
        }
    }
}
