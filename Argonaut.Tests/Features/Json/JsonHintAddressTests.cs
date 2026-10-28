using Argonaut.Engine.Settings;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json;

/// <summary>
/// A value hint's link in the JSON tree opens through the view model's launcher, and only for
/// the schemes a document may launch - see <see cref="HintAddresses"/>.
/// </summary>
public sealed class JsonHintAddressTests
{
    public static TheoryData<string> SafeAddresses => HintAddresses.Safe;

    public static TheoryData<string> UnsafeAddresses => HintAddresses.Unsafe;

    [Theory]
    [MemberData(nameof(SafeAddresses))]
    public void ASafeAddress_IsOpened(string address)
    {
        var launcher = new RecordingSystemLauncher();
        using var vm = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog(), launcher);

        vm.OpenAddress(new Uri(address));

        Assert.Equal([new Uri(address)], launcher.AddressesOpened);
    }

    [Theory]
    [MemberData(nameof(UnsafeAddresses))]
    public void AnUnsafeAddress_IsNotOpened(string address)
    {
        var launcher = new RecordingSystemLauncher();
        using var vm = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog(), launcher);

        vm.OpenAddress(new Uri(address));

        Assert.Empty(launcher.AddressesOpened);
    }
}
