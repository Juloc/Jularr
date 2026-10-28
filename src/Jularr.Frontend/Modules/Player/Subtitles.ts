// The second subtitle: the rules only, so they can be tested without a video.
export interface SubtitleOption {
    key: string;
    language?: string;
    forced: boolean;
}

// The cue line (counted from the bottom) of the second language: above the first, which usually takes two lines.
export const SecondSubtitleLine = -4;

// The track a translation starts from: the shown first subtitle when it is not already in the wanted language, else the first full track in another language.
export function pickTranslationSource(options: SubtitleOption[], primaryKey: string, target: string): SubtitleOption | undefined {
    const shown = options.find(option => option.key === primaryKey);
    if (shown && shown.language !== target) return shown;
    const others = options.filter(option => option.language !== target);
    return others.find(option => !option.forced) ?? others[0];
}

// The profile's second language: a full track before a forced one, never the track that is already the first and never the first one's language.
export function pickSecondSubtitle(options: SubtitleOption[], language: string | undefined, primaryKey: string, primaryLanguage: string | undefined): SubtitleOption | undefined {
    if (!language || language === primaryLanguage) return undefined;
    const matching = options.filter(option => option.key !== primaryKey && option.language === language);
    return matching.find(option => !option.forced) ?? matching[0];
}
