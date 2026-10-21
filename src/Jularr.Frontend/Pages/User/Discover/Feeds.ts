// Which rankings of the filter apply to the chosen media types. A ranking lists the types it exists for (none: every type has it);
// no type chosen means every type is shown, so every ranking is.
export function shownFeeds(feedScopes: ReadonlyArray<ReadonlyArray<string> | undefined>, picked: readonly string[], every: readonly string[]): boolean[] {
    const wanted = picked.length > 0 ? picked : every;
    return feedScopes.map(scopes => !scopes || scopes.some(slug => wanted.includes(slug)));
}

// The group is worth showing only while a choice besides "all" is left.
export function showsFeedGroup(shown: readonly boolean[]): boolean {
    return shown.filter(Boolean).length > 1;
}
