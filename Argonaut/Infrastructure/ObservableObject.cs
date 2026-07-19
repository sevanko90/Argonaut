using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Argonaut.Infrastructure;

/// <summary>
/// Minimal INotifyPropertyChanged base for the view models and settings objects - the
/// SetField helper used to be copy-pasted into each of them. Deliberately hand-rolled:
/// the app takes no MVVM-toolkit dependency.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        this.OnPropertyChanged(propertyName);
        return true;
    }
}
