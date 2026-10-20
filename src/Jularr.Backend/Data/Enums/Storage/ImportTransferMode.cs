namespace Jularr.Backend.Data.Enums.Storage
{
    // How an import places its files: a hardlink where the volume allows it and else a copy, a plain copy, or a move that leaves nothing behind.
    public enum ImportTransferMode : byte
    {
        Link = 1,
        Copy = 2,
        Move = 3
    }
}
