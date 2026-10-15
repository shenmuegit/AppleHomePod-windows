namespace AirFlash.Core;

public sealed record PcmVolumeCalibration(bool MasterInPcm, string MixFormat, string DriverSha256,
    double MaxDb, int ResumeDeviceVolume);
