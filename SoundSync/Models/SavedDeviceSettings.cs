namespace SoundSync.Models
{
    public class SavedDeviceSettings
    {
        public string DeviceId { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
        /// <summary>Legacy volume field retained for older profile readers.</summary>
        public float Volume { get; set; } = 1.0f;

        /// <summary>Separate app gain; profiles written before this feature default to unity.</summary>
        public float SoftwareVolume { get; set; } = 1.0f;
        public int Delay { get; set; }
        public float Bass { get; set; }
        public float Mid { get; set; }
        public float Treble { get; set; }

        /// <summary>Rows the user chose to hide stay configured but out of the list.</summary>
        public bool IsHidden { get; set; }

        /// <summary>Keep this output's delay matched to the slowest mirrored output.</summary>
        public bool AutoAlignDelay { get; set; }

        /// <summary>Follow the default device's Windows volume.</summary>
        public bool SyncVolumeWithDefault { get; set; }

        /// <summary>Ratio captured when volume sync was switched on.</summary>
        public float VolumeRatioToDefault { get; set; } = 1.0f;
    }
}
