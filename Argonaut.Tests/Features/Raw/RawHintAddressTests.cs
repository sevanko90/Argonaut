using Argonaut.Engine.Settings;
using Argonaut.Features.Raw;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Raw;

/// <summary>
/// A value hint's link in the raw view opens through the view model's launcher, and only for
/// the schemes a document may launch - see <see cref="HintAddresses"/>.
/// </summary>
public sealed class RawHintAddressTests
{
    public static TheoryData<string> SafeAddresses => HintAddresses.Safe;

    public static TheoryData<string> UnsafeAddresses => HintAddresses.Unsafe;

    [Theory]
    [MemberData(nameof(SafeAddresses))]
    public void ASafeAddress_IsOpened(string address)
    {
        var launcher = new RecordingSystemLauncher();
        using var vm = new RawViewModel(new RawViewSettings(), launcher: launcher);

        vm.OpenAddress(new Uri(address));

        Assert.Equal([new Uri(address)], launcher.AddressesOpened);
    }

    [Theory]
    [MemberData(nameof(UnsafeAddresses))]
    public void AnUnsafeAddress_IsNotOpened(string address)
    {
        var launcher = new RecordingSystemLauncher();
        using var vm = new RawViewModel(new RawViewSettings(), launcher: launcher);

        vm.OpenAddress(new Uri(address));

        Assert.Empty(launcher.AddressesOpened);
    }

    [Fact]
    public void WithoutALauncher_ASafeAddress_OpensNothing_AndDoesNotThrow()
    {
        using var vm = new RawViewModel(new RawViewSettings());

        vm.OpenAddress(new Uri("https://example.com/"));
    }
}
