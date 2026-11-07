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
        // ★ 2026-11-07 补: 同步路径上抛出的异常会沿 WPF 命令管线冒成未处理异常
        //   (全局处理器记日志 + 弹框)。命令体里自己 try/catch 的调用点不受影响;
        //   没包的(写的时候以为"这就是同步小操作")在这里兜一层, 记日志不弹框。
        try
        {
            if (_executeParam != null) _executeParam(parameter);
            else _execute?.Invoke();
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
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