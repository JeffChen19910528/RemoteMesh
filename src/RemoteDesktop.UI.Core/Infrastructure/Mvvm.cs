using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RemoteDesktop.UI.Infrastructure;

/// <summary>Minimal property-change plumbing (no external MVVM package needed).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A command backed by a delegate. The async form disables itself while running, so a double click cannot start two.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _run;
    private readonly Func<object?, bool>? _canRun;
    private bool _running;

    public RelayCommand(Action<object?> run, Func<object?, bool>? canRun = null)
        : this(p => { run(p); return Task.CompletedTask; }, canRun) { }

    public RelayCommand(Func<object?, Task> run, Func<object?, bool>? canRun = null)
    {
        _run = run;
        _canRun = canRun;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canRun?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _run(parameter); }
        catch (Exception) { /* handlers report their own failures; a command must never crash the app */ }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
