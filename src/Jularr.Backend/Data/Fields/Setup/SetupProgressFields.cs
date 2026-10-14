namespace Jularr.Backend.Data.Fields.Setup
{
    // Derived wizard state: completion and the checks before it.
    public static class SetupProgressFields
    {
        public static readonly BoolField IsCompleted = new();
        public static readonly StringArrayField Blockers = new() { ItemMaximumLength = 64 };
        public static readonly StringArrayField Warnings = new() { ItemMaximumLength = 64 };
        public static readonly StringField CheckKey = new() { MaximumLength = 64 };
        public static readonly BoolField IsBlocking = new();
    }
}
