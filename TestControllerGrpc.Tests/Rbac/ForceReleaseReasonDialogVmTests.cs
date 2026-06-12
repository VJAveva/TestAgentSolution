using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.Rbac;

public sealed class ForceReleaseReasonDialogVmTests
{
    private static ForceReleaseReasonDialogViewModel CreateVm()
    {
        // Use a minimal setup — the HTTP call won't fire in these unit tests
        var vm = new ForceReleaseReasonDialogViewModel(null!, null!, null!);
        vm.Initialize("pipeline-A", "ravi.kumar");
        return vm;
    }

    [Fact]
    public void CanSubmit_Should_BeFalse_When_ReasonTooShort()
    {
        var vm = CreateVm();
        vm.Reason = "short";
        vm.IsAffirmationChecked = true;

        Assert.False(vm.CanSubmit);
    }

    [Fact]
    public void CanSubmit_Should_BeFalse_When_CheckboxUnchecked()
    {
        var vm = CreateVm();
        vm.Reason = "This is a valid reason with enough characters.";
        vm.IsAffirmationChecked = false;

        Assert.False(vm.CanSubmit);
    }

    [Fact]
    public void CanSubmit_Should_BeTrue_When_BothConditionsMet()
    {
        var vm = CreateVm();
        vm.Reason = "This is a valid reason with enough characters.";
        vm.IsAffirmationChecked = true;

        Assert.True(vm.CanSubmit);
    }

    [Fact]
    public void CanSubmit_Should_BeFalse_When_ReasonExactly9Chars()
    {
        var vm = CreateVm();
        vm.Reason = "123456789"; // 9 chars
        vm.IsAffirmationChecked = true;

        Assert.False(vm.CanSubmit);
    }

    [Fact]
    public void CanSubmit_Should_BeTrue_When_ReasonExactly10Chars()
    {
        var vm = CreateVm();
        vm.Reason = "1234567890"; // 10 chars
        vm.IsAffirmationChecked = true;

        Assert.True(vm.CanSubmit);
    }

    [Fact]
    public void CharacterCounter_Should_ShowCorrectCount()
    {
        var vm = CreateVm();
        vm.Reason = "Hello";

        Assert.Equal("5 / 10 minimum", vm.CharacterCounter);
    }

    [Fact]
    public void CharacterCounter_Should_UpdateOnReasonChange()
    {
        var vm = CreateVm();
        vm.Reason = "This has 15 ch";

        Assert.Equal("14 / 10 minimum", vm.CharacterCounter);
    }

    [Fact]
    public void AffirmationLabel_Should_NameThePriorOwner()
    {
        var vm = CreateVm();

        Assert.Equal("I confirm that ravi.kumar's run will be cancelled", vm.AffirmationLabel);
    }

    [Fact]
    public void CanSubmit_Should_BeFalse_When_ReasonEmptyAndCheckboxChecked()
    {
        var vm = CreateVm();
        vm.Reason = "";
        vm.IsAffirmationChecked = true;

        Assert.False(vm.CanSubmit);
    }

    [Fact]
    public void CanSubmit_Should_TransitionCorrectly_When_ConditionsChangeSequentially()
    {
        var vm = CreateVm();

        // Start: both conditions unmet
        Assert.False(vm.CanSubmit);

        // Meet reason only
        vm.Reason = "A proper reason text.";
        Assert.False(vm.CanSubmit);

        // Meet checkbox
        vm.IsAffirmationChecked = true;
        Assert.True(vm.CanSubmit);

        // Uncheck
        vm.IsAffirmationChecked = false;
        Assert.False(vm.CanSubmit);

        // Re-check, then shorten reason
        vm.IsAffirmationChecked = true;
        vm.Reason = "Short";
        Assert.False(vm.CanSubmit);
    }
}
