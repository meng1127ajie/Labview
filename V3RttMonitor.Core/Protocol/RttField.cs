namespace V3RttMonitor.Core.Protocol;

public enum RttField
{
    Seq = 0,
    TimeMs,
    RunState,
    CalibrationStatus,
    CalibrationStep,
    CalibrationError,
    EncoderRaw,
    SpeedRpm,
    VBusV,
    IdA,
    IqA,
    VdMod,
    VqMod,
    EncoderOffset,
    LdUh,
    LqUh,
    PsiMwb,
    RsMohm,
    HfControlHz,
    HfInjectHz,
    HfCurrentAmplitudeA,
    HfEncoderMove,
    HfVoltageAmplitudeV,
}
