using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Work;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    public sealed partial class ProfilesModel
    {
        // The add-ons of a language, for every media type: release groups and words that are preferred (points) or blocked in releases of that language.
        private UiSection LanguageRules()
        {
            var language = RuleLanguage is { Length: 2 } code && WorkLanguages.Choices.Contains(code.ToLowerInvariant()) ? code.ToLowerInvariant() : null;
            var current = language is null ? null : _rules.FirstOrDefault(rule => rule.Language == language);
            var input = _ruleInput ?? new SaveLanguageRuleDataV1(current?.PreferredGroups ?? [], current?.BlockedGroups ?? [], current?.PreferredWords ?? [], current?.BlockedWords ?? []);
            var content = new List<IUiComponent> { new P(new Text("arr.languageRules.info")) };
            if (_rules.Count > 0)
            {
                content.Add(new Div([.. _rules.Select(rule => (IUiComponent)new A(new ValueText(WorkLanguages.Name(rule.Language))).Class("button").Attribute("href", Address() + (Address().Contains('?') ? "&" : "?") + "rule=" + rule.Language))]).Class("page-actions"));
            }

            content.Add(new Div([.. WorkLanguages.Choices.Select(code => (IUiComponent)new A(new ValueText(WorkLanguages.Name(code))).Class(code == language ? "button button--accent" : "button button--soft")
                .Attribute("href", Address() + (Address().Contains('?') ? "&" : "?") + "rule=" + code))]).Class("page-actions"));
            if (language is not null)
            {
                content.Add(new Form<SaveLanguageRuleDataV1>(fields =>
                {
                    fields.Multiline(item => item.PreferredGroups, "arr.languageRules.preferredGroups");
                    fields.Multiline(item => item.BlockedGroups, "arr.languageRules.blockedGroups");
                    fields.Multiline(item => item.PreferredWords, "arr.languageRules.preferredWords");
                    fields.Multiline(item => item.BlockedWords, "arr.languageRules.blockedWords");
                    fields.Submit("common.save");
                }, Address("LanguageRule") + (Address("LanguageRule").Contains('?') ? "&" : "?") + "rule=" + language, Token, input, _ruleInput is null ? null : FormError));
            }

            return new UiSection("arr.languageRules.title", content);
        }

        public async Task<IActionResult> OnPostLanguageRuleAsync(string? preferredGroups, string? blockedGroups, string? preferredWords, string? blockedWords)
        {
            static string[] Entries(string? text) => [.. (text ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            _ruleInput = new SaveLanguageRuleDataV1(Entries(preferredGroups), Entries(blockedGroups), Entries(preferredWords), Entries(blockedWords));
            await saveRule.ExecuteAsync<UpdateResult>(new LanguageRuleParametersV1((RuleLanguage ?? "").ToLowerInvariant()), _ruleInput, HttpContext.RequestAborted);
            return LocalRedirect(Address() + (Address().Contains('?') ? "&" : "?") + "rule=" + RuleLanguage?.ToLowerInvariant());
        }
    }
}
