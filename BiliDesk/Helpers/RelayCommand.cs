using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace BiliDesk.Helpers;

public class RelayCommand : ICommand
{
    private readonly Action? _execute;
    private readonly Action<object?>? _executeParam;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action<object?> executeParam, Func<bool>? canExecute = null)
    {
        _executeParam = executeParam;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        if (_executeParam != null) _executeParam(parameter);
        else _execute?.Invoke();
    }
}

/// <summary>异步命令: 执行期间自动禁用, 防止重复点击</summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }
}