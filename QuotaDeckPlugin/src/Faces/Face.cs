namespace Loupedeck.QuotaDeckPlugin
{
    using System;

    /// <summary>
    /// Everything one key face needs. Accent drives the hairline, the ring and the secondary text; the
    /// headline is the big number inside the ring; corner and label are the small texts top and bottom.
    /// </summary>
    public sealed record Face
    {
        /// <summary>Provider (or status) colour.</summary>
        public BitmapColor Accent { get; init; } = new BitmapColor(0, 200, 170);

        /// <summary>Ring fill 0..1. Null hides the ring.</summary>
        public Double? Progress { get; init; }

        /// <summary>Big text inside the ring, e.g. "73%".</summary>
        public String? Headline { get; init; }

        /// <summary>Small text just under the headline inside the ring, e.g. "wk 41%".</summary>
        public String? Secondary { get; init; }

        /// <summary>Small top-left text in the top band, e.g. "↻ 2h 05m".</summary>
        public String? Corner { get; init; }

        /// <summary>Bottom label, e.g. the provider name.</summary>
        public String? Label { get; init; }

        /// <summary>Brighter hairline and a border while the key is "doing something" (live faces, later phases).</summary>
        public Boolean Active { get; init; }
    }
}
