using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeBindingValidationTests
{
    [Fact]
    public void ANotificationImmediatelyRejectsThePreviouslyValidatedBinding()
    {
        var validation = new NativeBindingValidation();
        Assert.False(validation.IsCurrent);
        validation.CompleteValidation(validation.Version); Assert.True(validation.IsCurrent);
        validation.NotifyChanged(); Assert.False(validation.IsCurrent);
        validation.CompleteValidation(validation.Version); Assert.True(validation.IsCurrent);
    }

    [Fact]
    public void AnOlderScanCannotClearANewerNotificationOrRevalidateAfterAnotherChange()
    {
        var validation = new NativeBindingValidation();
        validation.CompleteValidation(validation.Version);
        validation.NotifyChanged(); var olderScan = validation.Version;
        validation.NotifyChanged(); var currentScan = validation.Version;
        validation.CompleteValidation(olderScan); Assert.False(validation.IsCurrent);
        validation.CompleteValidation(currentScan); Assert.True(validation.IsCurrent);
        validation.NotifyChanged();
        validation.CompleteValidation(currentScan); Assert.False(validation.IsCurrent);
        validation.CompleteValidation(validation.Version); Assert.True(validation.IsCurrent);
    }
}
