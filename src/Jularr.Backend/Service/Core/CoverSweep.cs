namespace Jularr.Backend.Service.Core
{
    // Where the cover job's walk through the provider addresses stands: the poster and backdrop facts, then the season and episode images, each after
    // the last one looked at. It lives as long as the process; after a start the walk begins again at the first address.
    internal sealed class CoverSweep
    {
        private readonly object _gate = new();
        private bool _isUnits;
        private long _after;

        public (bool IsUnits, long After) Position
        {
            get
            {
                lock (_gate)
                {
                    return (_isUnits, _after);
                }
            }
            set
            {
                lock (_gate)
                {
                    (_isUnits, _after) = value;
                }
            }
        }
    }
}
