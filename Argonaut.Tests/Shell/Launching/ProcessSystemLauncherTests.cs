#if !MAC_APP_STORE
using Argonaut.Shell.Launching;

namespace Argonaut.Tests.Shell.Launching;

public class ProcessSystemLauncherTests
{
    [Fact]
    public void ExplorerSelect_QuotesThePathAfterTheSwitch_NotTheWholeSwitch()
    {
        // Quoting "/select,C:\...\my file.json" as one argument makes Explorer ignore the switch
        // and open Documents, so the quotes must sit around the path alone.
        Assert.Equal(
            "/select,\"C:\\Users\\me\\Downloads\\my file.json\"",
            ProcessSystemLauncher.ExplorerSelectArguments(@"C:\Users\me\Downloads\my file.json"));
    }
}
#endif
