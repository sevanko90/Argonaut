using Avalonia.Controls;

namespace Argonaut.Features.Requests;

/// <summary>A failed send, in place of the response - see <see cref="RequestFailure"/>.</summary>
public partial class RequestFailureView : UserControl
{
    public RequestFailureView()
    {
        InitializeComponent();
    }
}
