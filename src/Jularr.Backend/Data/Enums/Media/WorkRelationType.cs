namespace Jularr.Backend.Data.Enums.Media
{
    // What a related title is to a Work. Prequel, main story and source material are the views of sequel, side story or spin-off and adaptation from the other side.
    public enum WorkRelationType : byte
    {
        Sequel = 1,
        Prequel = 2,
        SideStory = 3,
        SpinOff = 4,
        MainStory = 5,
        Adaptation = 6,
        SourceMaterial = 7,
        Alternative = 8,
        SharedUniverse = 9,
        Summary = 10,
        Other = 11
    }
}
