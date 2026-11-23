using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetGuardStateStore
{
    public InternetCommandPayload? Load() => InternetVerifiedStateFile.Load(InternetGuardPaths.StatePath);

    public void SaveVerified(InternetCommandPayload payload)
        => InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, payload);
}