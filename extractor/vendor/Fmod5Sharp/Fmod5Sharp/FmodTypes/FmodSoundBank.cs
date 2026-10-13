using System;
using System.Collections.Generic;

namespace Fmod5Sharp.FmodTypes
{
    public class FmodSoundBank : IDisposable
    {
        public FmodAudioHeader Header;
        public List<FmodSample> Samples;

        // CthExtract patch: a lazily loaded bank owns the stream its samples read from.
        private IDisposable? owner;

        internal FmodSoundBank(FmodAudioHeader header, List<FmodSample> samples, IDisposable? owner = null)
        {
            Header = header;
            Samples = samples;
            this.owner = owner;
            Samples.ForEach(s => s.MyBank = this);
        }

        /// <summary>Releases the stream of a lazily loaded bank (no-op for banks held in memory). CthExtract patch.</summary>
        public void Dispose()
        {
            owner?.Dispose();
            owner = null;
        }
    }
}
