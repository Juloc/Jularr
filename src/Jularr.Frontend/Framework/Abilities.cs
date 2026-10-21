namespace Jularr.Frontend.Framework
{
    // What pages offer, each as one permission in its modules.
    public static class Abilities
    {
        public static bool CanReadMedia(this UiText texts) => texts.Can("profile.media.read", "Library");
        public static bool CanManageMedia(this UiText texts) => texts.Can("media.manual.update", "Library");
        public static bool CanRequest(this UiText texts) => texts.Can("profile.requests.update", "Requests");
        public static bool CanManageRequests(this UiText texts) => texts.Can("requests.update", "Requests");
        public static bool CanManageWanted(this UiText texts) => texts.Can("wanted.update", "Requests");
        public static bool CanAcquire(this UiText texts) => texts.Can("wanted.update", "Requests", "Acquisition");
        public static bool CanConfigureAcquisition(this UiText texts) => texts.Can("acquisition.manage", "Acquisition");
        public static bool CanReadRequestRules(this UiText texts) => texts.Can("rules.read", "Requests");
        public static bool CanPlay(this UiText texts) => texts.Can("profile.playback", "Library", "Playback");
        public static bool CanRead(this UiText texts) => texts.Can("profile.media.read", "Library", "Reading");
        public static bool CanTranslate(this UiText texts) => texts.Can("profile.media.read", "Library", "Reading", "Translation");
        public static bool CanManageProfiles(this UiText texts) => texts.Can(null, "Profiles");
        public static bool CanCreateProfiles(this UiText texts) => texts.Can("session.profile.create", "Profiles");
        public static bool CanDeleteProfile(this UiText texts) => texts.Can("profile.self.delete", "Profiles");
        public static bool CanPlayGames(this UiText texts) => texts.Can("profile.playback", "Library", "Emulation");
        public static bool CanReadOwnProfile(this UiText texts) => texts.Can("profile.self.read");
        public static bool CanNotify(this UiText texts) => texts.Can("profile.notifications", "Notifications");
        public static bool CanCollect(this UiText texts) => texts.Can("profile.collections", "Library", "Collections");
        public static bool CanSeeCalendar(this UiText texts) => texts.Can("profile.media.read", "Library", "Calendar");
        public static bool CanSeeStatistics(this UiText texts) => texts.Can("profile.media.read", "Library", "Statistics");
        public static bool CanAdminister(this UiText texts) => texts.Can("instance.settings.read");
        public static bool CanDiagnose(this UiText texts) => texts.Can("instance.settings.read", "Diagnostics");
        public static bool CanUploadVolumes(this UiText texts) => texts.Can("media.manual.update", "Library", "Uploads");
        public static bool CanManageStorage(this UiText texts) => texts.Can("storage.update", "Library");
        public static bool CanEditMarkers(this UiText texts) => texts.Can("media.manual.update", "Library", "Playback");
        public static bool CanGroupUnits(this UiText texts) => texts.Can("media.manual.update", "Library", "UnitGroups");
        public static bool CanArrangeHome(this UiText texts) => texts.Can("profile.self.update", "Library", "HomeLayout");
        public static bool CanPreviewSeeking(this UiText texts) => texts.Can("profile.playback", "Library", "Playback", "Trickplay");
    }
}
