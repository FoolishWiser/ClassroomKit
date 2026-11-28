using System.Diagnostics;
using System.Windows.Threading;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 每 2 秒看一眼：现在是不是「上一节同科目、下一节还是它」的续用窗口，
/// ClassInX 的板书窗口有没有打开。同一份板书的同一个目标只提示一次；
/// 「稍后」10 分钟后再允许提示，「这次不用」本目标不再提示。
/// </summary>
public sealed class BoardReuseWatcher : IDisposable
{
    private readonly AppLog _log;
    private readonly Func<LessonSnapshot?> _snapshot;
    private readonly BoardArchive _archive;
    private readonly BoardReuseStore _store;
    private readonly Func<BoardSaveConfig> _config;
    private readonly System.Threading.Timer _timer;
    private BoardReuseWindow? _window;
    private long _lastHandle;
    private string? _promptedKey;
    private int _busy;

    public BoardReuseWatcher(AppLog log, Func<LessonSnapshot?> snapshot, BoardArchive archive,
        BoardReuseStore store, Func<BoardSaveConfig> config)
    {
        _log = log;
        _snapshot = snapshot;
        _archive = archive;
        _store = store;
        _config = config;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (_window is not null) return;
            if (!_config().ReusePrompt) return;
            if (ClassInBoardSaver.IsSaving) return;

            var board = ClassInBoardSaver.LocateBoardWindow();
            if (board is null)
            {
                _lastHandle = 0;
                _promptedKey = null;
                return;
            }

            if (board.Handle != _lastHandle)
            {
                _lastHandle = board.Handle;
                _promptedKey = null;
            }

            var now = DateTimeOffset.Now;
            var snapshot = _snapshot();
            if (snapshot is null) return;
            var target = BoardReusePlanner.FromSnapshot(snapshot, _archive, now);
            if (target is null) return;
            if (string.Equals(_promptedKey, target.Key, StringComparison.Ordinal)) return;
            if (!_store.Current.ShouldPrompt(target, now)) return;

            _promptedKey = target.Key;
            _log.Info($"续用提示：{target.Text}");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                // 只有窗口真的显示出来才记为「已提示」，否则老师没看到却再也等不到第二次
                if (!Show(target))
                {
                    _promptedKey = null;
                    return;
                }
                _store.Save(_store.Current.MarkPrompted(target));
            }));
        }
        catch (Exception ex)
        {
            _log.Info("续用提示巡检失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private bool Show(BoardReuseTarget target)
    {
        try
        {
            if (_window is not null) return false;
            var window = new BoardReuseWindow(target, Reuse, Snooze, Dismiss);
            window.Closed += (_, _) =>
            {
                _window = null;
                // 允许「稍后」到点后再次提示；是否重复由 BoardReuseStore 的 key 决定
                _promptedKey = null;
            };
            _window = window;
            window.Show();
            window.Activate();
            return true;
        }
        catch (Exception ex)
        {
            _window = null;
            _promptedKey = null;
            _log.Warn("续用提示窗打开失败：" + ex.Message);
            return false;
        }
    }

    private bool Reuse(BoardReuseTarget target)
    {
        var file = target.Record.PrimaryFile;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            _log.Warn($"续用失败：找不到板书文件（{file}）");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            _log.Info($"续用板书：{file}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"续用板书失败：{ex.Message}（{file}）");
            return false;
        }
    }

    private void Snooze(BoardReuseTarget target)
        => _store.Save(_store.Current.Snooze(DateTimeOffset.Now.AddMinutes(10)));

    private void Dismiss(BoardReuseTarget target)
        => _store.Save(_store.Current.Dismiss(target));

    public void Dispose()
    {
        _timer.Dispose();
        try { _window?.Close(); } catch { }
    }
}